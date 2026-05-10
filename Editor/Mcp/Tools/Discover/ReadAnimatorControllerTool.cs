using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ReadAnimatorControllerTool : IMcpTool
    {
        public string Name => "read_animator_controller";
        public string Description =>
            "Returns the full structure of an AnimatorController: parameters (name, type, default value), layers (name, weight, blending), per-layer states (name, motion, speed) and transitions (destination, conditions, duration, hasExitTime), plus AnyState transitions. Pass `controllerGuidOrPath` (preferred) or `controllerInstanceId`.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["controllerInstanceId"] = new JObject { ["type"] = "integer" },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.ReadController(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
