using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class StepFrameTool : IMcpTool
    {
        public string Name => "step_frame";
        public string Description =>
            "Advances the game by N frames while paused. Auto-pauses first if not already paused (and leaves it paused on return). " +
            "Each step calls EditorApplication.Step() once — the player loop runs exactly one update + render. " +
            "Use after enter_play_mode { paused: true } for deterministic frame-by-frame inspection.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["count"] = new JObject { ["type"] = "integer", ["default"] = 1, ["minimum"] = 1, ["maximum"] = 600 },
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["default"] = 10, ["minimum"] = 1, ["maximum"] = 60 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => PlayToolSchemas.Step;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "step_frame requires async dispatch — framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            int count = (int?)args["count"] ?? 1;
            double timeoutSeconds = (double?)args["timeoutSeconds"] ?? 10.0;
            if (count < 1 || count > 600) return ToolResult.Error("BAD_COUNT", "count must be between 1 and 600");
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 60)
                return ToolResult.Error("BAD_TIMEOUT", "timeoutSeconds must be finite and between 1 and 60");

            var precheck = await MainThreadDispatcher.RunAsync<ToolResult>(() =>
            {
                if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
                    return ToolResult.Error("TRANSITIONING", "wait for the play-mode transition to settle");
                if (!EditorApplication.isPlaying)
                    return ToolResult.Error("NOT_PLAYING",
                        "step_frame only works during play mode — call enter_play_mode first.");
                return null;
            });
            if (precheck != null) return precheck;

            int startFrame = await MainThreadDispatcher.RunAsync(() =>
            {
                // Auto-pause if not paused — Step() requires isPaused=true to advance.
                if (!EditorApplication.isPaused) EditorApplication.isPaused = true;
                return Time.frameCount;
            });

            for (int i = 0; i < count; i++)
            {
                var expectedFrame = startFrame + i + 1;
                await MainThreadDispatcher.RunAsync(() => EditorApplication.Step());
                if (!await PlayModeWaiter.WaitForFrameAtLeast(expectedFrame, timeoutSeconds))
                    return ToolResult.Error("STEP_TIMEOUT",
                        $"frameCount did not reach {expectedFrame} within {timeoutSeconds}s after EditorApplication.Step()");
            }

            int endFrame = await MainThreadDispatcher.RunAsync(() => Time.frameCount);
            return ToolResult.Ok(
                $"stepped {endFrame - startFrame} frame(s): {startFrame} -> {endFrame}",
                new JObject
                {
                    ["startFrame"] = startFrame,
                    ["endFrame"] = endFrame,
                    ["delta"] = endFrame - startFrame,
                });
        }
    }
}
