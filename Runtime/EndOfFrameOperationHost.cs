using System;
using System.Collections;
using UnityEngine;

namespace Maicon.SceneAnalyzer
{
    /// Runtime coroutine host used by Editor tooling that must execute after
    /// all cameras and late UI have rendered. Hidden objects using this type
    /// are transient and never become part of a saved scene.
    [AddComponentMenu("")]
    public sealed class EndOfFrameOperationHost : MonoBehaviour
    {
        private Action _operation;
        private Action _aborted;
        private bool _finished;

        public void Schedule(Action operation, Action aborted)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            _aborted = aborted;
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            yield return new WaitForEndOfFrame();
            _finished = true;
            try
            {
                _operation();
            }
            finally
            {
                if (this != null && gameObject != null)
                    Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (!_finished) _aborted?.Invoke();
        }
    }
}
