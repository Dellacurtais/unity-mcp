using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Timeline.Tools
{
    public sealed class PlayTimelineTool : IMcpTool
    {
        public string Name => "play_timeline";
        public string Description =>
            "Drives a PlayableDirector on a GameObject. action: play / stop / pause / resume / evaluate. With evaluate, pass `time` (seconds) to scrub the timeline to that point and tick once. Most actions only take effect in Play Mode; `evaluate` works in edit mode too.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "play", "stop", "pause", "resume", "evaluate" }, ["default"] = "play" },
                ["time"] = new JObject { ["type"] = "number" },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = TimelineAuthor.Play(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
