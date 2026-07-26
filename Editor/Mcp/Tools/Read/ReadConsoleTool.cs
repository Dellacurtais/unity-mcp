using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadConsoleTool : IMcpTool
    {
        public string Name => "read_console";
        public string Description =>
            "Reads structured Unity Console entries from the persistent internal Console when available and/or the live capture buffer. " +
            "Live delta anchors are valid only with the returned bufferDomainId; domain reload resets that epoch.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["source"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "all", "unity", "buffer" }, ["default"] = "all" },
                ["severity"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "all", "warnings_and_errors", "errors_only" }, ["default"] = "all" },
                ["since"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["bufferDomainId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 64 },
                ["messageContains"] = new JObject { ["type"] = "string", ["maxLength"] = 4096 },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 50, ["minimum"] = 1, ["maximum"] = 1000 },
                ["includeStack"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["source"] = new JObject { ["type"] = "string" },
                ["unityConsoleAvailable"] = new JObject { ["type"] = "boolean" },
                ["bufferDomainId"] = new JObject { ["type"] = "string" },
                ["nextSeq"] = new JObject { ["type"] = "integer" },
                ["bufferSize"] = new JObject { ["type"] = "integer" },
                ["errors"] = new JObject { ["type"] = "integer" },
                ["warnings"] = new JObject { ["type"] = "integer" },
                ["logs"] = new JObject { ["type"] = "integer" },
                ["count"] = new JObject { ["type"] = "integer" },
                ["truncated"] = new JObject { ["type"] = "boolean" },
                ["entries"] = new JObject { ["type"] = "array", ["items"] = EntrySchema() },
            },
            ["required"] = new JArray { "source", "unityConsoleAvailable", "bufferDomainId", "nextSeq", "bufferSize", "errors", "warnings", "logs", "count", "truncated", "entries" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var source = ((string)args["source"] ?? "all").ToLowerInvariant();
            var severity = ((string)args["severity"] ?? "all").ToLowerInvariant();
            var since = (int?)args["since"];
            var domainId = (string)args["bufferDomainId"];
            var contains = (string)args["messageContains"];
            var limit = (int?)args["limit"] ?? 50;
            var includeStack = (bool?)args["includeStack"] ?? false;
            if (limit < 1 || limit > 1000 || since < 0 || (contains?.Length ?? 0) > 4096)
                return ToolResult.Error("BAD_ARGS", "limit must be in 1..1000, since must be non-negative and messageContains must be at most 4096 characters");
            if (since.HasValue && string.IsNullOrWhiteSpace(domainId))
                return ToolResult.Error("BAD_ARGS", "bufferDomainId is required with since because sequence numbers reset on domain reload");
            if (since.HasValue && !ConsoleCapture.IsCurrentDomain(domainId))
                return ToolResult.Error("STALE_CONSOLE_ANCHOR", "bufferDomainId belongs to a previous domain reload; request a new anchor");
            if (since.HasValue && source == "all") source = "buffer";
            if (since.HasValue && source == "unity")
                return ToolResult.Error("BAD_ARGS", "since applies only to source=buffer");

            var rows = new List<JObject>();
            var unityAvailable = UnityConsoleReader.IsAvailable;
            var candidateLimit = Math.Min(1000, limit + 1);
            if (source == "unity" && !unityAvailable)
                return ToolResult.Error("UNAVAILABLE", "UnityEditor.LogEntries reflection is unavailable; use source=buffer");

            if ((source == "all" || source == "unity") && unityAvailable)
            {
                foreach (var item in UnityConsoleReader.Read(severity == "all" ? null : severity, contains, candidateLimit))
                {
                    rows.Add(new JObject
                    {
                        ["source"] = "unity",
                        ["row"] = item.Row,
                        ["seq"] = null,
                        ["timeUtc"] = null,
                        ["severity"] = item.Severity,
                        ["message"] = item.Message,
                        ["stack"] = null,
                        ["file"] = item.File,
                        ["line"] = item.Line,
                        ["column"] = 0,
                        ["isCompile"] = UnityConsoleReader.IsCompilerDiagnostic(item),
                    });
                }
            }

            if (source == "all" || source == "buffer")
            {
                LogType? min = severity == "errors_only"
                    ? LogType.Error
                    : severity == "warnings_and_errors" ? LogType.Warning : (LogType?)null;
                foreach (var entry in ConsoleCapture.Snapshot(since, min, contains, candidateLimit))
                {
                    rows.Add(new JObject
                    {
                        ["source"] = "buffer",
                        ["row"] = null,
                        ["seq"] = entry.Seq,
                        ["timeUtc"] = entry.Time.ToUniversalTime().ToString("O"),
                        ["severity"] = entry.Severity.ToString(),
                        ["message"] = entry.Message,
                        ["stack"] = includeStack ? entry.Stack : null,
                        ["file"] = entry.SourceFile,
                        ["line"] = entry.SourceLine,
                        ["column"] = entry.SourceColumn,
                        ["isCompile"] = entry.IsCompile,
                    });
                }
            }

            bool truncated;
            if (source == "all")
            {
                var unityRows = rows.Where(row => string.Equals((string)row["source"], "unity", StringComparison.Ordinal)).ToList();
                var bufferRows = rows.Where(row => string.Equals((string)row["source"], "buffer", StringComparison.Ordinal)).ToList();
                var unityTake = Math.Min(unityRows.Count, (limit + 1) / 2);
                var bufferTake = Math.Min(bufferRows.Count, limit - unityTake);
                if (unityTake + bufferTake < limit)
                    unityTake = Math.Min(unityRows.Count, limit - bufferTake);
                truncated = unityRows.Count > unityTake || bufferRows.Count > bufferTake;
                rows = unityRows.Skip(unityRows.Count - unityTake)
                    .Concat(bufferRows.Skip(bufferRows.Count - bufferTake))
                    .ToList();
            }
            else
            {
                truncated = rows.Count > limit;
                if (truncated) rows = rows.Skip(rows.Count - limit).ToList();
            }
            var errors = rows.Count(row => IsError((string)row["severity"]));
            var warnings = rows.Count(row => string.Equals((string)row["severity"], "Warning", StringComparison.Ordinal));
            var payload = new JObject
            {
                ["source"] = source,
                ["unityConsoleAvailable"] = unityAvailable,
                ["bufferDomainId"] = ConsoleCapture.DomainId,
                ["nextSeq"] = ConsoleCapture.CurrentSeq,
                ["bufferSize"] = ConsoleCapture.BufferSize,
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["logs"] = rows.Count - errors - warnings,
                ["count"] = rows.Count,
                ["truncated"] = truncated,
                ["entries"] = new JArray(rows),
            };
            return ToolResult.Ok($"{rows.Count} console entr{(rows.Count == 1 ? "y" : "ies")}", payload);
        }

        private static bool IsError(string severity) =>
            string.Equals(severity, "Error", StringComparison.Ordinal)
            || string.Equals(severity, "Exception", StringComparison.Ordinal)
            || string.Equals(severity, "Assert", StringComparison.Ordinal);

        private static JObject EntrySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["source"] = new JObject { ["type"] = "string" },
                ["row"] = new JObject { ["type"] = new JArray { "integer", "null" } },
                ["seq"] = new JObject { ["type"] = new JArray { "integer", "null" } },
                ["timeUtc"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["severity"] = new JObject { ["type"] = "string" },
                ["message"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["stack"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["file"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["line"] = new JObject { ["type"] = "integer" },
                ["column"] = new JObject { ["type"] = "integer" },
                ["isCompile"] = new JObject { ["type"] = "boolean" },
            },
            ["additionalProperties"] = false,
        };
    }
}
