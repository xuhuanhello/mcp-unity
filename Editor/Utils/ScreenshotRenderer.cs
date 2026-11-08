using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace McpUnity.Utils
{
    /// <summary>
    /// Synchronous screenshot rendering. Unlike ScreenCapture it does not need the Game view to draw a
    /// frame, so it works in Edit mode, while paused, and while the Editor is unfocused/throttled.
    /// Every temporary change (camera target, canvas render mode) is restored in a finally block.
    /// </summary>
    internal static class ScreenshotRenderer
    {
        // Far from any scene content so a temporary UI camera only sees the canvas placed in front of it.
        private static readonly Vector3 UiCameraOrigin = new Vector3(0f, 0f, -10000f);
        private const float UiCameraSpacing = 100f; // > an orthographic camera's view width (2 * 5 * aspect)
        private const float UiPlaneDistance = 10f;
        private const float UiCameraFarClip = 1000f;

        /// <summary>Main Game view size in pixels, falling back to the screen size.</summary>
        public static Vector2Int GetGameViewSize()
        {
            Vector2 size = Handles.GetMainGameViewSize();
            int width = (int)size.x;
            int height = (int)size.y;
            if (width <= 0 || height <= 0)
            {
                width = Screen.width;
                height = Screen.height;
            }
            return new Vector2Int(Mathf.Max(1, width), Mathf.Max(1, height));
        }

        /// <summary>
        /// Approximates the Game view: every enabled Display 1 camera by depth, then every
        /// Screen Space - Overlay canvas by sorting order. UI Toolkit panels are not included.
        /// </summary>
        public static Texture2D RenderComposite(int width, int height)
        {
            List<Canvas> overlays = GetOverlayCanvases();
            return RenderToTexture(width, height, Color.black, (rt, undo) =>
            {
                foreach (Camera camera in GetScreenCameras())
                {
                    RenderCameraInto(camera, rt, undo);
                }
                RenderCanvasesInto(overlays, rt, undo);
            });
        }

        /// <summary>Renders a single camera (even a disabled one). Overlay UI is not included.</summary>
        public static Texture2D RenderCamera(Camera camera, int width, int height)
        {
            return RenderToTexture(width, height, Color.black, (rt, undo) => RenderCameraInto(camera, rt, undo));
        }

        /// <summary>Renders only the given canvases, in order, over a solid background.</summary>
        public static Texture2D RenderCanvases(IReadOnlyList<Canvas> canvases, int width, int height, Color background)
        {
            return RenderToTexture(width, height, background, (rt, undo) => RenderCanvasesInto(canvases, rt, undo));
        }

        public static List<Canvas> GetOverlayCanvases()
        {
            return UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .Where(c => c.isRootCanvas && c.isActiveAndEnabled && c.renderMode == RenderMode.ScreenSpaceOverlay)
                .OrderBy(c => c.sortingOrder)
                .ToList();
        }

        /// <summary>
        /// Returns a copy whose longest edge is at most <paramref name="maxEdge"/>, or null when the
        /// source is already small enough. Caller destroys the returned texture.
        /// </summary>
        public static Texture2D Downscale(Texture2D source, int maxEdge)
        {
            if (maxEdge <= 0 || Mathf.Max(source.width, source.height) <= maxEdge)
            {
                return null;
            }

            float scale = (float)maxEdge / Mathf.Max(source.width, source.height);
            int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            // Match the RT's read/write mode to the source: in a Linear project a Default (sRGB) RT would
            // encode a linear-flagged capture on store without a matching decode and wash it out.
            bool sourceIsSrgb = GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
            RenderTextureReadWrite readWrite = sourceIsSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, readWrite);
            rt.filterMode = FilterMode.Bilinear;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = new Texture2D(width, height, TextureFormat.RGBA32, false, !sourceIsSrgb);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static IEnumerable<Camera> GetScreenCameras()
        {
            return Camera.allCameras
                .Where(c => c.targetTexture == null && c.targetDisplay == 0 && c.cameraType == CameraType.Game)
                .OrderBy(c => c.depth);
        }

        /// <summary>
        /// Clears a temporary RT, lets <paramref name="draw"/> render into it, reads it back, and only then
        /// runs the undo actions <paramref name="draw"/> registered. Undoing earlier (e.g. switching a canvas
        /// back to Overlay right after Camera.Render) swaps the UI geometry out before the queued draw runs on
        /// the render thread and leaves the capture empty; ReadPixels waits for that draw to finish.
        /// </summary>
        private static Texture2D RenderToTexture(int width, int height, Color background, Action<RenderTexture, List<Action>> draw)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var undo = new List<Action>();
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, background);
                draw(rt, undo);

                RenderTexture.active = rt;
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture;
            }
            finally
            {
                // Deactivate the RT before undoing: destroying a camera that targets the active RT logs
                // "Releasing render texture that is set to be RenderTexture.active!".
                RenderTexture.active = previousActive;
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        undo[i]();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[MCP Unity] Failed to restore state after screenshot: {ex.Message}");
                    }
                }
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static void RenderCameraInto(Camera camera, RenderTexture rt, List<Action> undo)
        {
            RenderTexture previousTarget = camera.targetTexture;
            undo.Add(() => camera.targetTexture = previousTarget);
            camera.targetTexture = rt;
            camera.Render();
        }

        private static void RenderCanvasesInto(IReadOnlyList<Canvas> canvases, RenderTexture rt, List<Action> undo)
        {
            for (int i = 0; i < canvases.Count; i++)
            {
                RenderCanvasInto(canvases[i], i, rt, undo);
            }
        }

        /// <summary>
        /// Attaches the canvas to a hidden camera (Screen Space - Camera) and renders it into the texture.
        /// The canvas stays attached until read-back, so each one gets its own camera spot (by
        /// <paramref name="slot"/>) to keep earlier canvases out of later cameras' views.
        /// </summary>
        private static void RenderCanvasInto(Canvas canvas, int slot, RenderTexture rt, List<Action> undo)
        {
            RenderMode previousMode = canvas.renderMode;
            Camera previousCamera = canvas.worldCamera;
            float previousPlaneDistance = canvas.planeDistance;
            var cameraObject = new GameObject("__McpUiCaptureCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = cameraObject.AddComponent<Camera>();
            undo.Add(() =>
            {
                canvas.renderMode = previousMode;
                canvas.worldCamera = previousCamera;
                canvas.planeDistance = previousPlaneDistance;
                Canvas.ForceUpdateCanvases();
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(cameraObject);
            });

            camera.enabled = false;
            camera.orthographic = true;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = UiCameraFarClip;
            camera.transform.position = UiCameraOrigin + Vector3.right * (UiCameraSpacing * slot);
            camera.clearFlags = CameraClearFlags.Nothing; // draw over what is already in the texture
            camera.targetTexture = rt;

            canvas.planeDistance = UiPlaneDistance;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            Canvas.ForceUpdateCanvases();
            camera.Render();
        }
    }
}
