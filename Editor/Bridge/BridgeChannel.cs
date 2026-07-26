using System;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Bridge
{
    internal static class BridgeToolSchemas
    {
        public static JObject Object(JObject properties) => new JObject
        {
            ["type"] = "object",
            ["properties"] = properties ?? new JObject(),
            ["additionalProperties"] = true,
        };

        public static JObject Install => Object(new JObject
        {
            ["installed"] = new JObject { ["type"] = "boolean" },
            ["alreadyExisted"] = new JObject { ["type"] = "boolean" },
            ["sceneObjectInstanceId"] = new JObject { ["type"] = "integer" },
            ["dontDestroyOnLoad"] = new JObject { ["type"] = "boolean" },
        });

        public static JObject Status => Object(new JObject
        {
            ["installed"] = new JObject { ["type"] = "boolean" },
            ["runtimeActive"] = new JObject { ["type"] = "boolean" },
            ["sceneObjectInstanceId"] = new JObject { ["type"] = new JArray { "integer", "null" } },
            ["registeredHandlers"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            ["queuedScripts"] = new JObject { ["type"] = "integer" },
            ["activeScripts"] = new JObject { ["type"] = "integer" },
            ["runningScriptId"] = new JObject { ["type"] = "integer" },
            ["lastDispatch"] = new JObject { ["type"] = new JArray { "object", "null" } },
        });

        public static JObject Handlers => Object(new JObject
        {
            ["handlers"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
        });

        public static JObject Command => Object(new JObject
        {
            ["accepted"] = new JObject { ["type"] = "boolean" },
            ["completed"] = new JObject { ["type"] = "boolean" },
            ["ok"] = new JObject { ["type"] = "boolean" },
            ["durationMs"] = new JObject { ["type"] = "number" },
            ["frameAtStart"] = new JObject { ["type"] = "integer" },
            ["frameAtEnd"] = new JObject { ["type"] = "integer" },
            ["script"] = new JObject { ["type"] = "object" },
        });

        public static JObject Commands => Object(new JObject
        {
            ["totalSteps"] = new JObject { ["type"] = "integer" },
            ["executedSteps"] = new JObject { ["type"] = "integer" },
            ["okSteps"] = new JObject { ["type"] = "integer" },
            ["stoppedEarly"] = new JObject { ["type"] = "boolean" },
            ["scriptStartSeq"] = new JObject { ["type"] = "integer" },
            ["scriptEndSeq"] = new JObject { ["type"] = "integer" },
            ["script"] = new JObject { ["type"] = "object" },
            ["steps"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
        });
    }

    public static class BridgeChannel
    {
        private static int _nextScriptId;

        public static Task<(bool ok, string error)> PrecheckAsync(bool requirePlayMode)
        {
            return MainThreadDispatcher.RunAsync(() =>
            {
                if (requirePlayMode && EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
                    return (false, "TRANSITIONING: wait for the play-mode transition to settle before dispatching commands.");
                if (requirePlayMode && !EditorApplication.isPlaying)
                    return (false, "NOT_PLAYING: bridge handlers only run during play mode; call enter_play_mode first.");
                if (requirePlayMode && EditorApplication.isPaused)
                    return (false, "PLAY_PAUSED: bridge scripts advance from MonoBehaviour.Update; resume play mode before dispatching commands.");
                if (!McpCommandBridge.Exists)
                    return (false, "BRIDGE_NOT_ACTIVE: no active McpCommandBridge exists in the Play Mode scene; install it in Edit Mode and enter play mode.");
                return (true, (string)null);
            });
        }

        public static async Task<BridgeScript> Submit(
            BridgeScript script, CancellationToken cancellationToken = default)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            cancellationToken.ThrowIfCancellationRequested();
            script.Id = Interlocked.Increment(ref _nextScriptId);
            string submitError = null;
            var submitted = await MainThreadDispatcher.RunAsync(() =>
            {
                var bridge = McpCommandBridge.Instance;
                if (bridge == null) { submitError = "BRIDGE_DISAPPEARED"; return false; }
                return bridge.TrySubmit(script, out submitError);
            }, cancellationToken);
            if (submitted) return script;
            script.TrySetTerminal(BridgeScriptStatus.Failed, submitError ?? "BRIDGE_SUBMIT_FAILED");
            throw new InvalidOperationException(submitError ?? "BRIDGE_SUBMIT_FAILED");
        }

        public static async Task<BridgeScript> SubmitAndAwait(
            BridgeScript script, double timeoutSeconds, CancellationToken cancellationToken = default)
        {
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            var completion = new TaskCompletionSource<BridgeScript>(TaskCreationOptions.RunContinuationsAsynchronously);
            script.CompletionCallback = terminal => completion.TrySetResult(terminal);
            await Submit(script, cancellationToken);

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (linked.Token.Register(() => cancelled.TrySetResult(true)))
                {
                    var winner = await Task.WhenAny(completion.Task, cancelled.Task);
                    if (winner == completion.Task) return await completion.Task;
                }

                var requestCancelled = cancellationToken.IsCancellationRequested;
                var reason = requestCancelled ? "MCP_REQUEST_CANCELLED" : "EDITOR_TIMEOUT";
                try { await MainThreadDispatcher.RunAsync(() => McpCommandBridge.Instance?.Cancel(script.Id, reason)); }
                catch { }
                script.ClearCompletionCallback();
                if (completion.Task.IsCompleted) return await completion.Task;
                if (requestCancelled) throw new OperationCanceledException(cancellationToken);
                throw new TimeoutException(
                    $"bridge script #{script.Id} exceeded {timeoutSeconds:F1}s; status={script.Status}, " +
                    $"results={script.Results.Count}/{script.Steps.Count}; cancellation requested.");
            }
        }
    }
}
