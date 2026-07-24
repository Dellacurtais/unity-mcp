using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AssignAnimatorControllerTool : IMcpTool
    {
        public string Name => "assign_controller_to_animator";
        public string Description => "Assigns a RuntimeAnimatorController asset to an existing Animator with Undo, scene dirty state and Prefab override recording.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            },
            ["required"] = new JArray { "instanceId", "controllerGuidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.SceneWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.AssignControllerToAnimator(args));
    }
}
