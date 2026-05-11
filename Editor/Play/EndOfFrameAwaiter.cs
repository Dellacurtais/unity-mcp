using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Spawns a hidden runtime GameObject whose coroutine yields one
    /// `WaitForEndOfFrame`, then invokes a callback on the main thread and
    /// destroys itself. Used by screenshot tools to capture the post-process
    /// pass in URP/HDRP — calling Camera.Render() synchronously from editor
    /// code typically skips post-fx in scriptable render pipelines, but a
    /// coroutine awaited after end-of-frame sees the final back buffer.
    ///
    /// Only works in play mode (no player loop in edit mode means coroutines
    /// don't tick). For edit-mode capture, fall back to a direct Camera.Render().
    public sealed class EndOfFrameAwaiter : MonoBehaviour
    {
        private Action _callback;

        public static Task WaitOneEndOfFrame()
        {
            if (!Application.isPlaying)
                return Task.CompletedTask; // caller must use sync render path

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var go = new GameObject("__SceneAnalyzer_EndOfFrameAwaiter")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var awaiter = go.AddComponent<EndOfFrameAwaiter>();
            awaiter._callback = () => tcs.TrySetResult(true);
            awaiter.StartCoroutine(awaiter.Run());
            return tcs.Task;
        }

        private IEnumerator Run()
        {
            yield return new WaitForEndOfFrame();
            try { _callback?.Invoke(); }
            finally
            {
                if (this != null && gameObject != null)
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
    }
}
