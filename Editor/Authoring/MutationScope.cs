using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public sealed class MutationScope : IDisposable
    {
        private readonly int _undoGroup;
        private readonly HashSet<int> _sceneHandles = new HashSet<int>();
        private readonly HashSet<string> _assets = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private bool _completed;

        public MutationScope(string name)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(string.IsNullOrEmpty(name) ? "Unity MCP mutation" : name);
            _undoGroup = Undo.GetCurrentGroup();
        }

        public void Record(UnityEngine.Object target, string undoName = null)
        {
            if (target == null) return;
            Undo.RecordObject(target, undoName ?? "Unity MCP mutation");
            Touch(target);
        }

        public T AddComponent<T>(GameObject gameObject) where T : Component
        {
            var component = Undo.AddComponent<T>(gameObject);
            Touch(component);
            return component;
        }

        public Component AddComponent(GameObject gameObject, Type type)
        {
            var component = Undo.AddComponent(gameObject, type);
            Touch(component);
            return component;
        }

        public void RegisterCreated(UnityEngine.Object target, string undoName = null)
        {
            if (target == null) return;
            Undo.RegisterCreatedObjectUndo(target, undoName ?? "Create object");
            Touch(target);
        }

        public void Destroy(UnityEngine.Object target)
        {
            if (target == null) return;
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (gameObject != null) TouchScene(gameObject.scene);
            Undo.DestroyObjectImmediate(target);
        }

        public void SetParent(Transform child, Transform parent, string undoName = null)
        {
            if (child == null) return;
            Undo.SetTransformParent(child, parent, undoName ?? "Reparent");
            Touch(child);
        }

        public bool Apply(SerializedObject serializedObject, UnityEngine.Object target)
        {
            if (serializedObject == null) return false;
            var changed = serializedObject.ApplyModifiedProperties();
            if (changed) Touch(target ?? serializedObject.targetObject);
            return changed;
        }

        public void Touch(UnityEngine.Object target)
        {
            if (target == null) return;
            _objects.Add(target);
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (gameObject != null)
            {
                TouchScene(gameObject.scene);
            }
            else
            {
                var path = AssetDatabase.GetAssetPath(target);
                if (!string.IsNullOrEmpty(path))
                {
                    _assets.Add(path);
                    EditorUtility.SetDirty(target);
                }
            }
        }

        public void TouchScene(Scene scene)
        {
            if (scene.IsValid()) _sceneHandles.Add(scene.handle);
        }

        // SceneManager.GetSceneByHandle was removed in Unity 6000.3; resolve by iterating loaded scenes.
        private static Scene ResolveSceneByHandle(int handle)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if ((int)scene.handle == handle) return scene;
            }
            return default;
        }

        public void Complete()
        {
            if (_completed) return;
            foreach (var handle in _sceneHandles)
            {
                var scene = ResolveSceneByHandle(handle);
                if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            }
            foreach (var target in _objects)
            {
                if (target == null) continue;
                if (target is GameObject || target is Component)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(target))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                }
                else EditorUtility.SetDirty(target);
            }
            Undo.CollapseUndoOperations(_undoGroup);
            _completed = true;
        }

        public JObject ToJson()
        {
            var scenes = new JArray();
            foreach (var handle in _sceneHandles)
            {
                var scene = ResolveSceneByHandle(handle);
                scenes.Add(new JObject { ["handle"] = handle, ["path"] = scene.path, ["name"] = scene.name });
            }
            return new JObject { ["scenes"] = scenes, ["assets"] = new JArray(_assets), ["objectCount"] = _objects.Count, ["completed"] = _completed };
        }

        public static HashSet<int> CaptureLegacySceneHandles(JObject arguments)
        {
            var scenes = new HashSet<int>();
            var objects = new HashSet<int>();
            if (arguments != null) CaptureToken(arguments, scenes, objects);
            return scenes;
        }

        public static JObject FinalizeLegacyMutation(
            JObject arguments, int resultInstanceId = 0, HashSet<int> preMutationSceneHandles = null)
        {
            var touchedScenes = preMutationSceneHandles ?? new HashSet<int>();
            var touchedObjects = new HashSet<int>();
            if (arguments != null) FinalizeToken(arguments, touchedScenes, touchedObjects);
            if (resultInstanceId != 0)
                FinalizeObject(EditorUtility.InstanceIDToObject(resultInstanceId), touchedScenes, touchedObjects);

            var scenes = new JArray();
            foreach (var handle in touchedScenes)
            {
                var scene = ResolveSceneByHandle(handle);
                if (!scene.IsValid()) continue;
                EditorSceneManager.MarkSceneDirty(scene);
                scenes.Add(new JObject { ["handle"] = handle, ["name"] = scene.name, ["path"] = scene.path });
            }
            return new JObject { ["scenes"] = scenes, ["objectCount"] = touchedObjects.Count };
        }

        private static void CaptureToken(JToken token, HashSet<int> scenes, HashSet<int> objects)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                        && property.Value.Type == JTokenType.Integer)
                        CaptureObject(EditorUtility.InstanceIDToObject((int)property.Value), scenes, objects);
                    else
                        CaptureToken(property.Value, scenes, objects);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array) CaptureToken(item, scenes, objects);
            }
        }

        private static void CaptureObject(UnityEngine.Object target, HashSet<int> scenes, HashSet<int> objects)
        {
            if (target == null || !objects.Add(target.GetInstanceID())) return;
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (gameObject != null && gameObject.scene.IsValid()) scenes.Add(gameObject.scene.handle);
        }

        private static void FinalizeToken(JToken token, HashSet<int> scenes, HashSet<int> objects)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                        && property.Value.Type == JTokenType.Integer)
                        FinalizeObject(EditorUtility.InstanceIDToObject((int)property.Value), scenes, objects);
                    else
                        FinalizeToken(property.Value, scenes, objects);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array) FinalizeToken(item, scenes, objects);
            }
        }

        private static void FinalizeObject(UnityEngine.Object target, HashSet<int> scenes, HashSet<int> objects)
        {
            if (target == null || !objects.Add(target.GetInstanceID())) return;
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (gameObject == null)
            {
                var path = AssetDatabase.GetAssetPath(target);
                if (!string.IsNullOrEmpty(path)) EditorUtility.SetDirty(target);
                return;
            }

            if (gameObject.scene.IsValid()) scenes.Add(gameObject.scene.handle);
            if (!PrefabUtility.IsPartOfPrefabInstance(gameObject)) return;
            PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
            PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject.transform);
            foreach (var component in gameObject.GetComponents<Component>())
                if (component != null) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        public void Dispose()
        {
            if (_completed) return;
            try { Undo.RevertAllDownToGroup(_undoGroup); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
