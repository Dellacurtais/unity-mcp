using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Analyze
{
    public sealed class AnalyzePerformanceTool : IMcpTool
    {
        public string Name => "analyze_performance";
        public string Description =>
            "Runs all performance analyzers on the active scene and returns the resulting issues (severity, category, message, instanceId, gameObjectPath). Cached against the current snapshot — repeated calls without scene changes are cheap.";

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
            var issues = Bootstrap.Runner.GetIssues(new[] { "performance" });
            return BuildResult(issues, max);
        }

        internal static ToolResult BuildResult(IEnumerable<Issue> issues, int max)
        {
            var arr = new JArray();
            int count = 0;
            int errors = 0, warnings = 0, infos = 0;
            foreach (var i in issues)
            {
                if (count >= max) break;
                count++;
                switch (i.Severity)
                {
                    case IssueSeverity.Error: errors++; break;
                    case IssueSeverity.Warning: warnings++; break;
                    default: infos++; break;
                }
                arr.Add(new JObject
                {
                    ["analyzer"] = i.Analyzer,
                    ["severity"] = i.Severity.ToString(),
                    ["category"] = i.Category,
                    ["message"] = i.Message,
                    ["instanceId"] = i.InstanceID,
                    ["gameObjectPath"] = i.GameObjectPath,
                });
            }
            var payload = new JObject
            {
                ["totalReported"] = count,
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["infos"] = infos,
                ["issues"] = arr,
            };
            var summary = $"{count} issues ({errors} error, {warnings} warning, {infos} info)";
            return ToolResult.Ok(summary, payload);
        }
    }
}
