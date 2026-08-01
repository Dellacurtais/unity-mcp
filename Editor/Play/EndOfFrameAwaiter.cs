using System;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Schedules work in the player's real end-of-frame phase. The coroutine
    /// host lives in the runtime assembly because Unity refuses to attach a
    /// MonoBehaviour compiled into an Editor-only assembly while playing.
    public static class EndOfFrameAwaiter
    {
        public static async Task WaitOneEndOfFrame(CancellationToken cancellationToken)
        {
            bool isPlaying = await MainThreadDispatcher.RunAsync(
                () => Application.isPlaying, cancellationToken);
            if (!isPlaying) return;
            await RunAtEndOfFrame(() => true, cancellationToken);
        }

        /// <summary>
        /// Runs <paramref name="operation"/> on Unity's main thread immediately
        /// after <see cref="WaitForEndOfFrame"/>. This is intentionally different
        /// from waiting and dispatching afterwards: that continuation normally
        /// runs during a later Editor update, outside the only frame phase where
        /// ScreenCapture is guaranteed to contain late UI.
        /// </summary>
        public static async Task<T> RunAtEndOfFrame<T>(Func<T> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            cancellationToken.ThrowIfCancellationRequested();

            Task<object> completionTask = null;
            await MainThreadDispatcher.RunAsync(() =>
            {
                if (!Application.isPlaying)
                    throw new InvalidOperationException("end-of-frame operations require Play Mode");

                var completion = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var go = new GameObject("__SceneAnalyzer_EndOfFrameOperation")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                var host = go.AddComponent<EndOfFrameOperationHost>();
                CancellationTokenRegistration cancellation = default;

                void FinishAborted()
                {
                    cancellation.Dispose();
                    completion.TrySetCanceled();
                }

                void FinishAtEndOfFrame()
                {
                    try
                    {
                        if (!completion.Task.IsCompleted)
                            completion.TrySetResult(operation());
                    }
                    catch (Exception e)
                    {
                        completion.TrySetException(e);
                    }
                    finally
                    {
                        cancellation.Dispose();
                    }
                }

                host.Schedule(FinishAtEndOfFrame, FinishAborted);
                if (cancellationToken.CanBeCanceled)
                    cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                completionTask = completion.Task;
            }, cancellationToken);

            return (T)await completionTask;
        }
    }
}
