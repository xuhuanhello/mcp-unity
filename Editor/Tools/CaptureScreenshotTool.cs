using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using McpUnity.Unity;
using McpUnity.Runtime;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace McpUnity.Tools
{
    /// <summary>
    /// Captures what the Game view shows (or a single camera, or UI only), saves a full-resolution PNG
    /// under .unity-mcp/screenshots (kept across Editor restarts as review evidence) and optionally returns
    /// a downscaled preview. Without the preview, the response points the AI at the full-resolution file.
    /// </summary>
    public class CaptureScreenshotTool : McpToolBase
    {
        private const string OutputFolder = ".unity-mcp/screenshots";
        private const int DefaultMaxResolution = 1024;
        private const float EndOfFrameTimeoutSeconds = 2f;
        private const int JpegQuality = 85;

        private const string SourceGameView = "game_view";
        private const string SourceCamera = "camera";
        private const string SourceUi = "ui";

        private const string MethodScreenCapture = "screen_capture";
        private const string MethodCameraComposite = "camera_composite";

        private const string CompositeLimitation =
            "Rendered cameras + Screen Space - Overlay canvases directly; UI Toolkit panels are not included.";

        public CaptureScreenshotTool()
        {
            Name = "capture_screenshot";
            Description = "Captures the Game view (or a single camera, or UI only), saves a PNG and returns the image";
            IsAsync = true; // Play mode capture waits for the end of a rendered frame
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            string source = parameters["source"]?.ToString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(source))
            {
                source = SourceGameView;
            }
            string target = parameters["target"]?.ToString();
            bool includeImage = parameters["includeImage"]?.ToObject<bool?>() ?? true;
            if (!TryParseRegion(parameters["region"], out RectInt? region, out string regionError))
            {
                tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(regionError, "validation_error"));
                return;
            }
            // A region is for 1:1 inspection, so by default it is attached unscaled; full frames get a preview.
            int maxResolution = Math.Max(0, parameters["maxResolution"]?.ToObject<int?>()
                                            ?? (region.HasValue ? 0 : DefaultMaxResolution));
            string outputPath = BuildOutputPath(parameters["fileName"]?.ToString());
            var output = new OutputOptions(outputPath, includeImage, maxResolution, region);
            Vector2Int size = ScreenshotRenderer.GetGameViewSize();

            switch (source)
            {
                case SourceGameView:
                    CaptureGameView(output, size, tcs);
                    return;

                case SourceCamera:
                {
                    Camera camera = ResolveCamera(target, out string error);
                    if (camera == null)
                    {
                        tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(error, "not_found_error"));
                        return;
                    }
                    Texture2D texture = ScreenshotRenderer.RenderCamera(camera, size.x, size.y);
                    Complete(tcs, output, texture, SourceCamera, $"Camera '{camera.name}' only; overlay UI is not included.");
                    return;
                }

                case SourceUi:
                {
                    List<Canvas> canvases = ResolveCanvases(target, out string error);
                    if (canvases == null)
                    {
                        tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(error, "not_found_error"));
                        return;
                    }
                    Texture2D texture = ScreenshotRenderer.RenderCanvases(canvases, size.x, size.y, Color.black);
                    string names = string.Join(", ", canvases.Select(c => c.name));
                    Complete(tcs, output, texture, SourceUi, $"UI only (canvas: {names}) on a black background.");
                    return;
                }

                default:
                    tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                        $"Invalid source '{source}'. Valid values: '{SourceGameView}', '{SourceCamera}', '{SourceUi}'.",
                        "validation_error"));
                    return;
            }
        }

        /// <summary>
        /// Play mode: the real composited frame at end of frame. Otherwise, or if no frame renders in time,
        /// a synchronous render of cameras + overlay canvases, with the reason in the response note.
        /// </summary>
        private static void CaptureGameView(OutputOptions output, Vector2Int size, TaskCompletionSource<JObject> tcs)
        {
            if (!EditorApplication.isPlaying)
            {
                CompleteWithComposite(tcs, output, size, "Edit mode");
                return;
            }
            if (EditorApplication.isPaused)
            {
                CompleteWithComposite(tcs, output, size, "Play mode is paused, so no new frame renders");
                return;
            }

            RepaintGameViews();
            EditorApplication.QueuePlayerLoopUpdate();
            EndOfFrameCapturer.Begin(EndOfFrameTimeoutSeconds, texture =>
            {
                if (texture != null)
                {
                    Complete(tcs, output, texture, MethodScreenCapture, null);
                    return;
                }
                CompleteWithComposite(tcs, output, size,
                    $"No frame rendered within {EndOfFrameTimeoutSeconds:0.#}s (Editor unfocused/throttled, or play mode stopped)");
            });
        }

        private static void CompleteWithComposite(TaskCompletionSource<JObject> tcs, OutputOptions output, Vector2Int size, string reason)
        {
            try
            {
                Texture2D texture = ScreenshotRenderer.RenderComposite(size.x, size.y);
                Complete(tcs, output, texture, MethodCameraComposite, $"{reason}. {CompositeLimitation}");
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to render screenshot: {ex.Message}", "tool_execution_error"));
            }
        }

        /// <summary>
        /// Saves the full-resolution PNG (and the region crop, if requested), builds the response and
        /// destroys the textures. The message states exactly what was attached, so a downscaled preview is
        /// never mistaken for the real capture.
        /// </summary>
        private static void Complete(TaskCompletionSource<JObject> tcs, OutputOptions output, Texture2D texture, string method, string note)
        {
            Texture2D crop = null;
            Texture2D downscaled = null;
            try
            {
                string fullPath = SavePng(texture, output.Path);
                var response = new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["path"] = output.Path,
                    ["fullPath"] = fullPath,
                    ["width"] = texture.width,
                    ["height"] = texture.height,
                    ["method"] = method
                };
                if (!string.IsNullOrEmpty(note))
                {
                    response["note"] = note;
                }
                string message = $"Screenshot saved: {fullPath} ({texture.width}x{texture.height}, method: {method}).";

                // What gets inspected: the whole capture, or the requested region of it.
                Texture2D shown = texture;
                string shownName = "capture";
                string shownPath = fullPath;
                if (output.Region.HasValue)
                {
                    RectInt requested = output.Region.Value;
                    RectInt bounded = ClampRegion(requested, texture.width, texture.height);
                    if (bounded.width <= 0 || bounded.height <= 0)
                    {
                        tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                            $"Region (x={requested.x}, y={requested.y}, {requested.width}x{requested.height}) is outside the " +
                            $"{texture.width}x{texture.height} capture. Region coordinates are full-resolution pixels with the " +
                            $"origin at the top-left. The full capture was still saved: {fullPath}",
                            "validation_error"));
                        return;
                    }

                    crop = CropTopLeft(texture, bounded);
                    string regionPath = BuildRegionPath(output.Path, bounded);
                    shownPath = SavePng(crop, regionPath);
                    shown = crop;
                    shownName = "region";
                    response["region"] = new JObject
                    {
                        ["x"] = bounded.x,
                        ["y"] = bounded.y,
                        ["width"] = bounded.width,
                        ["height"] = bounded.height
                    };
                    response["regionPath"] = regionPath;
                    response["regionFullPath"] = shownPath;
                    message += $" Region x={bounded.x} y={bounded.y} {bounded.width}x{bounded.height} " +
                               $"(full-resolution pixels, origin top-left) saved: {shownPath}.";
                    if (!bounded.Equals(requested))
                    {
                        message += " The region was clamped to the capture bounds.";
                    }
                }

                if (output.IncludeImage)
                {
                    downscaled = ScreenshotRenderer.Downscale(shown, output.MaxResolution);
                    Texture2D image = downscaled != null ? downscaled : shown;
                    response["image"] = new JObject
                    {
                        ["data"] = Convert.ToBase64String(image.EncodeToJPG(JpegQuality)),
                        ["mimeType"] = "image/jpeg",
                        ["width"] = image.width,
                        ["height"] = image.height
                    };
                    if (downscaled != null)
                    {
                        float scale = (float)shown.width / image.width;
                        message += $" The attached image is only a {image.width}x{image.height} preview of the {shownName} " +
                                   $"(1 preview px = {scale:0.##} full-resolution px). Do not judge small text, 1px borders, " +
                                   $"alignment or other fine UI details from it: read {shownPath}";
                        message += output.Region.HasValue
                            ? "."
                            : ", or call again with a region (preview coordinates x the scale above) to see an area at 1:1.";
                    }
                    else
                    {
                        message += $" The attached image is the {shownName} at full 1:1 resolution.";
                    }
                }
                else
                {
                    message += $" No image attached (includeImage=false): read {shownPath} to inspect it.";
                }

                if (!string.IsNullOrEmpty(note))
                {
                    message += $" Note: {note}";
                }
                response["message"] = message;
                tcs.TrySetResult(response);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to save screenshot: {ex.Message}", "tool_execution_error"));
            }
            finally
            {
                if (downscaled != null)
                {
                    UnityEngine.Object.DestroyImmediate(downscaled);
                }
                if (crop != null)
                {
                    UnityEngine.Object.DestroyImmediate(crop);
                }
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static string SavePng(Texture2D texture, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, texture.EncodeToPNG());
            return Path.GetFullPath(path);
        }

        /// <summary>Reads an optional {x, y, width, height} object. Coordinates are checked against the capture later.</summary>
        private static bool TryParseRegion(JToken token, out RectInt? region, out string error)
        {
            region = null;
            error = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            int? x = token["x"]?.ToObject<int?>();
            int? y = token["y"]?.ToObject<int?>();
            int? width = token["width"]?.ToObject<int?>();
            int? height = token["height"]?.ToObject<int?>();
            if (x == null || y == null || width == null || height == null || width <= 0 || height <= 0)
            {
                error = "'region' needs integer x, y, width and height (width and height > 0), " +
                        "in full-resolution pixels with the origin at the top-left.";
                return false;
            }
            region = new RectInt(x.Value, y.Value, width.Value, height.Value);
            return true;
        }

        private static RectInt ClampRegion(RectInt region, int imageWidth, int imageHeight)
        {
            int xMin = Mathf.Clamp(region.x, 0, imageWidth);
            int yMin = Mathf.Clamp(region.y, 0, imageHeight);
            int xMax = Mathf.Clamp(region.x + region.width, 0, imageWidth);
            int yMax = Mathf.Clamp(region.y + region.height, 0, imageHeight);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>Copies a top-left-origin rectangle out of the texture (Unity textures are bottom-left origin).</summary>
        private static Texture2D CropTopLeft(Texture2D source, RectInt region)
        {
            int unityY = source.height - region.y - region.height;
            bool linear = !GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
            var crop = new Texture2D(region.width, region.height, TextureFormat.RGBA32, false, linear);
            crop.SetPixels(source.GetPixels(region.x, unityY, region.width, region.height));
            crop.Apply();
            return crop;
        }

        private static string BuildRegionPath(string capturePath, RectInt region)
        {
            string directory = Path.GetDirectoryName(capturePath);
            string name = Path.GetFileNameWithoutExtension(capturePath);
            return $"{directory}/{name}_region_{region.x}_{region.y}_{region.width}x{region.height}.png";
        }

        private static Camera ResolveCamera(string target, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(target))
            {
                Camera main = Camera.main;
                if (main == null)
                {
                    error = "No camera tagged MainCamera found. Pass 'target' with a camera GameObject name or path.";
                }
                return main;
            }

            GameObject go = MaterialToolUtils.FindGameObject(null, target);
            Camera camera = go != null ? go.GetComponent<Camera>() : null;
            if (camera == null)
            {
                error = go == null
                    ? $"GameObject '{target}' not found."
                    : $"GameObject '{target}' has no Camera component.";
            }
            return camera;
        }

        private static List<Canvas> ResolveCanvases(string target, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(target))
            {
                List<Canvas> overlays = ScreenshotRenderer.GetOverlayCanvases();
                if (overlays.Count == 0)
                {
                    error = "No active Screen Space - Overlay canvas found. Pass 'target' with a Canvas (or any UI object) name or path.";
                    return null;
                }
                return overlays;
            }

            GameObject go = MaterialToolUtils.FindGameObject(null, target);
            Canvas canvas = go != null ? go.GetComponentInParent<Canvas>(true) : null;
            if (canvas == null)
            {
                error = go == null
                    ? $"GameObject '{target}' not found."
                    : $"GameObject '{target}' is not under a Canvas.";
                return null;
            }

            canvas = canvas.rootCanvas;
            if (canvas.renderMode == RenderMode.WorldSpace)
            {
                error = $"Canvas '{canvas.name}' is World Space; capture it with source 'camera' or 'game_view' instead.";
                return null;
            }
            return new List<Canvas> { canvas };
        }

        /// <summary>Asks existing Game views to redraw without opening or focusing one (no layout changes).</summary>
        private static void RepaintGameViews()
        {
            Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null)
            {
                return;
            }
            foreach (UnityEngine.Object window in UnityEngine.Resources.FindObjectsOfTypeAll(gameViewType))
            {
                (window as EditorWindow)?.Repaint();
            }
        }

        private static string BuildOutputPath(string fileName)
        {
            string name = string.IsNullOrWhiteSpace(fileName)
                ? null
                : Path.GetFileNameWithoutExtension(Path.GetFileName(fileName.Trim()));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
            }
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            // Screenshots are review evidence: never overwrite an earlier one.
            string path = $"{OutputFolder}/{name}.png";
            for (int i = 1; File.Exists(path); i++)
            {
                path = $"{OutputFolder}/{name}_{i}.png";
            }
            return path;
        }

        private readonly struct OutputOptions
        {
            public readonly string Path;
            public readonly bool IncludeImage;
            public readonly int MaxResolution;
            public readonly RectInt? Region; // full-resolution pixels, origin top-left

            public OutputOptions(string path, bool includeImage, int maxResolution, RectInt? region)
            {
                Path = path;
                IncludeImage = includeImage;
                MaxResolution = maxResolution;
                Region = region;
            }
        }
    }
}
