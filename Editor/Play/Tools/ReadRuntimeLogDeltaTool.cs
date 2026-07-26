using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class ReadRuntimeLogDeltaTool : IMcpTool
    {
        public string Name => "read_runtime_log_delta";
        public string Description =>
            "Returns console entries since a given sequence anchor (from mark_log_anchor) — Debug.Log*, exceptions, compile messages. " +
            "Filter by severity to focus on what matters. Use to correlate a screenshot or a runtime command with any error/warning it produced.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sinceSeq"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["severity"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "all", "warnings_and_errors", "errors_only" },
                    ["default"] = "all"
                },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 100, ["minimum"] = 1, ["maximum"] = 1000 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;
        public JObject OutputSchema => PlayToolSchemas.Logs;

        public ToolResult Execute(JObject args)
        {
            int? sinceSeq = (int?)args["sinceSeq"];
            string severity = ((string)args["severity"] ?? "all").ToLowerInvariant();
            int limit = (int?)args["limit"] ?? 100;
            if (sinceSeq < 0) return ToolResult.Error("BAD_SEQUENCE", "sinceSeq must be non-negative");
            if (limit < 1 || limit > 1000) return ToolResult.Error("BAD_LIMIT", "limit must be between 1 and 1000");
            if (severity != "all" && severity != "warnings_and_errors" && severity != "errors_only")
                return ToolResult.Error("BAD_SEVERITY", "severity must be all, warnings_and_errors, or errors_only");

            LogType? minSeverity = severity switch
            {
                "errors_only" => LogType.Error,
                "warnings_and_errors" => LogType.Warning,
                _ => (LogType?)null,
            };

            var entries = ConsoleCapture.Snapshot(sinceSeq, minSeverity, contains: null, limit: limit);

            var arr = new JArray();
            foreach (var e in entries)
            {
                arr.Add(new JObject
                {
                    ["seq"] = e.Seq,
                    ["time"] = e.Time.ToString("HH:mm:ss.fff"),
                    ["severity"] = e.Severity.ToString(),
                    ["message"] = e.Message,
                    ["isCompile"] = e.IsCompile,
                });
            }

            int currentSeq = ConsoleCapture.CurrentSeq;
            string summary = sinceSeq.HasValue
                ? $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} since seq={sinceSeq.Value} (current seq={currentSeq})"
                : $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} from buffer (current seq={currentSeq})";

            return ToolResult.Ok(summary, new JObject
            {
                ["entries"] = arr,
                ["currentSeq"] = currentSeq,
                ["returnedCount"] = entries.Count,
            });
        }
    }
}
