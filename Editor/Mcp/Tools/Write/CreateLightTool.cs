using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateLightTool : IMcpTool
    {
        public string Name => "create_light";
        public string Description =>
            "Creates a Light GameObject in the active scene. Type is one of Directional, Point, Spot, Area, Disc. Registers a single Undo step. NOTES: (1) Area and Disc are baking-only — they have no realtime effect, only contribute to lightmaps. (2) intensity scale differs across pipelines: Built-in uses an arbitrary 0..8 scale, URP uses lumens for Point/Spot (1000+ is normal), HDRP uses physical units. Check `get_project_meta` and pick a value that suits the active pipeline.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Directional", "Point", "Spot", "Area", "Disc" } },
                ["name"] = new JObject { ["type"] = "string" },
                ["intensity"] = new JObject { ["type"] = "number", ["default"] = 1.0 },
                ["range"] = new JObject { ["type"] = "number", ["default"] = 10.0 },
                ["color"] = SchemaHelpers.ColorSchema,
                ["shadows"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "None", "Hard", "Soft" } },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Realtime", "Mixed", "Baked" } },
                ["parent"] = new JObject { ["type"] = "integer" },
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

            return ToolResult.Ok(result.Summary, new JObject
            {
                ["instanceId"] = result.InstanceID,
            });
        }
    }
}
