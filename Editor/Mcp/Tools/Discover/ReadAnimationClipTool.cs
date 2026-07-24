using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ReadAnimationClipTool : IMcpTool
    {
        public string Name => "read_animation_clip";
        public string Description => "Returns structured AnimationClip metadata, settings, float/object-reference bindings and persistent AnimationEvents.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["clipGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 } },
            ["required"] = new JArray { "clipGuidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.ReadClip(args));
    }
}
