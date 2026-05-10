using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetRenderSettingsTool : IMcpTool
    {
        public string Name => "set_render_settings";
        public string Description =>
            "Tweaks scene-level rendering: ambient lighting (mode + colors + intensity), fog (on/off, color, density, distances, mode), skybox material (by GUID/path), reflection intensity. Pass only the keys you want changed. Note: RenderSettings is static; per-tweak undo is best-effort — the scene is marked dirty so the change persists with the scene save.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["ambientMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Skybox", "Trilight", "Flat", "Custom" } },
                ["ambientLight"] = SchemaHelpers.ColorSchema,
                ["ambientSkyColor"] = SchemaHelpers.ColorSchema,
                ["ambientEquatorColor"] = SchemaHelpers.ColorSchema,
                ["ambientGroundColor"] = SchemaHelpers.ColorSchema,
                ["ambientIntensity"] = new JObject { ["type"] = "number" },
                ["fog"] = new JObject { ["type"] = "boolean" },
                ["fogColor"] = SchemaHelpers.ColorSchema,
                ["fogDensity"] = new JObject { ["type"] = "number" },
                ["fogStartDistance"] = new JObject { ["type"] = "number" },
                ["fogEndDistance"] = new JObject { ["type"] = "number" },
                ["fogMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Linear", "Exponential", "ExponentialSquared" } },
                ["skyboxGuid"] = new JObject { ["type"] = "string" },
                ["reflectionIntensity"] = new JObject { ["type"] = "number" },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = RenderSettingsEditor.Apply(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
