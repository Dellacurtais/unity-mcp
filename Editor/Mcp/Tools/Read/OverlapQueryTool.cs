using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class OverlapQueryTool : IMcpTool
    {
        public string Name => "overlap_query";
        public string Description =>
            "Lists colliders inside a volume (Physics.OverlapBox/Sphere/Capsule). " +
            "shape: 'box' (needs center, halfExtents, optional rotation), 'sphere' (center, radius), 'capsule' (pointA, pointB, radius). " +
            "Optional layerMask + hitTriggers.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["shape"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule" } },
                ["center"] = new JObject(),
                ["halfExtents"] = new JObject(),
                ["rotation"] = new JObject(),
                ["radius"] = new JObject { ["type"] = "number" },
                ["pointA"] = new JObject(),
                ["pointB"] = new JObject(),
                ["layerMask"] = new JObject(),
                ["hitTriggers"] = new JObject(),
            },
            ["required"] = new JArray { "shape" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.OverlapQuery(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
