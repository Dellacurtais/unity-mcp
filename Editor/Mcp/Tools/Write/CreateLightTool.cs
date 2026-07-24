using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateLightTool : IMcpTool
    {
        public string Name => "create_light";
        public string Description =>
            "Creates a Unity 6000.3 Light with validated type-specific settings and one Undo step. Public types are Spot, Directional, Point, Rectangle, Disc, Pyramid, Box, and Tube; rendering support and physical intensity units depend on the active pipeline, so inspect get_render_pipeline_context first.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray(LightFactory.SupportedTypeNames), ["default"] = "Directional" },
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["intensity"] = new JObject { ["type"] = "number", ["minimum"] = 0.0, ["default"] = 1.0 },
                ["range"] = new JObject { ["type"] = "number", ["minimum"] = 0.0, ["default"] = 10.0, ["description"] = "Light influence range; directional lights ignore it." },
                ["spotAngle"] = new JObject { ["type"] = "number", ["minimum"] = 1.0, ["maximum"] = 179.0, ["description"] = "Outer cone angle for Spot and Pyramid lights." },
                ["innerSpotAngle"] = new JObject { ["type"] = "number", ["minimum"] = 0.0, ["maximum"] = 179.0, ["description"] = "Inner cone angle for Spot and Pyramid; must not exceed spotAngle." },
                ["areaSize"] = new JObject
                {
                    ["description"] = "Rectangle size [x,y], Disc radius in x, or Tube length in x.",
                    ["oneOf"] = new JArray
                    {
                        new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number", ["exclusiveMinimum"] = 0.0 }, ["minItems"] = 2, ["maxItems"] = 2 },
                        new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["x"] = new JObject { ["type"] = "number", ["exclusiveMinimum"] = 0.0 },
                                ["y"] = new JObject { ["type"] = "number", ["exclusiveMinimum"] = 0.0 },
                            },
                            ["required"] = new JArray { "x", "y" },
                            ["additionalProperties"] = false,
                        },
                    },
                },
                ["color"] = SchemaHelpers.ColorSchema,
                ["shadows"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "None", "Hard", "Soft" }, ["default"] = "Soft" },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Realtime", "Mixed", "Baked" } },
                ["parent"] = new JObject { ["type"] = "integer", ["description"] = "Existing parent GameObject instanceId." },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["rotation"] = SchemaHelpers.RotSchema,
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var result = LightFactory.Create(args);
            if (!result.Ok) return ToolResult.Error("OP_FAILED", result.Error);
            return ToolResult.Ok(result.Summary, result.Data);
        }
    }
}
