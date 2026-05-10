using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class PlayAnimatorStateTool : IMcpTool
    {
        public string Name => "play_animator_state";
        public string Description =>
            "Calls Animator.Play(state, layer, normalizedTime). Forces immediate transition to the named state. Most visible in Play Mode; in edit mode only the state is selected without time advancement.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["state"] = new JObject { ["type"] = "string" },
                ["layer"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["normalizedTime"] = new JObject { ["type"] = "number", ["default"] = 0 },
            },
            ["required"] = new JArray { "instanceId", "state" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.PlayAnimatorState(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
