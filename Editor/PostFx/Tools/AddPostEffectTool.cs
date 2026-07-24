using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddPostEffectTool : IMcpTool
    {
        public string Name => "add_post_effect";
        public string Description =>
            "Adds a pipeline-compatible VolumeComponent to a Volume's persistent shared profile. Discover exact fully-qualified types with list_post_effects available:true. Idempotent; overrideAll defaults false.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["volumeInstanceId"] = new JObject { ["type"] = "integer" },
                ["effect"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Fully-qualified type from list_post_effects is safest; an unambiguous short name is accepted." },
                ["overrideAll"] = new JObject { ["type"] = "boolean", ["default"] = false, ["description"] = "Set every VolumeParameter overrideState when creating the component." },
            },
            ["required"] = new JArray { "volumeInstanceId", "effect" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.AddEffect(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data);
        }
    }
}
