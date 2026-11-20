using Maicon.SceneAnalyzer.Editor.Core;
using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class PrefabFactory
    {
        public static OpResult Instantiate(JObject args)
        {
            args ??= new JObject();
            var guidOrPath = ((string)args["guidOrPath"] ?? (string)args["asset"] ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(guidOrPath)) return OpResult.Fail("INVALID_ARG: guidOrPath is required");

            var path = guidOrPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                       || guidOrPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                ? guidOrPath.Replace('\\', '/')
                : AssetDatabase.GUIDToAssetPath(guidOrPath);
            if (string.IsNullOrEmpty(path)) return OpResult.Fail($"ASSET_NOT_FOUND: {guidOrPath}");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || !PrefabUtility.IsPartOfPrefabAsset(prefab))
                return OpResult.Fail($"INVALID_PREFAB: asset is not a GameObject Prefab: {path}");
            if (!SceneAuthoringSupport.TryResolveParent(args["parent"], out var parent, out var error))
                return OpResult.Fail(error);
            if (!PrimitiveFactory.TryOptionalVector(args, "position", Vector3.zero, out var position, out error)
                || !PrimitiveFactory.TryOptionalRotation(args, "rotation", out var rotation, out error)
                || !PrimitiveFactory.TryOptionalVector(args, "scale", Vector3.one, out var scale, out error))
                return OpResult.Fail(error);

            var requestedName = args["name"] != null ? ((string)args["name"] ?? string.Empty).Trim() : null;
            if (requestedName != null && requestedName.Length == 0)
                return OpResult.Fail("INVALID_ARG: name must not be blank");

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var destinationScene = parent != null ? parent.scene : stage != null ? stage.scene : SceneManager.GetActiveScene();
            if (!destinationScene.IsValid() || !destinationScene.isLoaded)
                return OpResult.Fail("INVALID_SCENE_TARGET: destination Scene must be valid and loaded");

            using (var mutation = new MutationScope($"Instantiate {prefab.name}"))
            {
                GameObject instance;
                try { instance = PrefabUtility.InstantiatePrefab(prefab, destinationScene) as GameObject; }
                catch (Exception exception) { return OpResult.Fail($"INSTANTIATE_FAILED: {exception.Message}"); }
                if (instance == null) return OpResult.Fail("INSTANTIATE_FAILED: PrefabUtility returned no GameObject");

                mutation.RegisterCreated(instance, $"Instantiate {prefab.name}");
                if (requestedName != null)
                {
                    mutation.Record(instance, "Rename Prefab Instance");
                    instance.name = requestedName;
                }
                if (parent != null) mutation.SetParent(instance.transform, parent.transform, "Parent");
                mutation.Record(instance.transform, "Set Prefab Pose");
                instance.transform.localPosition = position;
                instance.transform.localRotation = rotation;
                instance.transform.localScale = scale;
                mutation.Complete();
                return OpResult.Success(instance.GetMcpInstanceId(), $"instantiated prefab '{prefab.name}'");
            }
        }
    }
}
