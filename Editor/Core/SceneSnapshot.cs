using Maicon.SceneAnalyzer.Editor.Core;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// In-memory mirror of all loaded scenes (active + additive) keyed by
    /// InstanceID. Explicit MCP tools refresh it lazily on the main thread; no
    /// automatic hierarchy or Play Mode watcher traverses the scene.
    public sealed class SceneSnapshot
    {
        private readonly Dictionary<int, GameObjectInfo> _byId = new();
        private readonly Dictionary<int, RendererSummary> _renderers = new();
        private readonly List<LoadedScene> _loadedScenes = new();
        private string _sceneName;
        private string _scenePath;
        private bool _stale = true;
        private long _revision;

        public int Count => _byId.Count;
        public bool IsStale => _stale;
        public string SceneName => _sceneName;
        public string ScenePath => _scenePath;
        public IReadOnlyList<LoadedScene> LoadedScenes => _loadedScenes;

        /// Monotonic counter bumped whenever the snapshot is invalidated or refreshed.
        public long Revision => _revision;

        public IEnumerable<GameObjectInfo> All() => _byId.Values;

        public GameObjectInfo Get(int instanceId)
        {
            return _byId.TryGetValue(instanceId, out var info) ? info : null;
        }

        /// Cached renderer surface keyed by Renderer InstanceID. Populated during
        /// Refresh; null when the GO has no renderer or the renderer was missing
        /// at capture time.
        public RendererSummary GetRenderer(int rendererInstanceId)
            => _renderers.TryGetValue(rendererInstanceId, out var r) ? r : null;

        public IEnumerable<KeyValuePair<int, RendererSummary>> AllRenderers() => _renderers;

        public void MarkStale()
        {
            _stale = true;
            unchecked { _revision++; }
        }

        public void RefreshIfNeeded()
        {
            if (!_stale) return;
            Refresh();
            _stale = false;
        }

        public void Refresh()
        {
            _byId.Clear();
            _renderers.Clear();
            _loadedScenes.Clear();

            var active = SceneManager.GetActiveScene();
            _sceneName = active.name;
            _scenePath = active.path;
            unchecked { _revision++; }

            // Settings flag is read once per Refresh — flipping it mid-refresh
            // would split the snapshot between with/without renderer cache.
            var includeRenderers = Settings.SnapshotIncludeRendererSummary;

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var sceneGuid = string.IsNullOrEmpty(scene.path) ? null : AssetDatabase.AssetPathToGUID(scene.path);
                _loadedScenes.Add(new LoadedScene
                {
                    Name = scene.name,
                    Path = scene.path,
                    Guid = sceneGuid,
                    Handle = scene.GetMcpSceneHandle(),
                    IsActive = scene == active,
                    IsPrefabStage = false,
                });
                foreach (var root in scene.GetRootGameObjects())
                {
                    Capture(root, parentId: 0, scene: scene, isPrefabStage: false, includeRenderers: includeRenderers);
                }
            }

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.scene.IsValid()
                && !_loadedScenes.Exists(x => x.Handle == prefabStage.scene.GetMcpSceneHandle()))
            {
                var stageScene = prefabStage.scene;
                _loadedScenes.Add(new LoadedScene
                {
                    Name = stageScene.name,
                    Path = prefabStage.assetPath,
                    Guid = string.IsNullOrEmpty(prefabStage.assetPath) ? null : AssetDatabase.AssetPathToGUID(prefabStage.assetPath),
                    Handle = stageScene.GetMcpSceneHandle(),
                    IsActive = false,
                    IsPrefabStage = true,
                });
                foreach (var root in stageScene.GetRootGameObjects())
                    Capture(root, parentId: 0, scene: stageScene, isPrefabStage: true, includeRenderers: includeRenderers);
            }
        }

        private void Capture(GameObject go, int parentId, Scene scene, bool isPrefabStage, bool includeRenderers)
        {
            var staticFlags = GameObjectUtility.GetStaticEditorFlags(go);
            var capturedTarget = ToolTargetRef.Capture(go);
            var info = new GameObjectInfo
            {
                InstanceID = go.GetMcpInstanceId(),
                Name = go.name,
                ParentId = parentId,
                SceneName = scene.name,
                ScenePath = capturedTarget.ScenePath,
                SceneGuid = capturedTarget.SceneGuid,
                SceneHandle = scene.GetMcpSceneHandle(),
                HierarchyPath = ToolTargetRef.BuildHierarchyPath(go.transform),
                GlobalObjectId = capturedTarget.GlobalObjectId,
                CaptureDomainId = capturedTarget.CaptureDomainId,
                IsPrefabStage = isPrefabStage,
                LocalPosition = go.transform.localPosition,
                LocalRotation = go.transform.localRotation,
                LocalScale = go.transform.localScale,
                WorldPosition = go.transform.position,
                WorldRotation = go.transform.rotation,
                Tag = go.tag,
                Layer = go.layer,
                IsActive = go.activeInHierarchy,
                IsStatic = go.isStatic,
                StaticFlags = staticFlags,
                Components = new List<ComponentInfo>(),
                ChildIds = new List<int>(),
            };

            // Prefab info: capture even if asset is missing so authoring tools
            // can detect broken prefab links without a separate scan.
            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                info.PrefabAssetPath = source != null ? AssetDatabase.GetAssetPath(source) : null;
                info.PrefabStatus = PrefabUtility.IsPrefabAssetMissing(go)
                    ? PrefabInstanceStatus.MissingAsset
                    : PrefabInstanceStatus.Connected;
            }
            else
            {
                info.PrefabStatus = PrefabInstanceStatus.NotAPrefab;
            }

            // Don't skip missing-script slots — capturing them as IsMissing=true
            // gives consumers (read tools, validators) a direct signal without a
            // second scan via `go.GetComponents<Component>()`.
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    info.Components.Add(new ComponentInfo
                    {
                        InstanceID = 0,
                        TypeName = "[Missing]",
                        Enabled = false,
                        IsMissing = true,
                    });
                    continue;
                }

                var componentTarget = ToolTargetRef.Capture(c);
                info.Components.Add(new ComponentInfo
                {
                    InstanceID = c.GetMcpInstanceId(),
                    GlobalObjectId = componentTarget?.GlobalObjectId,
                    CaptureDomainId = componentTarget?.CaptureDomainId,
                    ComponentOrdinal = componentTarget?.ComponentOrdinal ?? -1,
                    TypeName = c.GetType().FullName,
                    Enabled = c is Behaviour b ? b.enabled : true,
                    IsMissing = false,
                });

                if (includeRenderers && c is Renderer r)
                {
                    var sharedMats = r.sharedMaterials;
                    var paths = new string[sharedMats.Length];
                    for (int mi = 0; mi < sharedMats.Length; mi++)
                    {
                        paths[mi] = sharedMats[mi] != null
                            ? AssetDatabase.GetAssetPath(sharedMats[mi])
                            : null;
                    }
                    _renderers[r.GetMcpInstanceId()] = new RendererSummary
                    {
                        OwnerInstanceID = go.GetMcpInstanceId(),
                        MaterialPaths = paths,
                        MaterialCount = paths.Length,
                        ShadowCasting = r.shadowCastingMode,
                        LightmapIndex = r.lightmapIndex,
                        IsBatchableStatic = (staticFlags & StaticEditorFlags.BatchingStatic) != 0,
                        WorldBounds = r.bounds,
                        IsEnabled = r.enabled,
                    };
                }
            }

            _byId[info.InstanceID] = info;

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                info.ChildIds.Add(child.GetMcpInstanceId());
                Capture(child, info.InstanceID, scene, isPrefabStage, includeRenderers);
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
        public string SceneName;
        public string ScenePath;
        public string SceneGuid;
        public int SceneHandle;
        public string HierarchyPath;
        public string GlobalObjectId;
        public string CaptureDomainId;
        public bool IsPrefabStage;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 LocalScale;
        public Vector3 WorldPosition;
        public Quaternion WorldRotation;
        public string Tag;
        public int Layer;
        public bool IsActive;
        public bool IsStatic;
        public StaticEditorFlags StaticFlags;
        public string PrefabAssetPath;
        public PrefabInstanceStatus PrefabStatus;
        public List<ComponentInfo> Components;
        public List<int> ChildIds;

        public ToolTargetRef ToTargetRef() => new ToolTargetRef
        {
            Kind = "gameObject", InstanceId = InstanceID, GlobalObjectId = GlobalObjectId,
            CaptureDomainId = CaptureDomainId, SceneGuid = SceneGuid, ScenePath = ScenePath,
            SceneHandle = SceneHandle, HierarchyPath = HierarchyPath, IsPrefabStage = IsPrefabStage,
        };
    }

    public sealed class ComponentInfo
    {
        public int InstanceID;
        public string GlobalObjectId;
        public string CaptureDomainId;
        public int ComponentOrdinal;
        public string TypeName;
        public bool Enabled;
        /// True when the component slot exists but the script type couldn't be
        /// resolved (broken MonoBehaviour reference). When true, InstanceID is 0
        /// and TypeName is "[Missing]".
        public bool IsMissing;
    }

    public sealed class LoadedScene
    {
        public string Name;
        public string Path;
        public string Guid;
        public int Handle;
        public bool IsActive;
        public bool IsPrefabStage;
    }

    public enum PrefabInstanceStatus
    {
        NotAPrefab,
        Connected,
        MissingAsset,
    }

    public sealed class RendererSummary
    {
        public int OwnerInstanceID;
        public string[] MaterialPaths;
        public int MaterialCount;
        public ShadowCastingMode ShadowCasting;
        public int LightmapIndex;
        public bool IsBatchableStatic;
        public Bounds WorldBounds;
        public bool IsEnabled;
    }
}
