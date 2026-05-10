using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    /// Marshals work from background threads (the HTTP listener) onto the Unity
    /// editor main thread. Tools call EditorApplication / UnityEngine APIs which
    /// must run on the main thread; the listener thread enqueues tasks here and
    /// awaits completion via TaskCompletionSource.
    [InitializeOnLoad]
    public static class MainThreadDispatcher
    {
        private static readonly Queue<Action> Queue = new();
        private static readonly object Lock = new();

        /// Time budget per editor frame for draining the queue. ~5ms ≈ a third
        /// of a 60fps frame — drains as much as possible without taxing the
        /// editor. Replaces the older fixed-count budget which couldn't keep up
        /// when bursts of analyzer/changeset work hit the queue.
        private const double MaxPumpMs = 5.0;

        /// If a single dispatched closure exceeds this, we log a warning so
        /// it's obvious which tool is hogging the main thread. The tool ran
        /// to completion either way — this is purely diagnostic. 200ms is the
        /// threshold a frame stutter becomes visible to the user; below that
        /// the heavier analyzers / GetComponentsInChildren calls are common
        /// and noisy to warn about.
        private const double SlowClosureWarnMs = 200.0;

        static MainThreadDispatcher()
        {
            EditorApplication.update += Pump;
        }

        public static Task<T> RunAsync<T>(Func<T> fn)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Lock)
            {
                Queue.Enqueue(() =>
                {
                    try { tcs.SetResult(fn()); }
                    catch (Exception e) { tcs.SetException(e); }
                });
            }
            return tcs.Task;
        }

        public static Task RunAsync(Action action)
        {
            return RunAsync<bool>(() => { action(); return true; });
        }

        private static void Pump()
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < MaxPumpMs)
            {
                Action a;
                lock (Lock)
                {
                    if (Queue.Count == 0) return;
                    a = Queue.Dequeue();
                }
                var closureStart = sw.Elapsed.TotalMilliseconds;
                try { a(); }
                catch (Exception e) { Debug.LogException(e); }
                var closureMs = sw.Elapsed.TotalMilliseconds - closureStart;
                if (closureMs > SlowClosureWarnMs)
                    Debug.LogWarning($"[scene-analyzer] dispatcher closure took {closureMs:F1}ms — " +
                                     "tool is doing heavy work on the main thread; consider async.");
            }
        }
    }
}
