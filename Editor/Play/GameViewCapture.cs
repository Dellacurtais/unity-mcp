using System;
using System.Reflection;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Centralised game-view capture. Two paths:
    ///   - Play mode: pick a target Camera, optionally defer until the current
    ///     frame ends, then render into a temporary RenderTexture. Supported
    ///     SRPs use StandardRequest; Built-in/unsupported SRPs use Camera.Render.
    ///   - Edit mode: reflect over UnityEditor.GameView to grab its internal
    ///     m_TargetTexture. Post-fx may be absent (URP doesn't always render to
    ///     it outside play). If reflection fails, returns null and caller emits
    ///     a structured UNAVAILABLE error.
    public static class GameViewCapture
    {
        public sealed class CaptureResult
        {
            public byte[] Bytes;
            public string MimeType;
            public string SourceDescription;
            public int Width;
            public int Height;
            public string RenderPath;
        }

        public static async Task<CaptureResult> CapturePlayModeAsync(
            Camera camera, int width, int height, bool waitForEndOfFrame,
            string encoding, int jpgQuality)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            var cancellationToken = ToolExecutionContext.Current?.CancellationToken ?? default;
            cancellationToken.ThrowIfCancellationRequested();
            if (waitForEndOfFrame && Application.isPlaying)
            {
                await EndOfFrameAwaiter.WaitOneEndOfFrame(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();

            // Render + readback must happen on the main thread.
            return await MainThreadDispatcher.RunAsync(() =>
                RenderCameraToBytes(camera, width, height, encoding, jpgQuality,
                    sourceDescription: $"Game view camera '{camera.name}' (depth={camera.depth})"),
                cancellationToken);
        }

        /// Synchronous (no WaitForEndOfFrame). Used by screenshot_from_pov in
        /// both edit and play modes — the temp camera is not part of the player
        /// loop, so it is rendered explicitly through the effective pipeline.
        public static CaptureResult CaptureSync(Camera camera, int width, int height,
            string encoding, int jpgQuality, string sourceDescription)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            return RenderCameraToBytes(camera, width, height, encoding, jpgQuality, sourceDescription);
        }

        public static CaptureResult CaptureFromGameViewReflection(int width, int height,
            string encoding, int jpgQuality)
        {
            // GameView is an internal EditorWindow type. Field name "m_TargetTexture"
            // has been stable across Unity 2020+ but we tolerate failure since
            // internal API drift is the rule, not the exception.
            try
            {
                var asm = typeof(EditorWindow).Assembly;
                var gvType = asm.GetType("UnityEditor.GameView");
                if (gvType == null) return null;

                // Get an open Game view window. GetWindow() will open one if
                // none exists — which is what we want (otherwise capture is
                // impossible). Pass focus:false so the user's current window
                // focus isn't stolen.
                var gv = EditorWindow.GetWindow(gvType, utility: false, title: null, focus: false);
                if (gv == null) return null;

                var field = gvType.GetField("m_TargetTexture",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) return null;
                var rt = field.GetValue(gv) as RenderTexture;
                if (rt == null) return null;

                return ReadRenderTexture(rt, width, height, encoding, jpgQuality,
                    sourceDescription: "Game view (via reflection, edit-mode fallback)");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[unity-mcp] GameView reflection failed: {e.Message}");
                return null;
            }
        }

        public static Camera PickGameViewCamera(int? instanceId, string tag)
        {
            // Explicit param wins.
            if (instanceId.HasValue && instanceId.Value != 0)
            {
                foreach (var c in Camera.allCameras)
                {
                    if (c != null && c.GetInstanceID() == instanceId.Value) return c;
                }
                return null;
            }
            if (!string.IsNullOrWhiteSpace(tag))
            {
                foreach (var c in Camera.allCameras)
                {
                    if (c != null && string.Equals(c.tag, tag, StringComparison.Ordinal)) return c;
                }
                return null;
            }
            // Camera.main first; fall back to highest-depth enabled camera.
            if (Camera.main != null) return Camera.main;

            Camera best = null;
            float bestDepth = float.NegativeInfinity;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || !c.enabled) continue;
                if (c.depth > bestDepth)
                {
                    best = c;
                    bestDepth = c.depth;
                }
            }
            return best;
        }

        private static CaptureResult RenderCameraToBytes(Camera camera, int width, int height,
            string encoding, int jpgQuality, string sourceDescription)
        {
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                var renderPath = RenderCamera(camera, rt);

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                var result = Encode(tex, width, height, encoding, jpgQuality, sourceDescription);
                result.RenderPath = renderPath;
                return result;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static CaptureResult ReadRenderTexture(RenderTexture rt, int targetW, int targetH,
            string encoding, int jpgQuality, string sourceDescription)
        {
            var previousActive = RenderTexture.active;
            Texture2D tex = null;
            RenderTexture downsized = null;
            try
            {
                // Honor the requested dimensions even if the GameView texture
                // is a different size — Blit through a temp RT at target size
                // so the agent's `width/height` params remain authoritative.
                bool resize = rt.width != targetW || rt.height != targetH;
                RenderTexture sourceRt = rt;
                if (resize)
                {
                    downsized = RenderTexture.GetTemporary(targetW, targetH, 0, rt.format);
                    Graphics.Blit(rt, downsized);
                    sourceRt = downsized;
                }
                RenderTexture.active = sourceRt;
                tex = new Texture2D(targetW, targetH, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                tex.Apply();

                var result = Encode(tex, targetW, targetH, encoding, jpgQuality, sourceDescription);
                result.RenderPath = "game-view-target-reflection";
                return result;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (downsized != null) RenderTexture.ReleaseTemporary(downsized);
            }
        }

        private static string RenderCamera(Camera camera, RenderTexture destination)
        {
            var request = new RenderPipeline.StandardRequest { destination = destination };
            if (GraphicsSettings.currentRenderPipeline != null
                && RenderPipeline.SupportsRenderRequest(camera, request))
            {
                RenderPipeline.SubmitRenderRequest(camera, request);
                return "srp-standard-request";
            }

            camera.targetTexture = destination;
            camera.Render();
            return GraphicsSettings.currentRenderPipeline == null
                ? "built-in-camera-render"
                : "srp-camera-render-fallback";
        }

        private static CaptureResult Encode(Texture2D tex, int width, int height,
            string encoding, int jpgQuality, string sourceDescription)
        {
            byte[] bytes;
            string mime;
            if (string.Equals(encoding, "jpg", StringComparison.OrdinalIgnoreCase))
            {
                bytes = tex.EncodeToJPG(Mathf.Clamp(jpgQuality, 1, 100));
                mime = "image/jpeg";
            }
            else
            {
                bytes = tex.EncodeToPNG();
                mime = "image/png";
            }
            return new CaptureResult
            {
                Bytes = bytes,
                MimeType = mime,
                SourceDescription = sourceDescription,
                Width = width,
                Height = height,
            };
        }
    }
}
