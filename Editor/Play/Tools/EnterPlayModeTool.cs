using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class EnterPlayModeTool : IMcpTool
    {
        public string Name => "enter_play_mode";
        public string Description =>
            "Enters Unity play mode and, when running, optionally waits until Time.frameCount reaches at least 1. " +
            "When paused is true, preserves frame 0 and does not wait for a frame; combine with step_frame for deterministic inspection. " +
            "Refuses if the editor is currently compiling (the imminent domain reload would kill the HTTP request). " +
            "Note: if 'Reload Domain on Play' is enabled in Project Settings, the underlying assembly reload returns HTTP 503 mid-call — retry once and the second call returns immediately.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["paused"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["waitForFirstFrame"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["default"] = 10, ["minimum"] = 1, ["maximum"] = 60 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => PlayToolSchemas.Enter;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "enter_play_mode requires async dispatch — framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            bool paused = (bool?)args["paused"] ?? false;
            bool waitForFirstFrame = (bool?)args["waitForFirstFrame"] ?? true;
            double timeoutSeconds = (double?)args["timeoutSeconds"] ?? 10.0;
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 60)
                return ToolResult.Error("BAD_TIMEOUT", "timeoutSeconds must be finite and between 1 and 60");

            // Preconditions on main thread.
            var precheck = await MainThreadDispatcher.RunAsync<ToolResult>(() =>
            {
                if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
                    return ToolResult.Error("TRANSITIONING",
                        "a play-mode transition is already in flight — call get_play_state, wait for it to settle, then retry.");
                if (EditorApplication.isPlaying)
                    return ToolResult.Ok("already in play mode (no-op)",
                        new JObject
                        {
                            ["entered"] = true, ["alreadyPlaying"] = true,
                            ["paused"] = EditorApplication.isPaused, ["frameCount"] = UnityEngine.Time.frameCount,
                            ["waitForFirstFrameRequested"] = waitForFirstFrame, ["waitedForFirstFrame"] = false,
                        });
                if (EditorApplication.isCompiling)
                    return ToolResult.Error("COMPILING",
                        "editor is compiling — wait for compile to finish before entering play mode (the upcoming domain reload would 503 this call).");
                return null;
            });
            if (precheck != null) return precheck;

            // Kick off the transition and pre-set paused so we land on frame 0
            // already paused (rather than running one frame then pausing).
            await MainThreadDispatcher.RunAsync(() =>
            {
                if (paused) EditorApplication.isPaused = true;
                EditorApplication.EnterPlaymode();
            });

            bool entered = await PlayModeWaiter.WaitFor(PlayModeStateChange.EnteredPlayMode, timeoutSeconds);
            if (!entered)
            {
                return ToolResult.Error("TIMEOUT",
                    $"editor did not enter play mode within {timeoutSeconds}s — possible domain reload in progress (retry) or scene errors blocked entry (check console).");
            }

            var waitedForFirstFrame = waitForFirstFrame && !paused;
            if (waitedForFirstFrame && !await PlayModeWaiter.WaitForFrameAtLeast(1, timeoutSeconds))
                return ToolResult.Error("FRAME_TIMEOUT",
                    $"play mode was entered but frameCount did not reach 1 within {timeoutSeconds}s");

            var summary = await MainThreadDispatcher.RunAsync(() =>
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("entered play mode");
                if (paused) sb.Append(" (paused)");
                if (waitedForFirstFrame) sb.Append($" — at frame {UnityEngine.Time.frameCount}");
                else if (paused && waitForFirstFrame) sb.Append(" — first-frame wait skipped to preserve paused frame 0");
                return sb.ToString();
            });

            return ToolResult.Ok(summary, new JObject
            {
                ["entered"] = true,
                ["paused"] = paused,
                ["frameCount"] = await MainThreadDispatcher.RunAsync(() => UnityEngine.Time.frameCount),
                ["waitForFirstFrameRequested"] = waitForFirstFrame,
                ["waitedForFirstFrame"] = waitedForFirstFrame,
            });
        }
    }
}
