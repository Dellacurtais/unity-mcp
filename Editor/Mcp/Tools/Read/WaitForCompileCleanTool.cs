using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class WaitForCompileCleanTool : IMcpTool
    {
        public string Name => "wait_for_compile_clean";
        public string Description =>
            "Refreshes assets and waits cooperatively for one public CompilationPipeline cycle. " +
            "Returns structured compiler diagnostics when the request survives; a successful compilation can still trigger domain reload and interrupt the HTTP request.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 600, ["default"] = 60 },
                ["includeWarnings"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["compiled"] = new JObject { ["type"] = "boolean" },
                ["clean"] = new JObject { ["type"] = "boolean" },
                ["elapsedSeconds"] = new JObject { ["type"] = "number" },
                ["errors"] = new JObject { ["type"] = "integer" },
                ["warnings"] = new JObject { ["type"] = "integer" },
                ["diagnostics"] = new JObject { ["type"] = "array", ["items"] = DiagnosticSchema() },
                ["diagnosticsTruncated"] = new JObject { ["type"] = "boolean" },
                ["bufferDomainId"] = new JObject { ["type"] = "string" },
                ["nextSeq"] = new JObject { ["type"] = "integer" },
                ["serverMayReload"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "compiled", "clean", "elapsedSeconds", "errors", "warnings", "diagnostics", "diagnosticsTruncated", "bufferDomainId", "nextSeq", "serverMayReload" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args) =>
            ToolResult.Error("ASYNC_REQUIRED", "wait_for_compile_clean must run through ExecuteAsync");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var timeoutSeconds = (double?)args["timeoutSeconds"] ?? 60d;
            var includeWarnings = (bool?)args["includeWarnings"] ?? true;
            if (timeoutSeconds < 1d || timeoutSeconds > 600d)
                return ToolResult.Error("BAD_ARGS", "timeoutSeconds must be in 1..600");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying)
                return ToolResult.Error("EDITOR_BUSY", "asset refresh and script compilation require stable Edit Mode");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return ToolResult.Error("EDITOR_BUSY", "Unity is already compiling or updating assets");

            var context = ToolExecutionContext.Current;
            var token = context != null ? context.CancellationToken : default;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var diagnostics = new List<JObject>();
            var compilationStarted = false;
            var diagnosticsTruncated = false;
            const int maxDiagnostics = 1000;
            double startTime = 0d;

            Action<object> onStarted = _ => compilationStarted = true;
            Action<string, CompilerMessage[]> onAssemblyFinished = (assembly, messages) =>
            {
                if (messages == null) return;
                foreach (var message in messages)
                {
                    if (message.type != CompilerMessageType.Error
                        && !(includeWarnings && message.type == CompilerMessageType.Warning)) continue;
                    if (diagnostics.Count >= maxDiagnostics) { diagnosticsTruncated = true; continue; }
                    diagnostics.Add(new JObject
                    {
                        ["assembly"] = assembly,
                        ["severity"] = message.type.ToString(),
                        ["message"] = message.message,
                        ["file"] = message.file,
                        ["line"] = message.line,
                        ["column"] = message.column,
                    });
                }
            };
            Action<object> onFinished = _ => completion.TrySetResult(true);

            try
            {
                await MainThreadDispatcher.RunAsync(() =>
                {
                    CompilationPipeline.compilationStarted += onStarted;
                    CompilationPipeline.assemblyCompilationFinished += onAssemblyFinished;
                    CompilationPipeline.compilationFinished += onFinished;
                    startTime = EditorApplication.timeSinceStartup;
                    AssetDatabase.Refresh();
                    EditorApplication.delayCall += () =>
                    {
                        if (!compilationStarted && !EditorApplication.isCompiling)
                            completion.TrySetResult(false);
                    };
                }, token);

                using (token.Register(() => completion.TrySetCanceled(token)))
                {
                    var timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                    var winner = await Task.WhenAny(completion.Task, timeout);
                    if (winner != completion.Task)
                        return ToolResult.Error("TIMEOUT", $"compilation or refresh did not settle within {timeoutSeconds:F1}s");
                    var compiled = await completion.Task;
                    var elapsed = await MainThreadDispatcher.RunAsync(() => EditorApplication.timeSinceStartup, token) - startTime;
                    var errors = diagnostics.Count(item => string.Equals((string)item["severity"], CompilerMessageType.Error.ToString(), StringComparison.Ordinal));
                    var warnings = diagnostics.Count - errors;
                    var payload = new JObject
                    {
                        ["compiled"] = compiled,
                        ["clean"] = errors == 0,
                        ["elapsedSeconds"] = Math.Max(0d, elapsed),
                        ["errors"] = errors,
                        ["warnings"] = warnings,
                        ["diagnostics"] = new JArray(diagnostics),
                        ["diagnosticsTruncated"] = diagnosticsTruncated,
                        ["bufferDomainId"] = ConsoleCapture.DomainId,
                        ["nextSeq"] = ConsoleCapture.CurrentSeq,
                        ["serverMayReload"] = compiled,
                    };
                    if (errors > 0)
                        return ToolResult.Error("COMPILATION_FAILED", $"script compilation finished with {errors} error(s)", payload);
                    return ToolResult.Ok(compiled
                        ? $"script compilation finished with {warnings} warning(s)"
                        : "asset refresh completed without script compilation", payload);
                }
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Error("CANCELLED", "compile wait cancelled");
            }
            catch (Exception exception)
            {
                return ToolResult.Error("COMPILE_WAIT_FAILED", exception.Message);
            }
            finally
            {
                try
                {
                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        CompilationPipeline.compilationStarted -= onStarted;
                        CompilationPipeline.assemblyCompilationFinished -= onAssemblyFinished;
                        CompilationPipeline.compilationFinished -= onFinished;
                    });
                }
                catch { }
            }
        }

        private static JObject DiagnosticSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assembly"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["severity"] = new JObject { ["type"] = "string" },
                ["message"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["file"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["line"] = new JObject { ["type"] = "integer" },
                ["column"] = new JObject { ["type"] = "integer" },
            },
            ["additionalProperties"] = false,
        };
    }
}
