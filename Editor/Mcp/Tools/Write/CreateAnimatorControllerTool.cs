using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateAnimatorControllerTool : IMcpTool
    {
        public string Name => "create_animator_controller";
        public string Description => "Creates a new AnimatorController asset under Assets/ after path and conflict preflight; folders are created as needed.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["path"] = new JObject { ["type"] = "string", ["minLength"] = 1 } },
            ["required"] = new JArray { "path" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.CreateController(args));
    }
}
