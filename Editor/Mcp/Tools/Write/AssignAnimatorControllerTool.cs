using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AssignAnimatorControllerTool : IMcpTool
    {
        public string Name => "assign_controller_to_animator";
        public string Description =>
            "Assigns a RuntimeAnimatorController (typically an AnimatorController .controller asset, but also an AnimatorOverrideController) to the Animator on a scene GameObject. The GameObject MUST already have an Animator component (call add_component first if needed).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "instanceId", "controllerGuidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.AssignControllerToAnimator(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
