using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    /// <summary>
    /// Keeps Unity's editor/player loop moving while an MCP operation depends on
    /// update ticks and temporarily activates the Scene/Game view required by a
    /// tool. The previous Unity window is restored only when the lease still owns
    /// focus, so a user action made during the tool is never overwritten.
    /// </summary>
    [InitializeOnLoad]
    public sealed class EditorActivityLease
    {
        private static int _activeUpdateLeases;
        private static double _nextPulse;
        private static double _nextRepaint;
        private static DateTimeOffset? _lastActivationUtc;
        private static string _lastActivatedWindow;

        private readonly McpExecutionRequirement _requirements;
        private EditorWindow _previousWindow;
        private EditorWindow _requiredWindow;
        private bool _updateLease;
        private bool _released;

        static EditorActivityLease()
        {
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        private EditorActivityLease(McpExecutionRequirement requirements)
        {
            _requirements = requirements;
        }

        public static int ActiveCount => Math.Max(0, Volatile.Read(ref _activeUpdateLeases));
        public static DateTimeOffset? LastActivationUtc => _lastActivationUtc;
        public static string LastActivatedWindow => _lastActivatedWindow;

        public static async Task<EditorActivityLease> AcquireAsync(
            ToolDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            var requirements = descriptor?.ExecutionRequirements ?? McpExecutionRequirement.MainThread;
            var lease = new EditorActivityLease(requirements);
            if ((requirements & McpExecutionRequirement.EditorUpdates) != 0)
            {
                lease._updateLease = true;
                Interlocked.Increment(ref _activeUpdateLeases);
            }

            try
            {
                await MainThreadDispatcher.RunAsync(lease.ActivateOnMainThread, cancellationToken);
                return lease;
            }
            catch
            {
                if (lease._updateLease) Interlocked.Decrement(ref _activeUpdateLeases);
                throw;
            }
        }

        public async Task ReleaseAsync()
        {
            if (_released) return;
            _released = true;
            if (_updateLease) Interlocked.Decrement(ref _activeUpdateLeases);

            try
            {
                await MainThreadDispatcher.RunAsync(() =>
                {
                    if (_requiredWindow != null
                        && EditorWindow.focusedWindow == _requiredWindow
                        && _previousWindow != null
                        && _previousWindow != _requiredWindow)
                    {
                        _previousWindow.Focus();
                    }
                    PulseEditor(repaint: true);
                }, CancellationToken.None);
            }
            catch
            {
                // Domain reload or editor shutdown can invalidate window handles.
            }
        }

        private void ActivateOnMainThread()
        {
            PulseEditor(repaint: true);
            if (!Settings.AutoActivateRequiredWindows || UnityEngine.Application.isBatchMode) return;

            _previousWindow = EditorWindow.focusedWindow;
            if ((_requirements & McpExecutionRequirement.SceneView) != 0)
            {
                _requiredWindow = SceneView.lastActiveSceneView
                    ?? EditorWindow.GetWindow<SceneView>(false, "Scene", false);
            }
            else if ((_requirements & McpExecutionRequirement.GameView) != 0)
            {
                var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType != null)
                    _requiredWindow = EditorWindow.GetWindow(gameViewType, false, "Game", false);
            }

            if (_requiredWindow == null) return;
            _requiredWindow.Show();
            _requiredWindow.Repaint();
            _requiredWindow.Focus();
            _lastActivationUtc = DateTimeOffset.UtcNow;
            _lastActivatedWindow = _requiredWindow.GetType().Name;
        }

        private static void Pump()
        {
            if (ActiveCount <= 0) return;
            var now = EditorApplication.timeSinceStartup;
            if (now >= _nextPulse)
            {
                _nextPulse = now + 0.05d;
                EditorApplication.QueuePlayerLoopUpdate();
            }
            if (now >= _nextRepaint)
            {
                _nextRepaint = now + 0.25d;
                InternalEditorUtility.RepaintAllViews();
            }
        }

        private static void PulseEditor(bool repaint)
        {
            EditorApplication.QueuePlayerLoopUpdate();
            if (repaint) InternalEditorUtility.RepaintAllViews();
        }
    }
}
