using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ReadAvatarTool : IMcpTool
    {
        public string Name => "read_avatar";
        public string Description => "Returns structured validity, humanoid mapping and skeleton metadata for an Avatar asset.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["guidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 } },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.ReadAvatar(args));
    }
}
