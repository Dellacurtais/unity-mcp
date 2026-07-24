using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetPostEffectPropertyTool : IMcpTool
    {
        public string Name => "set_post_effect_property";
        public string Description =>
            "Sets one public VolumeParameter on an existing profile component with Undo support. Call describe_post_effect first for exact property names, value types, enum values, and numeric bounds. overrideState defaults true.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["volumeInstanceId"] = new JObject { ["type"] = "integer" },
                ["effect"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Fully-qualified effect type from list_post_effects." },
                ["property"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["value"] = new JObject { ["description"] = "JSON value matching describe_post_effect: scalar, enum name/integer, color, vector, asset path/GUID, or instanceId." },
                ["overrideState"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "volumeInstanceId", "effect", "property", "value" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.SetEffectProperty(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data);
        }
    }
}
