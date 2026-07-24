using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimatorStateTool : IMcpTool
    {
        public string Name => "add_animator_state";
        public string Description => "Adds a unique AnimatorState after validating controller, layer, optional clip and finite speed; supports atomic default-state assignment and Undo.";
        public JObject InputSchema => AnimationToolContracts.ControllerInputSchema(new JObject
        {
            ["state"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            ["layer"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
            ["clipGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            ["speed"] = new JObject { ["type"] = "number", ["default"] = 1 },
            ["isDefault"] = new JObject { ["type"] = "boolean", ["default"] = false },
        }, "state");
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.AddState(args));
    }
}
