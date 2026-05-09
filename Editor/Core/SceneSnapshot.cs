using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// In-memory mirror of the active scene keyed by InstanceID. Refreshed lazily by
    /// ChangeWatcher; tools and analyzers read from it instead of walking the scene
    /// each call. Main-thread only — no locking.
    public sealed class SceneSnapshot
    {
        private readonly Dictionary<int, GameObjectInfo> _byId = new();
        private string _sceneName;
        private string _scenePath;
        private bool _stale = true;

        public int Count => _byId.Count;
        public bool IsStale => _stale;
        public string SceneName => _sceneName;
        public string ScenePath => _scenePath;

        public IEnumerable<GameObjectInfo> All() => _byId.Values;

        public GameObjectInfo Get(int instanceId)
        {
            return _byId.TryGetValue(instanceId, out var info) ? info : null;
        }

        public void MarkStale() => _stale = true;

        public void RefreshIfNeeded()
        {
            if (!_stale) return;
            Refresh();
            _stale = false;
        }

        public void Refresh()
        {
            _byId.Clear();
            var active = SceneManager.GetActiveScene();
            _sceneName = active.name;
            _scenePath = active.path;
            if (!active.IsValid() || !active.isLoaded) return;

            foreach (var root in active.GetRootGameObjects())
            {
                Capture(root, parentId: 0);
            }
        }

        private void Capture(GameObject go, int parentId)
        {
            var info = new GameObjectInfo
            {
                InstanceID = go.GetInstanceID(),
                Name = go.name,
                ParentId = parentId,
                LocalPosition = go.transform.localPosition,
                LocalRotation = go.transform.localRotation,
                LocalScale = go.transform.localScale,
                IsActive = go.activeInHierarchy,
                IsStatic = go.isStatic,
                Components = new List<ComponentInfo>(),
                ChildIds = new List<int>(),
            };

            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue; // missing script
                var ci = new ComponentInfo
                {
                    InstanceID = c.GetInstanceID(),
                    TypeName = c.GetType().FullName,
                };
                if (c is Behaviour b) ci.Enabled = b.enabled;
                else ci.Enabled = true;
                info.Components.Add(ci);
            }

            _byId[info.InstanceID] = info;

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                info.ChildIds.Add(child.GetInstanceID());
                Capture(child, info.InstanceID);
            }
        }

        public string GetPath(int instanceId)
        {
            var info = Get(instanceId);
            if (info == null) return null;
            var stack = new Stack<string>();
            stack.Push(info.Name);
            while (info.ParentId != 0)
            {
                info = Get(info.ParentId);
                if (info == null) break;
                stack.Push(info.Name);
            }
            return string.Join("/", stack);
        }
    }

    public sealed class GameObjectInfo
    {
        public int InstanceID;
        public string Name;
        public int ParentId;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 LocalScale;
        public bool IsActive;
        public bool IsStatic;
        public List<ComponentInfo> Components;
        public List<int> ChildIds;
    }

    public sealed class ComponentInfo
    {
        public int InstanceID;
        public string TypeName;
        public bool Enabled;
    }
}
