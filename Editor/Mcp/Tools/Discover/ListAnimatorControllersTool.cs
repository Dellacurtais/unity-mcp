using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ListAnimatorControllersTool : IMcpTool
    {
        public string Name => "list_animator_controllers";
        public string Description => "Lists AnimatorController assets deterministically with optional Assets-folder/name filters and a bounded limit.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["nameContains"] = new JObject { ["type"] = "string" },
                ["folder"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500, ["default"] = 100 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.ListControllers(args));
    }
}
