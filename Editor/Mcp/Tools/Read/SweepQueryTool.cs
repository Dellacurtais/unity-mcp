using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class SweepQueryTool : IMcpTool
    {
        public string Name => "sweep_query";
        public string Description =>
            "Sweeps a volume along a direction (Physics.BoxCastAll/SphereCastAll/CapsuleCastAll), reports every collider hit. " +
            "Useful for 'what would I hit if I moved this rigidbody'. shape: 'box'/'sphere'/'capsule'. Same args as overlap_query plus direction + maxDistance.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
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
                    ["origin"] = SchemaHelpers.StrictVec3Schema,
                    ["radius"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                }, "origin", "radius"),
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
                ["hits"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "shape", "count", "hits" },
        };
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.SweepQuery(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }

        private static JObject Variant(string shape, JObject shapeProperties, params string[] requiredShapeProperties)
        {
            var properties = new JObject
            {
                ["shape"] = new JObject { ["type"] = "string", ["enum"] = new JArray { shape } },
                ["direction"] = SchemaHelpers.StrictVec3Schema,
                ["maxDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["layerMask"] = SchemaHelpers.LayerMaskSchema,
                ["hitTriggers"] = SchemaHelpers.TriggerInteractionSchema,
            };
            foreach (var property in shapeProperties.Properties()) properties[property.Name] = property.Value;
            var required = new JArray { "shape", "direction" };
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
