using System;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Helper that wraps EditorApplication.playModeStateChanged into a
    /// TaskCompletionSource so tools can `await` a specific transition
    /// without blocking the dispatcher pump.
    ///
    /// Critical detail: the playModeStateChanged callback fires on the main
    /// thread. The TCS itself is thread-safe; we use RunContinuationsAsynchronously
    /// so the awaiter doesn't hijack the editor update loop on completion.
    internal static class PlayToolSchemas
    {
        public static JObject Object(JObject properties) => new JObject
        {
            ["type"] = "object",
            ["properties"] = properties ?? new JObject(),
            ["additionalProperties"] = true,
        };

        public static JObject Enter => Object(new JObject
        {
            ["entered"] = new JObject { ["type"] = "boolean" },
            ["alreadyPlaying"] = new JObject { ["type"] = "boolean" },
            ["paused"] = new JObject { ["type"] = "boolean" },
            ["frameCount"] = new JObject { ["type"] = "integer" },
            ["waitForFirstFrameRequested"] = new JObject { ["type"] = "boolean" },
            ["waitedForFirstFrame"] = new JObject { ["type"] = "boolean" },
        });

        public static JObject Exit => Object(new JObject
        {
            ["entered"] = new JObject { ["type"] = "boolean" },
            ["alreadyEditMode"] = new JObject { ["type"] = "boolean" },
        });

        public static JObject Paused => Object(new JObject
        {
            ["paused"] = new JObject { ["type"] = "boolean" },
            ["wasAlreadySet"] = new JObject { ["type"] = "boolean" },
        });

        public static JObject Step => Object(new JObject
        {
            ["startFrame"] = new JObject { ["type"] = "integer" },
            ["endFrame"] = new JObject { ["type"] = "integer" },
            ["delta"] = new JObject { ["type"] = "integer" },
        });

        public static JObject TimeScale => Object(new JObject
        {
            ["previous"] = new JObject { ["type"] = "number" },
            ["current"] = new JObject { ["type"] = "number" },
            ["resetOnExit"] = new JObject { ["type"] = "boolean" },
        });

        public static JObject State => Object(new JObject
        {
            ["isPlaying"] = new JObject { ["type"] = "boolean" },
            ["isPaused"] = new JObject { ["type"] = "boolean" },
            ["isCompiling"] = new JObject { ["type"] = "boolean" },
            ["isUpdating"] = new JObject { ["type"] = "boolean" },
            ["isTransitioning"] = new JObject { ["type"] = "boolean" },
            ["transitionDirection"] = new JObject { ["type"] = new JArray { "string", "null" } },
            ["applicationIsPlaying"] = new JObject { ["type"] = "boolean" },
            ["frameCount"] = new JObject { ["type"] = "integer" },
            ["time"] = new JObject { ["type"] = "number" },
            ["timeScale"] = new JObject { ["type"] = "number" },
        });

        public static JObject Logs => Object(new JObject
        {
            ["entries"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["currentSeq"] = new JObject { ["type"] = "integer" },
            ["returnedCount"] = new JObject { ["type"] = "integer" },
        });

        public static JObject Anchor => Object(new JObject
        {
            ["seq"] = new JObject { ["type"] = "integer" },
        });
    }

    public static class PlayModeWaiter
    {
        public static async Task<bool> WaitFor(PlayModeStateChange target, double timeoutSeconds)
        {
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PlayModeStateChange> handler = null;
            handler = change =>
            {
                if (change != target) return;
                EditorApplication.playModeStateChanged -= handler;
                tcs.TrySetResult(true);
            };

            var already = await MainThreadDispatcher.RunAsync(() =>
            {
                if (MatchesCurrentState(target)) return true;
                EditorApplication.playModeStateChanged += handler;
                return false;
            });
            if (already) return true;

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (winner == tcs.Task) return true;
            await MainThreadDispatcher.RunAsync(() => EditorApplication.playModeStateChanged -= handler);
            return false;
        }

        public static async Task<bool> WaitForFrameAtLeast(int minimumFrame, double timeoutSeconds)
        {
            if (minimumFrame < 0) throw new ArgumentOutOfRangeException(nameof(minimumFrame));
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.CallbackFunction onUpdate = null;
            onUpdate = () =>
            {
                if (!EditorApplication.isPlaying || Time.frameCount >= minimumFrame)
                {
                    EditorApplication.update -= onUpdate;
                    tcs.TrySetResult(EditorApplication.isPlaying && Time.frameCount >= minimumFrame);
                }
            };
            var already = await MainThreadDispatcher.RunAsync(() =>
            {
                if (!EditorApplication.isPlaying || Time.frameCount >= minimumFrame)
                    return EditorApplication.isPlaying && Time.frameCount >= minimumFrame;
                EditorApplication.update += onUpdate;
                return false;
            });
            if (already) return true;

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (winner == tcs.Task) return await tcs.Task;
            await MainThreadDispatcher.RunAsync(() => EditorApplication.update -= onUpdate);
            return false;
        }

        public static async Task WaitOneUpdateTick()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.CallbackFunction onUpdate = null;
            onUpdate = () =>
            {
                EditorApplication.update -= onUpdate;
                tcs.TrySetResult(true);
            };
            await MainThreadDispatcher.RunAsync(() => EditorApplication.update += onUpdate);
            await tcs.Task;
        }

        private static bool MatchesCurrentState(PlayModeStateChange target)
        {
            // isPlayingOrWillChangePlaymode is true while a transition is queued —
            // we only return true when we're in the stable target state, never
            // mid-transition. That guarantees the caller sees a real transition.
            switch (target)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    return EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode;
                case PlayModeStateChange.EnteredEditMode:
                    return !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode;
                // ExitingPlayMode / ExitingEditMode are transient — never "already in".
                default:
                    return false;
            }
        }
    }
}
