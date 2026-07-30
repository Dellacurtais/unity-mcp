using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Spawns a hidden runtime GameObject whose coroutine yields one
    /// `WaitForEndOfFrame`, then completes on the main thread and destroys
    /// itself. Screenshot tools use this to defer their explicit render until
    /// the current player frame has finished. Cancellation and Play Mode exit
    /// both complete the returned task instead of leaving a pending request.
    ///
    /// Only works in play mode (no player loop in edit mode means coroutines
    /// don't tick). Edit-mode capture uses a synchronous render path.
    public sealed class EndOfFrameAwaiter : MonoBehaviour
    {
        private TaskCompletionSource<bool> _completion;
        private CancellationTokenRegistration _cancellation;
        private bool _completed;

        public static async Task WaitOneEndOfFrame(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            Task completionTask = null;
            await MainThreadDispatcher.RunAsync(() =>
            {
                if (!Application.isPlaying)
                {
                    completionTask = Task.CompletedTask;
                    return;
                }

                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var go = new GameObject("__SceneAnalyzer_EndOfFrameAwaiter")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                var awaiter = go.AddComponent<EndOfFrameAwaiter>();
                awaiter._completion = completion;
                if (cancellationToken.CanBeCanceled)
                    awaiter._cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                awaiter.StartCoroutine(awaiter.Run());
                completionTask = completion.Task;
            }, cancellationToken);
            await completionTask;
        }

        private IEnumerator Run()
        {
            yield return new WaitForEndOfFrame();
            Complete();
        }

        private void OnDestroy()
        {
            _cancellation.Dispose();
            if (!_completed) _completion?.TrySetCanceled();
        }

        private void Complete()
        {
            if (_completed) return;
            _completed = true;
            _cancellation.Dispose();
            _completion?.TrySetResult(true);
            if (this != null && gameObject != null)
                UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }
}
