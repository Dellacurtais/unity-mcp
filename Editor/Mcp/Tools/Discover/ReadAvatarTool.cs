using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ReadAvatarTool : IMcpTool
    {
        public string Name => "read_avatar";
        public string Description =>
            "Inspects an Avatar asset (typically extracted from a humanoid model rig). Returns isHuman, isValid, twist values, the human-bone → bone-name mapping (HumanBodyBones), and the count of skeleton transforms. Useful before creating an AvatarMask for a layer.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["guidOrPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.ReadAvatar(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
