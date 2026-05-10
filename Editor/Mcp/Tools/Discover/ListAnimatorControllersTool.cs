using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ListAnimatorControllersTool : IMcpTool
    {
        public string Name => "list_animator_controllers";
        public string Description =>
            "Lists `.controller` (AnimatorController) assets in the project. Filter by `nameContains` and/or `folder` prefix. Cap with `limit`.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["nameContains"] = new JObject { ["type"] = "string" },
                ["folder"] = new JObject { ["type"] = "string" },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 100 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.ListControllers(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
