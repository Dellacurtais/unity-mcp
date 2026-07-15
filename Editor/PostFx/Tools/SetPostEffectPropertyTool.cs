using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetPostEffectPropertyTool : IMcpTool
    {
        public string Name => "set_post_effect_property";
        public string Description =>
            "Sets a single VolumeParameter on an effect already added to a volume's profile. Examples: Bloom.intensity (number), Bloom.tint (color), Tonemapping.mode (enum 'Neutral'/'ACES'), ColorAdjustments.postExposure (number), Vignette.intensity. Toggles the parameter's overrideState=true automatically.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["volumeInstanceId"] = new JObject { ["type"] = "integer" },
                ["effect"] = new JObject { ["type"] = "string", ["description"] = "Effect type name (e.g. 'Bloom')." },
                ["property"] = new JObject { ["type"] = "string" },
                ["value"] = new JObject(),
            },
            ["required"] = new JArray { "volumeInstanceId", "effect", "property", "value" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.SetEffectProperty(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
