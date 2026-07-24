using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Sets a material on a Renderer slot. Loads the asset by GUID or path.
    public static class RendererMaterialEditor
    {
        public static OpResult SetMaterial(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var slot = (int?)args["slotIndex"] ?? 0;
            var matRef = (string)args["materialGuid"] ?? (string)args["material"];
            if (string.IsNullOrEmpty(matRef)) return OpResult.Fail("materialGuid is required");

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return OpResult.Fail("GameObject has no Renderer");

            var path = matRef.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? matRef
                : AssetDatabase.GUIDToAssetPath(matRef);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) return OpResult.Fail($"material asset not found: {matRef}");

            var mats = renderer.sharedMaterials;
            if (slot < 0 || slot >= mats.Length)
                return OpResult.Fail($"slotIndex {slot} out of range (renderer has {mats.Length} slots)");

            using (var mutation = new MutationScope("Set Material"))
            {
                mutation.Record(renderer);
                mats[slot] = mat;
                renderer.sharedMaterials = mats;
                if (PrefabUtility.IsPartOfPrefabInstance(renderer))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                mutation.Complete();
            }

            var result = OpResult.Success(id, $"set material on slot {slot} to '{mat.name}'");
            result.Data = new JObject
            {
                ["instanceId"] = id,
                ["rendererInstanceId"] = renderer.GetInstanceID(),
                ["slotIndex"] = slot,
                ["material"] = ToolTargetRef.Capture(mat)?.ToJson(),
            };
            return result;
        }
    }
}
