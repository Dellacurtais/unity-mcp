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
                ["originOffset"] = SchemaHelpers.StrictVec3Schema,
                ["down"] = SchemaHelpers.StrictVec3Schema,
                ["maxDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 100 },
                ["layerMask"] = SchemaHelpers.LayerMaskSchema,
                ["hitTriggers"] = SchemaHelpers.TriggerInteractionSchema,
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["grounded"] = new JObject { ["type"] = "boolean" },
                ["hit"] = new JObject
                {
                    ["oneOf"] = new JArray
                    {
                        new JObject { ["type"] = "object" },
                        new JObject { ["type"] = "null" },
                    },
                },
            },
            ["required"] = new JArray { "target", "grounded", "hit" },
        };
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.CheckGround(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
