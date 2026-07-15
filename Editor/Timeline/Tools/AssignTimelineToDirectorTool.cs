using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Timeline.Tools
{
    public sealed class AssignTimelineToDirectorTool : IMcpTool
    {
        public string Name => "assign_timeline_to_director";
        public string Description =>
            "Assigns a TimelineAsset to a PlayableDirector on a scene GameObject. Auto-adds a PlayableDirector component if missing.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["timelineGuidOrPath"] = new JObject { ["type"] = "string" },
                ["timelineInstanceId"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = TimelineAuthor.AssignToDirector(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
