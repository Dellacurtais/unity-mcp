using System;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Bridge
{
    public static class BridgeChannel
    {
        private static int _nextScriptId;

        public static (bool ok, string error) Precheck(bool requirePlayMode)
        {
            if (requirePlayMode && !EditorApplication.isPlaying)
                return (false, "NOT_PLAYING: bridge handlers only run during play mode; call enter_play_mode first.");
            if (!McpCommandBridge.Exists)
                return (false, "BRIDGE_NOT_INSTALLED: no McpCommandBridge is active; call bridge_install first.");
            return (true, null);
        }

        public static async Task<BridgeScript> SubmitAndAwait(
            BridgeScript script, double timeoutSeconds, CancellationToken cancellationToken = default)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            cancellationToken.ThrowIfCancellationRequested();
            script.Id = Interlocked.Increment(ref _nextScriptId);

            var completion = new TaskCompletionSource<BridgeScript>(TaskCreationOptions.RunContinuationsAsynchronously);
            script.CompletionCallback = terminal => completion.TrySetResult(terminal);

            string submitError = null;
            var submitted = await MainThreadDispatcher.RunAsync(() =>
            {
                var bridge = McpCommandBridge.Instance;
                if (bridge == null) { submitError = "BRIDGE_DISAPPEARED"; return false; }
                return bridge.TrySubmit(script, out submitError);
            }, cancellationToken);
            if (!submitted)
            {
                script.TrySetTerminal(BridgeScriptStatus.Failed, submitError ?? "BRIDGE_SUBMIT_FAILED");
                throw new InvalidOperationException(submitError ?? "BRIDGE_SUBMIT_FAILED");
            }

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (linked.Token.Register(() => cancelled.TrySetResult(true)))
                {
                    var winner = await Task.WhenAny(completion.Task, cancelled.Task);
                    if (winner == completion.Task) return await completion.Task;
                }

                var reason = cancellationToken.IsCancellationRequested ? "MCP_REQUEST_CANCELLED" : "EDITOR_TIMEOUT";
                try
                {
                    await MainThreadDispatcher.RunAsync(() => McpCommandBridge.Instance?.Cancel(script.Id, reason));
                }
                catch { }
                script.ClearCompletionCallback();
                if (completion.Task.IsCompleted) return await completion.Task;
                if (script.IsComplete) return script;
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                throw new TimeoutException(
                    $"bridge script #{script.Id} exceeded {timeoutSeconds:F1}s; status={script.Status}, " +
                    $"results={script.Results.Count}/{script.Steps.Count}; cancellation requested.");
            }
        }
    }
}
