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
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "step_frame requires async dispatch — framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            int count = Mathf.Clamp((int?)args["count"] ?? 1, 1, 600);

            var precheck = await MainThreadDispatcher.RunAsync<ToolResult>(() =>
            {
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

            // Step + one editor pump between calls so the player loop ticks
            // before the next Step. Calling Step() back-to-back in the same
            // dispatch frame seems to only advance once.
            for (int i = 0; i < count; i++)
            {
                await MainThreadDispatcher.RunAsync(() => EditorApplication.Step());
                await PlayModeWaiter.WaitOneUpdateTick();
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
