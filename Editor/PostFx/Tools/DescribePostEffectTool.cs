using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.PostFx.Tools
{
    public sealed class DescribePostEffectTool : IMcpTool
    {
        public string Name => "describe_post_effect";
        public string Description =>
            "Returns structured metadata for a pipeline-compatible VolumeComponent: exact property names, wrapper/value types, defaults, enum values, interpolation mode, and numeric bounds when exposed by the parameter.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["effect"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            },
            ["required"] = new JArray { "effect" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.DescribeEffectType(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data);
        }
    }
}
