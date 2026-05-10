using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class CheckGroundTool : IMcpTool
    {
        public string Name => "check_ground";
        public string Description =>
            "Convenience: raycast straight down from a GameObject and report what's beneath — including the hit's tag and layer. " +
            "Answers 'is this object standing on the sidewalk / floor / road / something specific?'. " +
            "Pass `originOffset` to start the ray above the pivot (useful for pivots inside the mesh). " +
            "Filter via `layerMask` (e.g. 'Ground' or ['Ground','Walkable']) to ignore irrelevant colliders.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["originOffset"] = new JObject(),
                ["down"] = new JObject(),
                ["maxDistance"] = new JObject { ["type"] = "number", ["default"] = 100 },
                ["layerMask"] = new JObject(),
                ["hitTriggers"] = new JObject(),
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.CheckGround(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
