using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ReadAnimatorControllerTool : IMcpTool
    {
        public string Name => "read_animator_controller";
        public string Description => "Returns structured parameters, layers, states, motions, transitions, conditions and AnyState transitions for exactly one AnimatorController reference.";
        public JObject InputSchema => AnimationToolContracts.ControllerInputSchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.ReadController(args));
    }
}
