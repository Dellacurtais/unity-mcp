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
            ["oneOf"] = new JArray
            {
                Variant("box", new JObject
                {
                    ["center"] = SchemaHelpers.StrictVec3Schema,
                    ["halfExtents"] = SchemaHelpers.NonNegativeVec3Schema,
                    ["rotation"] = SchemaHelpers.RotSchema,
                }, "center", "halfExtents"),
                Variant("sphere", new JObject
                {
                    ["center"] = SchemaHelpers.StrictVec3Schema,
                    ["radius"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                }, "center", "radius"),
                Variant("capsule", new JObject
                {
                    ["pointA"] = SchemaHelpers.StrictVec3Schema,
                    ["pointB"] = SchemaHelpers.StrictVec3Schema,
                    ["radius"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                }, "pointA", "pointB", "radius"),
            },
        };
        public bool IsWrite => false;
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["shape"] = new JObject { ["type"] = "string" },
                ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["colliders"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "shape", "count", "colliders" },
        };
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.OverlapQuery(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }

        private static JObject Variant(string shape, JObject shapeProperties, params string[] requiredShapeProperties)
        {
            var properties = new JObject
            {
                ["shape"] = new JObject { ["type"] = "string", ["enum"] = new JArray { shape } },
                ["layerMask"] = SchemaHelpers.LayerMaskSchema,
                ["hitTriggers"] = SchemaHelpers.TriggerInteractionSchema,
            };
            foreach (var property in shapeProperties.Properties()) properties[property.Name] = property.Value;
            var required = new JArray { "shape" };
            foreach (var property in requiredShapeProperties) required.Add(property);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }
    }
}
