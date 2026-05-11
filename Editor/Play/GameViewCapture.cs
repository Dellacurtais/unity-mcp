using System;
using System.Reflection;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Centralised game-view capture. Two paths:
    ///   - Play mode: pick target Camera, render to a temporary RenderTexture
    ///     after one WaitForEndOfFrame so URP/HDRP post-fx is included.
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
        }

        public static async Task<CaptureResult> CapturePlayModeAsync(
            Camera camera, int width, int height, bool waitForEndOfFrame,
            string encoding, int jpgQuality)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            if (waitForEndOfFrame && Application.isPlaying)
            {
                await EndOfFrameAwaiter.WaitOneEndOfFrame();
            }

            // Render + readback must happen on the main thread.
            return await MainThreadDispatcher.RunAsync(() =>
                RenderCameraToBytes(camera, width, height, encoding, jpgQuality,
                    sourceDescription: $"Game view camera '{camera.name}' (depth={camera.depth})"));
        }

        /// Synchronous (no WaitForEndOfFrame). Used by screenshot_from_pov in
        /// both edit and play modes — the temp camera is not part of the player
        /// loop so we render it explicitly here.
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
                Debug.LogWarning($"[scene-analyzer] GameView reflection failed: {e.Message}");
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
            if (!string.IsNullOrEmpty(tag))
            {
                foreach (var c in Camera.allCameras)
                {
                    if (c != null && c.CompareTag(tag)) return c;
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
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                var prevTarget = camera.targetTexture;
                var prevActive = RenderTexture.active;
                try
                {
                    camera.targetTexture = rt;
                    camera.Render();
                }
                finally
                {
                    camera.targetTexture = prevTarget;
                }

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;

                return Encode(tex, width, height, encoding, jpgQuality, sourceDescription);
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static CaptureResult ReadRenderTexture(RenderTexture rt, int targetW, int targetH,
            string encoding, int jpgQuality, string sourceDescription)
        {
            Texture2D tex = null;
            RenderTexture downsized = null;
            try
            {
                var prevActive = RenderTexture.active;
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
                RenderTexture.active = prevActive;

                return Encode(tex, targetW, targetH, encoding, jpgQuality, sourceDescription);
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (downsized != null) RenderTexture.ReleaseTemporary(downsized);
            }
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
