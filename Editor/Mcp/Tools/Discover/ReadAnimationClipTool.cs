using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ReadAnimationClipTool : IMcpTool
    {
        public string Name => "read_animation_clip";
        public string Description =>
            "Inspects an AnimationClip: length, frameRate, isLooping, loop/mirror settings, list of curve bindings (path/property/type — capped at 30 with overflow note), and animation events with their function/parameters.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clipGuidOrPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "clipGuidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.ReadClip(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
