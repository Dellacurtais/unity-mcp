using System;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Helper that wraps EditorApplication.playModeStateChanged into a
    /// TaskCompletionSource so tools can `await` a specific transition
    /// without blocking the dispatcher pump.
    ///
    /// Critical detail: the playModeStateChanged callback fires on the main
    /// thread. The TCS itself is thread-safe; we use RunContinuationsAsynchronously
    /// so the awaiter doesn't hijack the editor update loop on completion.
    public static class PlayModeWaiter
    {
        /// Waits for the editor to transition into `target` (e.g.
        /// EnteredPlayMode / EnteredEditMode). Returns true on transition,
        /// false on timeout. If the editor is already in the target state
        /// at call time, completes immediately.
        public static async Task<bool> WaitFor(PlayModeStateChange target, double timeoutSeconds)
        {
            // Fast path — check current state on the main thread first.
            bool already = await MainThreadDispatcher.RunAsync(() => MatchesCurrentState(target));
            if (already) return true;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PlayModeStateChange> handler = null;
            handler = (change) =>
            {
                if (change == target)
                {
                    EditorApplication.playModeStateChanged -= handler;
                    tcs.TrySetResult(true);
                }
            };

            await MainThreadDispatcher.RunAsync(() =>
            {
                EditorApplication.playModeStateChanged += handler;
            });

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (winner != tcs.Task)
            {
                await MainThreadDispatcher.RunAsync(() =>
                {
                    EditorApplication.playModeStateChanged -= handler;
                });
                return false;
            }
            return true;
        }

        /// Waits one EditorApplication.update tick after a play-mode transition.
        /// EnteredPlayMode fires before the first frame's Update runs, so
        /// Time.frameCount is still 0 in the same callback. One tick advances
        /// the player loop and frameCount becomes >= 1.
        public static async Task WaitOneUpdateTick()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.CallbackFunction onUpdate = null;
            onUpdate = () =>
            {
                EditorApplication.update -= onUpdate;
                tcs.TrySetResult(true);
            };
            await MainThreadDispatcher.RunAsync(() =>
            {
                EditorApplication.update += onUpdate;
            });
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
