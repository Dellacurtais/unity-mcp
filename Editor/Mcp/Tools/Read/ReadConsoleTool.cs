using System.Text;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadConsoleTool : IMcpTool
    {
        public string Name => "read_console";
        public string Description =>
            "Reads recent Unity Console messages — Debug.Log/Warning/Error, uncaught exceptions, AND structured compile errors/warnings (with file:line). " +
            "Filters: severity (`all` / `warnings_and_errors` / `errors_only`), `since` (only entries with seq >= N — pair with read_console first to get the current seq, then pass that as since on a follow-up call), `messageContains` (substring match), `limit` (default 50), `includeStack` (default false; on for exceptions). " +
            "Note: an assembly reload (after a successful .cs compile) resets the buffer and the seq counter. If your since= is from before such a reload, you'll see 0 entries — interpret as 'clean compile, buffer reset'.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["severity"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "all", "warnings_and_errors", "errors_only" }, ["default"] = "all" },
                ["since"] = new JObject { ["type"] = "integer" },
                ["messageContains"] = new JObject { ["type"] = "string" },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 50, ["minimum"] = 1 },
                ["includeStack"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            LogType? min = ((string)args["severity"] ?? "all").ToLowerInvariant() switch
            {
                "errors_only" => LogType.Error,
                "warnings_and_errors" => LogType.Warning,
                _ => null,
            };
            var since = (int?)args["since"];
            var contains = (string)args["messageContains"];
            var limit = (int?)args["limit"] ?? 50;
            var includeStack = (bool?)args["includeStack"] ?? false;

            var entries = ConsoleCapture.Snapshot(since, min, contains, limit);

            int errors = 0, warnings = 0, info = 0;
            foreach (var e in entries)
            {
                if (e.Severity == LogType.Error || e.Severity == LogType.Exception || e.Severity == LogType.Assert) errors++;
                else if (e.Severity == LogType.Warning) warnings++;
                else info++;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Console — buffer={ConsoleCapture.BufferSize}, currentSeq={ConsoleCapture.CurrentSeq}");
            sb.AppendLine($"Returned {entries.Count} entries  ({errors} error/exception, {warnings} warning, {info} info)");
            if (entries.Count == 0)
            {
                sb.AppendLine("  (no matching entries)");
            }
            else
            {
                foreach (var e in entries)
                {
                    var loc = e.IsCompile && !string.IsNullOrEmpty(e.SourceFile)
                        ? $"[{e.SourceFile}:{e.SourceLine}{(e.SourceColumn > 0 ? $":{e.SourceColumn}" : "")}]"
                        : $"[{e.Severity}]";
                    sb.AppendLine($"#{e.Seq} {e.Time:HH:mm:ss.fff} {loc} {e.Message}");
                    if (includeStack && !string.IsNullOrEmpty(e.Stack))
                    {
                        var indented = e.Stack.TrimEnd();
                        if (indented.Length > 0)
                            sb.AppendLine("    " + indented.Replace("\n", "\n    "));
                    }
                }
            }
            return ToolResult.Ok(sb.ToString());
        }
    }
}
