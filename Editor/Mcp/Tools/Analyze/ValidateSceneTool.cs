using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Analyze
{
    public sealed class ValidateSceneTool : IMcpTool
    {
        public string Name => "validate_scene";
        public string Description =>
            "Runs validation analyzers (missing references, broken scripts, required-field violations) and returns the resulting issues. Use after authoring to catch broken refs before save.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["maxIssues"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 200 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var max = (int?)args["maxIssues"] ?? 200;
            var issues = Bootstrap.Runner.GetIssues(new[] { "validation" });
            return AnalyzePerformanceTool.BuildResult(issues, max);
        }
    }
}
