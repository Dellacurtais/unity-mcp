using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddPostEffectTool : IMcpTool
    {
        public string Name => "add_post_effect";
        public string Description =>
            "Adds a VolumeComponent to a Volume's profile. effect is the type name (e.g. 'Bloom', 'Tonemapping', 'ColorAdjustments', 'Vignette', 'DepthOfField', 'MotionBlur', 'WhiteBalance', 'ChromaticAberration', 'FilmGrain', 'LiftGammaGain', 'ChannelMixer', 'ShadowsMidtonesHighlights', 'SplitToning', 'PaniniProjection', 'LensDistortion'). Resolves across URP / HDRP / SRP Core namespaces. Idempotent: returns the existing component if already present.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["volumeInstanceId"] = new JObject { ["type"] = "integer" },
                ["effect"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "volumeInstanceId", "effect" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.AddEffect(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
