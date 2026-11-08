using System;
using System.Collections;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace McpUnity.Runtime
{
    /// <summary>
    /// Hidden play-mode helper: waits for the end of the next rendered frame, grabs the composited
    /// Game view (all cameras + all UI) with ScreenCapture, then destroys itself.
    /// If no frame renders before the timeout (game paused, Editor throttled while unfocused, play mode
    /// exited) the callback receives null so the caller can fall back to a synchronous render.
    /// Deliberately never pumps EditorApplication.Step: re-entering the PlayerLoop from a tool call is unsafe.
    /// Lives in a non-editor assembly because Unity refuses to AddComponent an editor-assembly
    /// MonoBehaviour; the assembly's UNITY_EDITOR define constraint keeps it out of player builds.
    /// </summary>
    public sealed class EndOfFrameCapturer : MonoBehaviour
    {
        private Action<Texture2D> _onComplete;
        private double _deadline;
        private bool _done;

        /// <summary>
        /// Starts a capture. The callback runs on the main thread exactly once and owns the texture
        /// (null on timeout or failure).
        /// </summary>
        public static void Begin(float timeoutSeconds, Action<Texture2D> onComplete)
        {
            var go = new GameObject("__McpEndOfFrameCapturer") { hideFlags = HideFlags.HideAndDontSave };
            var capturer = go.AddComponent<EndOfFrameCapturer>();
            if (capturer == null)
            {
                DestroyImmediate(go);
                throw new InvalidOperationException("Could not add EndOfFrameCapturer to a GameObject.");
            }

            capturer._onComplete = onComplete;
            capturer._deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
            // Editor update keeps ticking even when the PlayerLoop does not, so the timeout always fires.
            EditorApplication.update += capturer.CheckTimeout;
        }

        private IEnumerator Start()
        {
            yield return new WaitForEndOfFrame();

            Texture2D texture = null;
            try
            {
                texture = GrabScreen();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP Unity] Screen capture failed: {ex.Message}");
            }
            Complete(texture);
        }

        /// <summary>
        /// Copies the finished frame into a texture. Uses CaptureScreenshotIntoRenderTexture rather than
        /// CaptureScreenshotAsTexture: on Metal the latter logs "Ignoring depth surface load/store action as
        /// it is memoryless" to the console on its first call of every Play session.
        /// </summary>
        private static Texture2D GrabScreen()
        {
            int width = Screen.width;
            int height = Screen.height;
            // Default read/write matches the back buffer (sRGB in Linear projects, plain in Gamma ones), so the
            // copy keeps the frame's bytes. A Linear target would store decoded values and darken the image.
            RenderTexture grab = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(grab);
                RenderTexture.active = grab;
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                if (SystemInfo.graphicsUVStartsAtTop)
                {
                    // The grab is upside down on top-left-origin APIs (Metal, D3D, Vulkan). Flip on the CPU so
                    // no pixel value is resampled.
                    FlipRows(texture);
                }
                texture.Apply();
                return texture;
            }
            finally
            {
                // Deactivate before releasing, or Unity warns about releasing the active render texture.
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(grab);
            }
        }

        private static void FlipRows(Texture2D texture)
        {
            NativeArray<Color32> pixels = texture.GetRawTextureData<Color32>();
            int width = texture.width;
            var row = new Color32[width];
            for (int top = 0, bottom = texture.height - 1; top < bottom; top++, bottom--)
            {
                NativeArray<Color32>.Copy(pixels, top * width, row, 0, width);
                NativeArray<Color32>.Copy(pixels, bottom * width, pixels, top * width, width);
                NativeArray<Color32>.Copy(row, 0, pixels, bottom * width, width);
            }
        }

        private void CheckTimeout()
        {
            if (EditorApplication.timeSinceStartup >= _deadline)
            {
                Complete(null);
            }
        }

        private void OnDestroy()
        {
            Complete(null);
        }

        private void Complete(Texture2D texture)
        {
            if (_done)
            {
                if (texture != null)
                {
                    DestroyImmediate(texture);
                }
                return;
            }

            _done = true;
            EditorApplication.update -= CheckTimeout;
            try
            {
                _onComplete?.Invoke(texture);
            }
            finally
            {
                // `this == null` when something else already destroyed the capturer (e.g. leaving play mode).
                if (this != null)
                {
                    DestroyImmediate(gameObject);
                }
            }
        }
    }
}
