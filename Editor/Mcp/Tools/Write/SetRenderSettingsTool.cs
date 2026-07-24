using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetRenderSettingsTool : IMcpTool
    {
        public string Name => "set_render_settings";
        public string Description =>
            "Atomically validates and updates the active scene's RenderSettings with Undo when Unity exposes the backing object. Trilight uses sky/equator/ground colors; Flat uses ambientLight. Exponential fog uses density, while Linear fog uses start/end distances.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["minProperties"] = 1,
            ["properties"] = new JObject
            {
                ["ambientMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Skybox", "Trilight", "Flat", "Custom" } },
                ["ambientLight"] = SchemaHelpers.ColorSchema,
                ["ambientSkyColor"] = SchemaHelpers.ColorSchema,
                ["ambientEquatorColor"] = SchemaHelpers.ColorSchema,
                ["ambientGroundColor"] = SchemaHelpers.ColorSchema,
                ["ambientIntensity"] = new JObject { ["type"] = "number", ["minimum"] = 0.0 },
                ["fog"] = new JObject { ["type"] = "boolean" },
                ["fogColor"] = SchemaHelpers.ColorSchema,
                ["fogDensity"] = new JObject { ["type"] = "number", ["minimum"] = 0.0, ["description"] = "Used by Exponential and ExponentialSquared fog." },
                ["fogStartDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0.0, ["description"] = "Linear fog start distance." },
                ["fogEndDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0.0, ["description"] = "Linear fog end distance; must be >= start distance." },
                ["fogMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Linear", "Exponential", "ExponentialSquared" } },
                ["skybox"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Skybox Material path under Assets/ or GUID." },
                ["skyboxGuid"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Legacy alias for a skybox Material GUID." },
                ["reflectionIntensity"] = new JObject { ["type"] = "number", ["minimum"] = 0.0 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var result = RenderSettingsEditor.Apply(args);
            if (!result.Ok) return ToolResult.Error("OP_FAILED", result.Error);
            return ToolResult.Ok(result.Summary, result.Data);
        }
    }
}
