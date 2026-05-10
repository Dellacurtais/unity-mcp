using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Timeline.Tools
{
    public sealed class AddTimelineClipTool : IMcpTool
    {
        public string Name => "add_timeline_clip";
        public string Description =>
            "Adds a clip to a named track. start/duration in seconds (timeline time). " +
            "sourceGuidOrPath optionally plugs a source asset into the clip's PlayableAsset (AnimationClip for AnimationTrack, AudioClip for AudioTrack, etc.). " +
            "name overrides the clip's displayName.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["timelineGuidOrPath"] = new JObject { ["type"] = "string" },
                ["timelineInstanceId"] = new JObject { ["type"] = "integer" },
                ["track"] = new JObject { ["type"] = "string" },
                ["start"] = new JObject { ["type"] = "number" },
                ["duration"] = new JObject { ["type"] = "number" },
                ["sourceGuidOrPath"] = new JObject { ["type"] = "string" },
                ["name"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "track" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = TimelineAuthor.AddClip(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
