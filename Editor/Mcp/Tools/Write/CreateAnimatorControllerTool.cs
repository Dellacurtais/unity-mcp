using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateAnimatorControllerTool : IMcpTool
    {
        public string Name => "create_animator_controller";
        public string Description =>
            "Creates a new AnimatorController .controller asset on disk. Path should be under Assets/ (folders are auto-created). Returns the controller's instanceId. Follow with add_animator_parameter / add_animator_state / add_animator_transition / assign_controller_to_animator.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["path"] = new JObject { ["type"] = "string", ["description"] = "Asset path under Assets/, e.g. Assets/Animations/Player.controller" },
            },
            ["required"] = new JArray { "path" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.CreateController(args);
            return r.Ok ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID }) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
