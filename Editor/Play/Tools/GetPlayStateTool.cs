using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class GetPlayStateTool : IMcpTool
    {
        public string Name => "get_play_state";
        public string Description =>
            "Returns the editor's current play-mode state — whether playing, paused, compiling, mid-transition, plus runtime Time.frameCount/timeScale/time when in play mode. " +
            "Use as a precheck before enter_play_mode / step_frame / set_time_scale, or to detect that a previous async transition has settled.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;
        public JObject OutputSchema => PlayToolSchemas.State;

        public ToolResult Execute(JObject args)
        {
            var isPlaying = EditorApplication.isPlaying;
            var isPlayingOrWillChange = EditorApplication.isPlayingOrWillChangePlaymode;
            var isTransitioning = isPlaying != isPlayingOrWillChange;
            var transitionDirection = isTransitioning ? (isPlayingOrWillChange ? "entering" : "exiting") : null;
            var json = new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["isTransitioning"] = isTransitioning,
                ["transitionDirection"] = transitionDirection,
                ["applicationIsPlaying"] = Application.isPlaying,
                ["frameCount"] = EditorApplication.isPlaying ? Time.frameCount : 0,
                ["time"] = EditorApplication.isPlaying ? Time.time : 0f,
                ["timeScale"] = Time.timeScale,
            };

            string summary = isTransitioning
                ? $"transitioning: {transitionDirection} play mode"
                : isPlaying
                    ? $"playing (frame {Time.frameCount}, t={Time.time:F2}s, scale={Time.timeScale:F2}{(EditorApplication.isPaused ? ", paused" : "")})"
                    : EditorApplication.isCompiling ? "edit mode — compiling" : "edit mode";

            return ToolResult.Ok(summary, json);
        }
    }
}
