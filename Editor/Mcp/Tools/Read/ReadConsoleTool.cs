using System.Collections.Generic;
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
            "Reads Unity Console messages from TWO sources merged together:\n" +
            " 1. **Unity's own Console** (`UnityEditor.LogEntries` via reflection) — persistent across domain reloads, includes pre-existing compile errors and anything currently visible in the Console window. No `seq` available.\n" +
            " 2. **Our live capture buffer** (subscribed to `Application.logMessageReceivedThreaded` + `CompilationPipeline.assemblyCompilationFinished`) — stable `seq` numbers for delta queries.\n\n" +
            "Filters: `source` (`all` default / `unity` / `buffer`), `severity` (`all` / `warnings_and_errors` / `errors_only`), `since` (only entries with seq >= N — implies source=buffer because LogEntries has no seq), `messageContains`, `limit` (default 50), `includeStack`.\n" +
            "When `since` is set OR `source=buffer`, only the live buffer is read — enables 'what changed during my op'. Without `since`, you get the full Console as the user sees it.\n" +
            "Note: domain reload (after a successful .cs compile) resets the live buffer's seq counter; LogEntries persists.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["source"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "all", "unity", "buffer" }, ["default"] = "all" },
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
            var source = ((string)args["source"] ?? "all").ToLowerInvariant();
            var severityFilter = ((string)args["severity"] ?? "all").ToLowerInvariant();
            var since = (int?)args["since"];
            var contains = (string)args["messageContains"];
            var limit = (int?)args["limit"] ?? 50;
            var includeStack = (bool?)args["includeStack"] ?? false;

            // `since` only applies to the live buffer (LogEntries has no seq).
            if (since.HasValue && source == "all") source = "buffer";

            var sb = new StringBuilder();
            int totalErrors = 0, totalWarnings = 0, totalInfo = 0;

            // 1) Unity LogEntries (persistent, full Console snapshot)
            if (source == "all" || source == "unity")
            {
                if (!UnityConsoleReader.IsAvailable && source == "unity")
                {
                    return ToolResult.Error("UNAVAILABLE",
                        "UnityEditor.LogEntries reflection failed — internal API may have changed in this Unity version. Try source=buffer.");
                }

                var unityItems = UnityConsoleReader.IsAvailable
                    ? UnityConsoleReader.Read(severityFilter == "all" ? null : severityFilter, contains, limit)
                    : new List<UnityConsoleReader.Item>();

                if (unityItems.Count > 0)
                {
                    var (e, w, l) = UnityConsoleReader.Counts();
                    sb.AppendLine($"=== Unity Console (full state) — {e} error(s), {w} warning(s), {l} log(s) total ===");
                    foreach (var it in unityItems)
                    {
                        if (it.Severity == "Error") totalErrors++;
                        else if (it.Severity == "Warning") totalWarnings++;
                        else totalInfo++;
                        var loc = !string.IsNullOrEmpty(it.File)
                            ? $"[{it.File}:{it.Line}]"
                            : $"[{it.Severity}]";
                        sb.AppendLine($"row#{it.Row} {loc} {it.Message}");
                    }
                }
                else if (source == "unity")
                {
                    sb.AppendLine("Unity Console is empty (or no entries match the filter).");
                }
            }

            // 2) Live capture buffer (delta-friendly via `since`)
            if (source == "all" || source == "buffer")
            {
                LogType? min = severityFilter switch
                {
                    "errors_only" => LogType.Error,
                    "warnings_and_errors" => LogType.Warning,
                    _ => null,
                };
                var captured = ConsoleCapture.Snapshot(since, min, contains, limit);
                if (captured.Count > 0)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine($"=== Live capture buffer — {ConsoleCapture.BufferSize} buffered, currentSeq={ConsoleCapture.CurrentSeq} ===");
                    foreach (var e in captured)
                    {
                        if (e.Severity == LogType.Error || e.Severity == LogType.Exception || e.Severity == LogType.Assert) totalErrors++;
                        else if (e.Severity == LogType.Warning) totalWarnings++;
                        else totalInfo++;
                        var loc = e.IsCompile && !string.IsNullOrEmpty(e.SourceFile)
                            ? $"[{e.SourceFile}:{e.SourceLine}{(e.SourceColumn > 0 ? $":{e.SourceColumn}" : "")}]"
                            : $"[{e.Severity}]";
                        sb.AppendLine($"#{e.Seq} {e.Time:HH:mm:ss.fff} {loc} {e.Message}");
                        if (includeStack && !string.IsNullOrEmpty(e.Stack))
                        {
                            var stack = e.Stack.TrimEnd();
                            if (stack.Length > 0) sb.AppendLine("    " + stack.Replace("\n", "\n    "));
                        }
                    }
                }
                else if (source == "buffer")
                {
                    sb.AppendLine("Live buffer is empty (or no entries match the filter).");
                }
            }

            if (sb.Length == 0)
                sb.AppendLine("(no console entries — both sources empty for these filters)");

            // Summary header
            var header = $"{totalErrors} error/exception, {totalWarnings} warning, {totalInfo} info  (source={source})\n\n";
            return ToolResult.Ok(header + sb.ToString());
        }
    }
}
