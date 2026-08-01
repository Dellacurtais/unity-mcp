using System;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class ScreenshotGameViewTool : IMcpTool
    {
        public string Name => "screenshot_game_view";
        public string Description =>
            "Captures what the player sees in the final Game View as PNG/JPG, including all cameras, post-fx, IMGUI, and Screen Space - Overlay canvases. " +
            "In play mode, the default capture runs at end-of-frame; waitForEndOfFrame=false reads the latest completed Game View frame without waiting. " +
            "Use cameraInstanceId or cameraTag only when you intentionally want one camera rendered in isolation (overlay UI is then outside that camera render). " +
            "Edit mode reads UnityEditor.GameView's current target texture.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["width"] = new JObject { ["type"] = "integer", ["default"] = 1280, ["minimum"] = 64, ["maximum"] = 4096 },
                ["height"] = new JObject { ["type"] = "integer", ["default"] = 720, ["minimum"] = 64, ["maximum"] = 4096 },
                ["cameraInstanceId"] = new JObject { ["type"] = "integer" },
                ["cameraTag"] = new JObject { ["type"] = "string" },
                ["waitForEndOfFrame"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["encoding"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "png", "jpg" }, ["default"] = "png" },
                ["jpgQuality"] = new JObject { ["type"] = "integer", ["default"] = 80, ["minimum"] = 1, ["maximum"] = 100 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "screenshot_game_view requires async dispatch — framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            int width = Mathf.Clamp((int?)args["width"] ?? 1280, 64, 4096);
            int height = Mathf.Clamp((int?)args["height"] ?? 720, 64, 4096);
            int? cameraInstanceId = (int?)args["cameraInstanceId"];
            string cameraTag = (string)args["cameraTag"];
            bool waitForEndOfFrame = (bool?)args["waitForEndOfFrame"] ?? true;
            string encoding = ((string)args["encoding"] ?? "png").ToLowerInvariant();
            int jpgQuality = Mathf.Clamp((int?)args["jpgQuality"] ?? 80, 1, 100);

            bool isPlaying = await MainThreadDispatcher.RunAsync(() => EditorApplication.isPlaying);

            if (isPlaying)
            {
                bool explicitCamera = (cameraInstanceId.HasValue && cameraInstanceId.Value != 0)
                                      || !string.IsNullOrWhiteSpace(cameraTag);
                if (!explicitCamera)
                {
                    var composited = await GameViewCapture.CaptureCompositedPlayModeAsync(
                        width, height, waitForEndOfFrame, encoding, jpgQuality);
                    return BuildImageResult(composited);
                }

                var cam = await MainThreadDispatcher.RunAsync(() =>
                    GameViewCapture.PickGameViewCamera(cameraInstanceId, cameraTag));
                if (ReferenceEquals(cam, null))
                {
                    return ToolResult.Error("NO_CAMERA",
                        "no suitable camera found in play mode (no MainCamera-tagged camera, no enabled cameras present, and no explicit cameraInstanceId/cameraTag matched). Inspect get_active_cameras.");
                }

                var result = await GameViewCapture.CapturePlayModeAsync(cam, width, height,
                    waitForEndOfFrame, encoding, jpgQuality);
                return BuildImageResult(result);
            }
            else
            {
                // Edit-mode fallback via GameView reflection. Returns null on
                // reflection failure (internal API drift).
                var result = await MainThreadDispatcher.RunAsync(() =>
                    GameViewCapture.CaptureFromGameViewReflection(width, height, encoding, jpgQuality));
                if (result == null)
                {
                    return ToolResult.Error("UNAVAILABLE",
                        "could not capture Game view in edit mode — UnityEditor.GameView reflection failed (internal API drift on this Unity version). Workaround: enter_play_mode { paused: true } first, then screenshot_game_view.");
                }
                return BuildImageResult(result);
            }
        }

        private static ToolResult BuildImageResult(GameViewCapture.CaptureResult result)
        {
            if (result == null || result.Bytes == null || result.Bytes.Length == 0)
                return ToolResult.Error("CAPTURE_FAILED", "render returned 0 bytes.");

            string b64 = Convert.ToBase64String(result.Bytes);
            string summary = $"{result.SourceDescription} — {result.Width}×{result.Height}, " +
                             $"{result.Bytes.Length:N0} bytes ({result.MimeType}), renderPath={result.RenderPath}";
            return ToolResult.Image(b64, result.MimeType, summary);
        }
    }
}
