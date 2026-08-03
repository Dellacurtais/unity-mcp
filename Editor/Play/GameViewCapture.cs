using System;
using System.Reflection;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Centralised game-view capture. Three paths:
    ///   - Play mode, no explicit camera: capture the completed Game View frame.
    ///     This includes every camera plus Screen Space - Overlay UI.
    ///   - Play mode, explicit camera: render that camera into a temporary
    ///     RenderTexture. Supported SRPs use StandardRequest;
    ///     Built-in/unsupported SRPs use Camera.Render.
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

        public static async Task<CaptureResult> CaptureCompositedPlayModeAsync(
            int width, int height, bool waitForEndOfFrame,
            string encoding, int jpgQuality)
        {
            var cancellationToken = ToolExecutionContext.Current?.CancellationToken ?? default;
            cancellationToken.ThrowIfCancellationRequested();

            if (waitForEndOfFrame)
            {
                try
                {
                    // ScreenCapture must execute inside the end-of-frame phase.
                    // Dispatching it in the next Editor update can lose late
                    // rendering, most visibly Screen Space - Overlay canvases.
                    return await EndOfFrameAwaiter.RunAtEndOfFrame(
                        () => CaptureCompletedGameViewFrame(width, height, encoding, jpgQuality),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[unity-mcp] End-of-frame Game View capture failed; trying the current Game View target: {e}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var reflected = await MainThreadDispatcher.RunAsync(() =>
                CaptureFromGameViewReflection(width, height, encoding, jpgQuality,
                    "Game view completed frame (all cameras + overlay UI)"),
                cancellationToken);
            if (reflected != null) return reflected;

            // On Unity versions where the internal GameView target moved, keep
            // correctness over the caller's latency preference: wait once and
            // use the public ScreenCapture API instead of returning a camera-only
            // image that silently omits overlay UI.
            return await EndOfFrameAwaiter.RunAtEndOfFrame(
                () => CaptureCompletedGameViewFrame(width, height, encoding, jpgQuality),
                cancellationToken);
        }

        public static async Task<CaptureResult> CapturePlayModeAsync(
            Camera camera, int width, int height, bool waitForEndOfFrame,
            string encoding, int jpgQuality)
        {
            if (ReferenceEquals(camera, null)) throw new ArgumentNullException(nameof(camera));

            var cancellationToken = ToolExecutionContext.Current?.CancellationToken ?? default;
            cancellationToken.ThrowIfCancellationRequested();
            if (waitForEndOfFrame)
                await EndOfFrameAwaiter.WaitOneEndOfFrame(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Render + readback must happen on the main thread.
            return await MainThreadDispatcher.RunAsync(() =>
            {
                if (camera == null) throw new InvalidOperationException("target camera was destroyed before capture");
                return RenderCameraToBytes(camera, width, height, encoding, jpgQuality,
                    sourceDescription: $"Game view camera '{camera.name}' (depth={camera.depth})");
            },
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
            string encoding, int jpgQuality, string sourceDescription = null)
        {
            // GameView is an internal EditorWindow type. Unity 6 keeps the
            // rendered frame in GameView.m_RenderTexture while older versions
            // expose PlayModeView.m_TargetTexture. Search the inheritance chain
            // because GetField does not return private fields from base types.
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

                var rt = FindGameViewRenderTexture(gvType, gv);
                if (rt == null) return null;

                // GameView's internal target is stored in device orientation;
                // drawing it in the Editor applies a vertical flip. Mirror that
                // presentation transform before encoding the MCP image.
                return ReadRenderTexture(rt, width, height, encoding, jpgQuality,
                    sourceDescription ?? "Game view (via reflection, edit-mode fallback)",
                    flipY: SystemInfo.graphicsUVStartsAtTop);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[unity-mcp] GameView reflection failed: {e.Message}");
                return null;
            }
        }

        private static RenderTexture FindGameViewRenderTexture(Type gameViewType, object gameView)
        {
            string[] candidates = { "m_RenderTexture", "m_TargetTexture" };
            foreach (var candidate in candidates)
            {
                for (var type = gameViewType; type != null; type = type.BaseType)
                {
                    var field = type.GetField(candidate,
                        BindingFlags.Instance | BindingFlags.NonPublic |
                        BindingFlags.Public | BindingFlags.DeclaredOnly);
                    if (field?.GetValue(gameView) is RenderTexture rt && rt.IsCreated())
                        return rt;
                }
            }
            return null;
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

        private static CaptureResult CaptureCompletedGameViewFrame(int width, int height,
            string encoding, int jpgQuality)
        {
            Texture2D source = null;
            RenderTexture resized = null;
            try
            {
                source = ScreenCapture.CaptureScreenshotAsTexture(1);
                if (source == null || source.width <= 0 || source.height <= 0)
                    throw new InvalidOperationException("ScreenCapture returned no Game View texture");

                if (source.width == width && source.height == height)
                {
                    var direct = Encode(source, width, height, encoding, jpgQuality,
                        "Game view completed frame (all cameras + overlay UI)");
                    direct.RenderPath = "screen-capture-end-of-frame";
                    return direct;
                }

                // ScreenCapture returns display-ready pixels. In Gamma projects
                // that texture is commonly linear/raw, while in Linear projects
                // it can be flagged as sRGB. A default temporary RT follows the
                // project color space instead of the source texture, so a plain
                // Blit can apply one extra gamma conversion and wash the image
                // out. Match the source encoding and explicitly control the
                // hardware sRGB write during the resize.
                resized = GetTemporaryMatchingColorEncoding(
                    source, width, height, 0, RenderTextureFormat.ARGB32);
                BlitPreservingColorEncoding(source, resized);
                var result = ReadRenderTexture(resized, width, height, encoding, jpgQuality,
                    "Game view completed frame (all cameras + overlay UI)");
                result.RenderPath = "screen-capture-end-of-frame";
                return result;
            }
            finally
            {
                if (source != null) UnityEngine.Object.DestroyImmediate(source);
                if (resized != null) RenderTexture.ReleaseTemporary(resized);
            }
        }

        private static CaptureResult ReadRenderTexture(RenderTexture rt, int targetW, int targetH,
            string encoding, int jpgQuality, string sourceDescription, bool flipY = false)
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
                if (resize || flipY)
                {
                    downsized = GetTemporaryMatchingColorEncoding(
                        rt, targetW, targetH, 0, rt.format);
                    if (flipY)
                        BlitPreservingColorEncoding(rt, downsized,
                            new Vector2(1f, -1f), new Vector2(0f, 1f));
                    else
                        BlitPreservingColorEncoding(rt, downsized);
                    sourceRt = downsized;
                }
                RenderTexture.active = sourceRt;
                // The Texture2D is only a readback/encoding container. Mirror
                // the RT flag so Unity does not reinterpret the copied pixels
                // if this path is extended or sampled before encoding.
                tex = new Texture2D(targetW, targetH, TextureFormat.RGB24, false,
                    linear: !sourceRt.sRGB);
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

        private static RenderTexture GetTemporaryMatchingColorEncoding(Texture source,
            int width, int height, int depthBuffer, RenderTextureFormat format)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var readWrite = source.isDataSRGB
                ? RenderTextureReadWrite.sRGB
                : RenderTextureReadWrite.Linear;
            return RenderTexture.GetTemporary(width, height, depthBuffer, format, readWrite);
        }

        private static void BlitPreservingColorEncoding(Texture source,
            RenderTexture destination)
        {
            BlitPreservingColorEncoding(source, destination, Vector2.one, Vector2.zero);
        }

        private static void BlitPreservingColorEncoding(Texture source,
            RenderTexture destination, Vector2 scale, Vector2 offset)
        {
            // Graphics.Blit depends on the global GL.sRGBWrite flag. Always
            // restore it: leaking this state would alter later Editor/game
            // rendering performed by unrelated code.
            var previousSrgbWrite = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = destination.sRGB;
                Graphics.Blit(source, destination, scale, offset);
            }
            finally
            {
                GL.sRGBWrite = previousSrgbWrite;
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
