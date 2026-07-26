using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class SetPlayPausedTool : IMcpTool
    {
        public string Name => "set_play_paused";
        public string Description =>
            "Sets EditorApplication.isPaused. While paused, scripts don't tick — useful before screenshot_game_view to freeze the frame, or as a prerequisite for step_frame. No-op if state already matches. Transient — pause state is cleared on play exit.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["paused"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "paused" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => PlayToolSchemas.Paused;

        public ToolResult Execute(JObject args)
        {
            if (args["paused"] == null)
                return ToolResult.Error("BAD_REQUEST", "missing required boolean 'paused'");
            bool paused = (bool)args["paused"];

            if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("TRANSITIONING", "wait for the play-mode transition to settle");
            if (!EditorApplication.isPlaying)
                return ToolResult.Error("NOT_PLAYING",
                    "set_play_paused only has effect during play mode — call enter_play_mode first.");

            bool was = EditorApplication.isPaused;
            EditorApplication.isPaused = paused;
            return ToolResult.Ok(
                was == paused ? $"already {(paused ? "paused" : "running")} (no-op)"
                              : $"{(paused ? "paused" : "resumed")}",
                new JObject { ["paused"] = paused, ["wasAlreadySet"] = was == paused });
        }
    }
}
