using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    public static class ToolTargetResolver
    {
        public static ToolTargetRef ReadTarget(JObject arguments)
        {
            if (arguments == null) return null;
            if (arguments["target"] != null) return ToolTargetRef.FromJson(arguments["target"]);
            var target = ToolTargetRef.FromJson(arguments);
            if (target != null && target.InstanceId == 0) target.InstanceId = (int?)arguments["instanceId"] ?? 0;
            return target;
        }

        public static UnityEngine.Object Resolve(ToolTargetRef target, out string error)
        {
            error = null;
            if (target == null) { error = "TARGET_REQUIRED: target or instanceId is required"; return null; }

            var hasStableIdentity = !string.IsNullOrEmpty(target.GlobalObjectId)
                || !string.IsNullOrEmpty(target.AssetGuid)
                || !string.IsNullOrEmpty(target.AssetPath)
                || !string.IsNullOrEmpty(target.HierarchyPath)
                || !string.IsNullOrEmpty(target.SceneGuid)
                || !string.IsNullOrEmpty(target.ScenePath);
            var prefabStage = target.IsPrefabStage ? PrefabStageUtility.GetCurrentPrefabStage() : null;
            if (target.IsPrefabStage)
            {
                if (prefabStage == null)
                {
                    error = "PREFAB_STAGE_NOT_OPEN: the captured Prefab Stage is not currently open";
                    return null;
                }
                if (!string.IsNullOrEmpty(target.ScenePath)
                    && !string.Equals(prefabStage.assetPath, target.ScenePath, StringComparison.Ordinal))
                {
                    error = $"PREFAB_STAGE_MISMATCH: expected '{target.ScenePath}', current stage is '{prefabStage.assetPath}'";
                    return null;
                }
                if (!string.IsNullOrEmpty(target.SceneGuid)
                    && !string.Equals(AssetDatabase.AssetPathToGUID(prefabStage.assetPath), target.SceneGuid, StringComparison.Ordinal))
                {
                    error = $"PREFAB_STAGE_MISMATCH: current stage GUID does not match '{target.SceneGuid}'";
                    return null;
                }
            }

            UnityEngine.Object resolved = null;
            if (!string.IsNullOrEmpty(target.GlobalObjectId) && GlobalObjectId.TryParse(target.GlobalObjectId, out var globalId))
                resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
            if (resolved == null && !target.IsPrefabStage && !string.IsNullOrEmpty(target.AssetGuid)) resolved = ResolveAsset(target.AssetGuid, target.LocalFileId);
            if (resolved == null && !target.IsPrefabStage && !string.IsNullOrEmpty(target.AssetPath)) resolved = ResolveAssetPath(target.AssetPath, target.LocalFileId);
            if (resolved != null && target.IsPrefabStage)
            {
                var resolvedGameObject = resolved as GameObject ?? (resolved as Component)?.gameObject;
                if (resolvedGameObject == null || resolvedGameObject.scene != prefabStage.scene) resolved = null;
            }
            if (resolved == null && !string.IsNullOrEmpty(target.HierarchyPath)) resolved = ResolveHierarchy(target, out error);
            if (resolved == null && !hasStableIdentity && target.InstanceId != 0)
                resolved = EditorUtility.InstanceIDToObject(target.InstanceId);
            if (resolved == null)
            {
                if (error == null) error = "TARGET_NOT_FOUND: the target no longer resolves; refresh and use its globalObjectId";
                return null;
            }

            if (!string.IsNullOrEmpty(target.ComponentType))
            {
                var gameObject = resolved as GameObject ?? (resolved as Component)?.gameObject;
                if (gameObject == null) { error = "TARGET_TYPE_MISMATCH: component target requires a GameObject"; return null; }
                var type = ResolveType(target.ComponentType);
                if (type == null || !typeof(Component).IsAssignableFrom(type)) { error = $"COMPONENT_TYPE_NOT_FOUND: {target.ComponentType}"; return null; }
                var components = gameObject.GetComponents(type);
                var ordinal = target.ComponentOrdinal < 0 ? 0 : target.ComponentOrdinal;
                if (ordinal >= components.Length) { error = $"COMPONENT_NOT_FOUND: ordinal {ordinal} exceeds {components.Length}"; return null; }
                resolved = components[ordinal];
            }
            return resolved;
        }

        public static GameObject ResolveGameObject(JObject arguments, out string error)
        {
            var target = Resolve(ReadTarget(arguments), out error);
            if (target is GameObject gameObject) return gameObject;
            if (target is Component component) return component.gameObject;
            if (target != null) error = $"TARGET_TYPE_MISMATCH: expected GameObject, got {target.GetType().Name}";
            return null;
        }

        public static Component ResolveComponent(JObject arguments, string componentType, int ordinal, out string error)
        {
            var gameObject = ResolveGameObject(arguments, out error);
            if (gameObject == null) return null;
            var type = ResolveType(componentType);
            if (type == null || !typeof(Component).IsAssignableFrom(type)) { error = $"COMPONENT_TYPE_NOT_FOUND: {componentType}"; return null; }
            var components = gameObject.GetComponents(type);
            if (ordinal < 0) ordinal = 0;
            if (ordinal >= components.Length) { error = $"COMPONENT_NOT_FOUND: {componentType} ordinal {ordinal}"; return null; }
            return components[ordinal];
        }

        private static UnityEngine.Object ResolveAsset(string guid, long localFileId)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return ResolveAssetPath(path, localFileId);
        }

        private static UnityEngine.Object ResolveAssetPath(string path, long localFileId)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (localFileId == 0) return AssetDatabase.LoadMainAssetAtPath(path);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out long candidate) && candidate == localFileId)
                    return asset;
            return null;
        }

        private static GameObject ResolveHierarchy(ToolTargetRef target, out string error)
        {
            error = null;
            var matches = new List<GameObject>();
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (target.IsPrefabStage)
            {
                if (prefabStage == null)
                {
                    error = "PREFAB_STAGE_NOT_OPEN: the captured Prefab Stage is not currently open";
                    return null;
                }
                CollectPathMatches(prefabStage.scene, target.HierarchyPath, matches);
            }
            else
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.IsValid() || !scene.isLoaded) continue;
                    if (!string.IsNullOrEmpty(target.ScenePath) && !string.Equals(scene.path, target.ScenePath, StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrEmpty(target.SceneGuid) && !string.Equals(AssetDatabase.AssetPathToGUID(scene.path), target.SceneGuid, StringComparison.Ordinal)) continue;
                    if (string.IsNullOrEmpty(target.ScenePath) && string.IsNullOrEmpty(target.SceneGuid)
                        && target.SceneHandle != 0 && scene.handle != target.SceneHandle) continue;
                    CollectPathMatches(scene, target.HierarchyPath, matches);
                }
            }
            if (matches.Count == 1) return matches[0];
            error = matches.Count == 0
                ? $"TARGET_NOT_FOUND: hierarchy path '{target.HierarchyPath}'"
                : $"TARGET_AMBIGUOUS: hierarchy path '{target.HierarchyPath}' matched {matches.Count} objects; include scenePath/globalObjectId";
            return null;
        }

        private static void CollectPathMatches(Scene scene, string path, List<GameObject> matches)
        {
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(path)) return;
            var segments = path.Split('/');
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!string.Equals(root.name, segments[0], StringComparison.Ordinal)) continue;
                Transform current = root.transform;
                bool found = true;
                for (int i = 1; i < segments.Length; i++)
                {
                    Transform next = null;
                    for (int child = 0; child < current.childCount; child++)
                        if (string.Equals(current.GetChild(child).name, segments[i], StringComparison.Ordinal))
                        { if (next != null) { found = false; break; } next = current.GetChild(child); }
                    if (!found || next == null) { found = false; break; }
                    current = next;
                }
                if (found) matches.Add(current.gameObject);
            }
        }

        private static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var exact = Type.GetType(name, false);
            if (exact != null) return exact;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var byFullName = assembly.GetType(name, false, false);
                    if (byFullName != null) return byFullName;
                    foreach (var type in assembly.GetTypes()) if (type.Name == name) return type;
                }
                catch { }
            }
            return null;
        }
    }
}
