using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.PostFx.Tools
{
    public sealed class RemovePostEffectTool : IMcpTool
    {
        public string Name => "remove_post_effect";
        public string Description =>
            "Removes and destroys a VolumeComponent subasset from a Volume's persistent shared profile with Undo support. Use the fully-qualified type returned by list_post_effects.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["volumeInstanceId"] = new JObject { ["type"] = "integer" },
                ["effect"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            },
            ["required"] = new JArray { "volumeInstanceId", "effect" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.RemoveEffect(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data);
        }
    }
}
