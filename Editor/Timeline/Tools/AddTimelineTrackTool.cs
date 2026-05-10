using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Timeline.Tools
{
    public sealed class AddTimelineTrackTool : IMcpTool
    {
        public string Name => "add_timeline_track";
        public string Description =>
            "Adds a track to a Timeline. trackType: Animation, Audio, Activation, Control, Signal, Marker — or pass a fully-qualified custom TrackAsset type name. name is the visible track label.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["timelineGuidOrPath"] = new JObject { ["type"] = "string" },
                ["timelineInstanceId"] = new JObject { ["type"] = "integer" },
                ["trackType"] = new JObject { ["type"] = "string", ["default"] = "Animation" },
                ["name"] = new JObject { ["type"] = "string" },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = TimelineAuthor.AddTrack(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
