using System;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Bridge
{
    /// Editor-side wrapper around McpCommandBridge.Instance. The MCP tools
    /// shouldn't talk to the bridge GameObject directly — they go through
    /// here so we can handle:
    ///   - preconditions (bridge exists, scene in play mode)
    ///   - script id allocation
    ///   - awaiting completion without blocking the dispatcher pump
    ///   - graceful cancellation when the editor reloads assemblies during
    ///     a long script
    public static class BridgeChannel
    {
        private static int _nextScriptId = 1;

        public static (bool ok, string error) Precheck(bool requirePlayMode)
        {
            if (requirePlayMode && !EditorApplication.isPlaying)
                return (false, "NOT_PLAYING: bridge handlers only run during play mode — call enter_play_mode first.");
            if (!McpCommandBridge.Exists)
                return (false, "BRIDGE_NOT_INSTALLED: no McpCommandBridge in the active scene — call bridge_install first.");
            return (true, null);
        }

        public static async Task<BridgeScript> SubmitAndAwait(
            BridgeScript script, double timeoutSeconds)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            script.Id = Interlocked.Increment(ref _nextScriptId);

            await MainThreadDispatcher.RunAsync(() =>
            {
                if (McpCommandBridge.Instance == null) return;
                McpCommandBridge.Instance.Submit(script);
            });

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (script.IsComplete)
                {
                    EditorApplication.update -= poll;
                    tcs.TrySetResult(true);
                }
            };
            await MainThreadDispatcher.RunAsync(() => EditorApplication.update += poll);

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (winner != tcs.Task)
            {
                await MainThreadDispatcher.RunAsync(() => EditorApplication.update -= poll);
                throw new TimeoutException(
                    $"bridge script #{script.Id} did not complete within {timeoutSeconds}s — " +
                    $"completed {script.Results.Count}/{script.Steps.Count} steps (started={script.StartedExecution})");
            }
            return script;
        }
    }
}
