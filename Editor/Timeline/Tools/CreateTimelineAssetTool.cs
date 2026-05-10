using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Timeline.Tools
{
    public sealed class CreateTimelineAssetTool : IMcpTool
    {
        public string Name => "create_timeline_asset";
        public string Description =>
            "Creates a new TimelineAsset (.playable) on disk. Returns the asset's instanceId. Follow with add_timeline_track / add_timeline_clip / assign_timeline_to_director.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["path"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "path" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = TimelineAuthor.CreateTimeline(args);
            return r.Ok ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID }) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
