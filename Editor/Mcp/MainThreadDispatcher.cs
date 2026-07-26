using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    [InitializeOnLoad]
    public static class MainThreadDispatcher
    {
        private sealed class WorkItem
        {
            public Action Execute;
            public Action Cancel;
        }

        private static readonly Queue<WorkItem> Queue = new Queue<WorkItem>();
        private static readonly object Lock = new object();
        private static readonly int MainThreadId;
        private const double MaxPumpMs = 5.0;
        private const double SlowClosureWarnMs = 200.0;

        static MainThreadDispatcher()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelPending;
            AssemblyReloadEvents.beforeAssemblyReload += CancelPending;
            EditorApplication.quitting -= CancelPending;
            EditorApplication.quitting += CancelPending;
        }

        public static int PendingCount { get { lock (Lock) return Queue.Count; } }

        public static Task<T> RunAsync<T>(Func<T> fn, CancellationToken cancellationToken = default)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
            if (Thread.CurrentThread.ManagedThreadId == MainThreadId)
            {
                try { return Task.FromResult(fn()); }
                catch (Exception e) { return Task.FromException<T>(e); }
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = default;
            var item = new WorkItem();
            item.Cancel = () => tcs.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
            item.Execute = () =>
            {
                registration.Dispose();
                if (cancellationToken.IsCancellationRequested) { tcs.TrySetCanceled(cancellationToken); return; }
                try { tcs.TrySetResult(fn()); }
                catch (Exception e) { tcs.TrySetException(e); }
            };
            if (cancellationToken.CanBeCanceled) registration = cancellationToken.Register(item.Cancel);
            lock (Lock) Queue.Enqueue(item);
            return tcs.Task;
        }

        public static Task RunAsync(Action action, CancellationToken cancellationToken = default)
        {
            return RunAsync(() => { action(); return true; }, cancellationToken);
        }

        public static void CancelPending()
        {
            WorkItem[] pending;
            lock (Lock)
            {
                pending = Queue.ToArray();
                Queue.Clear();
            }
            foreach (var item in pending)
            {
                try { item.Cancel?.Invoke(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        private static void Pump()
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < MaxPumpMs)
            {
                WorkItem item;
                lock (Lock)
                {
                    if (Queue.Count == 0) return;
                    item = Queue.Dequeue();
                }
                var start = sw.Elapsed.TotalMilliseconds;
                try { item.Execute(); }
                catch (Exception e) { Debug.LogException(e); }
                var elapsed = sw.Elapsed.TotalMilliseconds - start;
                if (elapsed > SlowClosureWarnMs)
                    Debug.LogWarning($"[unity-mcp] dispatcher closure took {elapsed:F1}ms; move long work to a cooperative async tool.");
            }
        }
    }
}
