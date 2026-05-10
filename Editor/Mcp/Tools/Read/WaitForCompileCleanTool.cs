using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class WaitForCompileCleanTool : IMcpTool
    {
        public string Name => "wait_for_compile_clean";
        public string Description =>
            "Forces an AssetDatabase.Refresh, cooperatively waits for compilation to finish (pump stays responsive — other tools keep working), then reports any compile errors / warnings that appeared during the wait. " +
            "Returns 'clean' when compile finishes with 0 errors. Single call replaces the refresh_assets + read_console errors_only since=N pattern. " +
            "Note: a successful compile triggers a domain reload, which closes in-flight requests with HTTP 503 — if you see 'Unity MCP server didn't respond' followed by a system note about assembly reload, retry once: the second call returns clean immediately.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["default"] = 60 },
                ["includeWarnings"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "wait_for_compile_clean requires async dispatch — the framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var timeoutSeconds = (float?)args["timeoutSeconds"] ?? 60f;
            var includeWarnings = (bool?)args["includeWarnings"] ?? true;

            // Anchor: any compile messages with seq >= startSeq belong to this run.
            int startSeq = ConsoleCapture.CurrentSeq;

            // Trigger refresh on the main thread; capture wall-clock for the report.
            double startTime = await MainThreadDispatcher.RunAsync(() =>
            {
                AssetDatabase.Refresh();
                return EditorApplication.timeSinceStartup;
            });

            // Cooperative wait — same pattern as RefreshAssetsTool. Polling
            // happens on EditorApplication.update without ever blocking the
            // dispatcher pump.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.CallbackFunction onUpdate = null;
            onUpdate = () =>
            {
                if (!EditorApplication.isCompiling)
                {
                    EditorApplication.update -= onUpdate;
                    tcs.TrySetResult(true);
                }
            };
            await MainThreadDispatcher.RunAsync(() =>
            {
                if (!EditorApplication.isCompiling) { tcs.TrySetResult(true); return; }
                EditorApplication.update += onUpdate;
            });

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (winner != tcs.Task)
            {
                await MainThreadDispatcher.RunAsync(() => EditorApplication.update -= onUpdate);
                return ToolResult.Error("TIMEOUT",
                    $"compilation still running after {timeoutSeconds}s — read_console errors_only since={startSeq} for details.");
            }

            // One delayCall tick so any pending assembly reload starts. If the
            // reload fully takes us out, this call won't return — the framework
            // closes the in-flight request with HTTP 503 and the agent retries.
            var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await MainThreadDispatcher.RunAsync(() =>
            {
                EditorApplication.delayCall += () => settled.TrySetResult(true);
            });
            await Task.WhenAny(settled.Task, Task.Delay(2000));

            // Report
            var min = includeWarnings ? LogType.Warning : LogType.Error;
            var entries = ConsoleCapture.Snapshot(startSeq, min, contains: null, limit: 100);
            int errors = entries.Count(e => e.Severity == LogType.Error || e.Severity == LogType.Exception || e.Severity == LogType.Assert);
            int warnings = entries.Count(e => e.Severity == LogType.Warning);

            double elapsed = await MainThreadDispatcher.RunAsync(() => EditorApplication.timeSinceStartup) - startTime;

            var sb = new StringBuilder();
            sb.AppendLine($"compile finished in {elapsed:F1}s — {errors} error(s), {warnings} warning(s)");
            if (errors == 0 && warnings == 0)
            {
                sb.Append("✓ clean");
            }
            else
            {
                foreach (var e in entries)
                {
                    var loc = e.IsCompile && !string.IsNullOrEmpty(e.SourceFile)
                        ? $"[{e.SourceFile}:{e.SourceLine}{(e.SourceColumn > 0 ? $":{e.SourceColumn}" : "")}]"
                        : $"[{e.Severity}]";
                    sb.AppendLine($"  {loc} {e.Message}");
                }
            }
            return ToolResult.Ok(sb.ToString());
        }
    }
}
