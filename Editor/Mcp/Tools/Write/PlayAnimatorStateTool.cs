using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class PlayAnimatorStateTool : IMcpTool
    {
        public string Name => "play_animator_state";
        public string Description => "Play Mode only. Validates and plays an Animator state by layer-qualified or simple name at normalized time 0..1.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["state"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["layer"] = new JObject { ["type"] = "integer", ["minimum"] = -1, ["default"] = 0 },
                ["normalizedTime"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 0 },
            },
            ["required"] = new JArray { "instanceId", "state" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.RuntimeWriteDescriptor(Name, false);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.PlayAnimatorState(args));
    }
}
