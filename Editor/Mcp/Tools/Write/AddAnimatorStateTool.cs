using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimatorStateTool : IMcpTool
    {
        public string Name => "add_animator_state";
        public string Description =>
            "Adds a state to an AnimatorController layer. Optional `clipGuidOrPath` assigns the state's motion (an AnimationClip). `speed` defaults to 1. `isDefault: true` makes it the layer's default entry state.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["controllerInstanceId"] = new JObject { ["type"] = "integer" },
                ["state"] = new JObject { ["type"] = "string" },
                ["layer"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["clipGuidOrPath"] = new JObject { ["type"] = "string" },
                ["speed"] = new JObject { ["type"] = "number", ["default"] = 1 },
                ["isDefault"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "state" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.AddState(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
