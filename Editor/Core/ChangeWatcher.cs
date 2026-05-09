using System;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// Subscribes to editor scene events and triggers a debounced refresh of the
    /// shared SceneSnapshot. v1 strategy: any event marks the whole snapshot
    /// stale; refresh runs once per debounce window. Granular invalidation via
    /// ObjectChangeEvents.changesPublished can be added later.
    public sealed class ChangeWatcher : IDisposable
    {
        private const double DebounceSeconds = 0.25;

        private readonly SceneSnapshot _snapshot;
        private readonly Action _onChanged;
        private double _lastEventTime;
        private bool _scheduled;
        private bool _disposed;

        public ChangeWatcher(SceneSnapshot snapshot, Action onChanged)
        {
            _snapshot = snapshot;
            _onChanged = onChanged;

            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            Undo.postprocessModifications += OnUndoModifications;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
            Undo.postprocessModifications -= OnUndoModifications;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        public void ForceRefresh()
        {
            _snapshot.MarkStale();
            FireChanged();
        }

        private void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode) => Trigger();
        private void OnSceneSaved(UnityEngine.SceneManagement.Scene scene) => Trigger();
        private void OnHierarchyChanged() => Trigger();
        private void OnObjectChanges(ref ObjectChangeEventStream stream) => Trigger();

        private UndoPropertyModification[] OnUndoModifications(UndoPropertyModification[] mods)
        {
            Trigger();
            return mods;
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            // Refresh on entering/exiting edit mode so the snapshot reflects whatever
            // edit-time state was preserved across the play session.
            if (change == PlayModeStateChange.EnteredEditMode) Trigger();
        }

        private void Trigger()
        {
            _lastEventTime = EditorApplication.timeSinceStartup;
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.update += DebouncePoll;
        }

        private void DebouncePoll()
        {
            if (EditorApplication.timeSinceStartup - _lastEventTime < DebounceSeconds) return;
            EditorApplication.update -= DebouncePoll;
            _scheduled = false;
            _snapshot.MarkStale();
            FireChanged();
        }

        private void FireChanged()
        {
            try { _onChanged?.Invoke(); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }
}
