using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.PostFx.Tools
{
    public sealed class DescribePostEffectTool : IMcpTool
    {
        public string Name => "describe_post_effect";
        public string Description =>
            "Returns the parameter schema of a VolumeComponent type (without needing a Volume): each VolumeParameter field's name, the parameter wrapper type (FloatParameter, ColorParameter, MinFloatParameter, …) and the underlying value type (float, Color, int, …). Use to learn valid `property` names before set_post_effect_property.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["effect"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "effect" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.DescribeEffectType(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
