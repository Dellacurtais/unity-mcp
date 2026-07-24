using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadAnimatorStateTool : IMcpTool
    {
        public string Name => "read_animator_state";
        public string Description => "Play Mode only. Returns current layer state information and live Animator parameter values.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["instanceId"] = new JObject { ["type"] = "integer" } },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => AnimationToolContracts.RuntimeReadDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.ReadAnimatorState(args));
    }
}
