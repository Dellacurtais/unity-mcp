using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class PrefabFactory
    {
        public static OpResult Instantiate(JObject args)
        {
            var guidOrPath = (string)args["guidOrPath"] ?? (string)args["asset"];
            if (string.IsNullOrEmpty(guidOrPath))
                return OpResult.Fail("guidOrPath is required");

            var path = guidOrPath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)
                ? guidOrPath
                : AssetDatabase.GUIDToAssetPath(guidOrPath);
            if (string.IsNullOrEmpty(path))
                return OpResult.Fail($"asset not found: {guidOrPath}");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                return OpResult.Fail($"asset is not a prefab: {path}");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
                return OpResult.Fail("failed to instantiate prefab");

            Undo.RegisterCreatedObjectUndo(instance, $"Instantiate {prefab.name}");

            var name = (string)args["name"];
            if (!string.IsNullOrEmpty(name)) instance.name = name;

            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(instance.transform, parent.transform, "Parent");

            instance.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            instance.transform.localRotation = JsonHelpers.ParseRotation(args["rotation"]);
            instance.transform.localScale = JsonHelpers.ParseVector3(args["scale"], Vector3.one);

            return OpResult.Success(instance.GetInstanceID(), $"instantiated prefab '{prefab.name}'");
        }
    }
}
