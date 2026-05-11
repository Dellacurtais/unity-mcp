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

        public ToolResult Execute(JObject args)
        {
            var json = new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["willChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying,
                ["applicationIsPlaying"] = Application.isPlaying,
                ["frameCount"] = EditorApplication.isPlaying ? Time.frameCount : 0,
                ["time"] = EditorApplication.isPlaying ? Time.time : 0f,
                ["timeScale"] = Time.timeScale,
            };

            string summary = EditorApplication.isPlaying
                ? $"playing (frame {Time.frameCount}, t={Time.time:F2}s, scale={Time.timeScale:F2}{(EditorApplication.isPaused ? ", paused" : "")})"
                : EditorApplication.isCompiling ? "edit mode — compiling"
                : (EditorApplication.isPlayingOrWillChangePlaymode ? "transitioning to play mode" : "edit mode");

            return ToolResult.Ok(summary, json);
        }
    }
}
