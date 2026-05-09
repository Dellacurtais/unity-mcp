using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

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
            // Drain a bounded batch per editor frame to avoid stalling the editor
            // when the queue gets long under heavy MCP load.
            const int budget = 32;
            for (int i = 0; i < budget; i++)
            {
                Action a;
                lock (Lock)
                {
                    if (Queue.Count == 0) return;
                    a = Queue.Dequeue();
                }
                try { a(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
