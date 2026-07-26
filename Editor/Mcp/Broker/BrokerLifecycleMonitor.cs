using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Broker
{
    public sealed class BrokerLifecycleMonitor : IDisposable
    {
        private readonly BrokerBridgeClient _bridge;
        private readonly McpToolRegistry _registry;
        private string _activeJobId;
        private bool _compilationStarted;
        private bool _compilationFailed;
        private bool _includeWarnings;
        private readonly JArray _compilerMessages = new JArray();

        public BrokerLifecycleMonitor(BrokerBridgeClient bridge, McpToolRegistry registry)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _bridge.ControlRequested += OnControlRequested;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public Task BeforeReloadAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_activeJobId)) return Task.CompletedTask;
            return _bridge.SendEventAsync(
                "before_reload",
                "before_reload",
                new JObject { ["jobId"] = _activeJobId },
                cancellationToken);
        }

        public void Dispose()
        {
            _bridge.ControlRequested -= OnControlRequested;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
        }

        private async Task OnControlRequested(string control, JObject payload)
        {
            var requestedJobId = (string)payload["jobId"];
            if (string.IsNullOrEmpty(requestedJobId)) return;
            if (control == "cancel_job")
            {
                await MainThreadDispatcher.RunAsync(() =>
                {
                    if (string.Equals(_activeJobId, requestedJobId, StringComparison.Ordinal))
                        _activeJobId = null;
                });
                return;
            }
            if (control != "refresh_compile") return;

            string authorizationCode = null;
            string authorizationMessage = null;
            var authorized = await MainThreadDispatcher.RunAsync(() =>
            {
                if (!_registry.TryGet("refresh_assets", out var tool))
                {
                    authorizationCode = "TOOL_NOT_FOUND";
                    authorizationMessage = "Unity refresh tool is not registered.";
                    return false;
                }
                var descriptor = _registry.GetDescriptor(tool.Name) ?? ToolDescriptor.Infer(tool);
                return ToolAuthorization.TryAuthorize(
                    tool, descriptor, out authorizationCode, out authorizationMessage);
            });
            if (!authorized)
            {
                await SafeSendAsync("compile_failed", "ready", new JObject
                {
                    ["jobId"] = requestedJobId,
                    ["code"] = authorizationCode,
                    ["error"] = authorizationMessage,
                });
                return;
            }

            var accepted = await MainThreadDispatcher.RunAsync(() =>
            {
                if (!string.IsNullOrEmpty(_activeJobId)) return false;
                _activeJobId = requestedJobId;
                _compilationStarted = false;
                _compilationFailed = false;
                _includeWarnings = (bool?)payload["includeWarnings"] ?? true;
                _compilerMessages.Clear();
                return true;
            });
            if (!accepted)
            {
                await SafeSendAsync("compile_failed", "ready", new JObject
                {
                    ["jobId"] = requestedJobId,
                    ["error"] = "Another refresh/compile job is already active.",
                });
                return;
            }

            await SafeSendAsync("refresh_accepted", "ready", new JObject
            {
                ["jobId"] = requestedJobId,
            });
            try
            {
                await MainThreadDispatcher.RunAsync(() =>
                {
                    AssetDatabase.Refresh();
                    EditorApplication.delayCall += CompleteRefreshWithoutCompilation;
                });
            }
            catch (Exception exception)
            {
                var failedJobId = await MainThreadDispatcher.RunAsync(() =>
                {
                    var current = _activeJobId;
                    _activeJobId = null;
                    return current;
                });
                await SafeSendAsync("compile_failed", "ready", new JObject
                {
                    ["jobId"] = failedJobId,
                    ["error"] = exception.Message,
                });
            }
        }

        private void OnCompilationStarted(object context)
        {
            if (string.IsNullOrEmpty(_activeJobId)) return;
            _compilationStarted = true;
            _ = SafeSendAsync("compilation_started", "compiling", new JObject
            {
                ["jobId"] = _activeJobId,
                ["compileCycleId"] = Guid.NewGuid().ToString("N"),
            });
        }

        private void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (string.IsNullOrEmpty(_activeJobId) || messages == null) return;
            foreach (var message in messages.Where(item =>
                item.type == CompilerMessageType.Error
                || (_includeWarnings && item.type == CompilerMessageType.Warning)))
            {
                if (message.type == CompilerMessageType.Error) _compilationFailed = true;
                _compilerMessages.Add(new JObject
                {
                    ["assembly"] = assemblyPath,
                    ["file"] = message.file,
                    ["line"] = message.line,
                    ["column"] = message.column,
                    ["message"] = message.message,
                    ["severity"] = message.type.ToString(),
                });
            }
        }

        private void OnCompilationFinished(object context)
        {
            if (string.IsNullOrEmpty(_activeJobId)) return;
            var jobId = _activeJobId;
            if (_compilationFailed)
            {
                _activeJobId = null;
                _ = SafeSendAsync("compile_failed", "compile_failed", new JObject
                {
                    ["jobId"] = jobId,
                    ["error"] = "Unity script compilation failed.",
                    ["messages"] = _compilerMessages.DeepClone(),
                });
                return;
            }
            _ = SafeSendAsync("compilation_finished", "reloading", new JObject
            {
                ["jobId"] = jobId,
                ["messages"] = _compilerMessages.DeepClone(),
            });
        }

        private void CompleteRefreshWithoutCompilation()
        {
            if (string.IsNullOrEmpty(_activeJobId) || _compilationStarted) return;
            var jobId = _activeJobId;
            _activeJobId = null;
            _ = SafeSendAsync("refresh_completed", "ready", new JObject
            {
                ["jobId"] = jobId,
                ["compiled"] = false,
            });
        }

        private async Task SafeSendAsync(string eventName, string state, JObject payload)
        {
            try { await _bridge.SendEventAsync(eventName, state, payload); }
            catch (Exception exception)
            {
                try
                {
                    await MainThreadDispatcher.RunAsync(() =>
                        Debug.LogWarning("[unity-mcp] broker lifecycle event failed: " + exception.Message));
                }
                catch { }
            }
        }
    }
}
