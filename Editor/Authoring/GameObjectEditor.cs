using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// GameObject-level mutations (other than transform/parent which live in
    /// TransformEditor). All ops register an Undo entry.
    public static class GameObjectEditor
    {
        public static OpResult SetActive(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var active = (bool?)args["active"] ?? true;
            Undo.RecordObject(go, active ? "Activate GameObject" : "Deactivate GameObject");
            go.SetActive(active);
            return OpResult.Success(id, $"set active = {active}");
        }

        public static OpResult Rename(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var name = (string)args["name"];
            if (string.IsNullOrEmpty(name)) return OpResult.Fail("name is required");
            Undo.RecordObject(go, "Rename GameObject");
            var oldName = go.name;
            go.name = name;
            return OpResult.Success(id, $"renamed '{oldName}' → '{name}'");
        }

        public static OpResult SetTag(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var tag = (string)args["tag"] ?? "Untagged";
            // Verify the tag is registered (Unity throws otherwise).
            try
            {
                _ = UnityEditorInternal.InternalEditorUtility.tags;
            }
            catch { /* fallback: just try */ }
            Undo.RecordObject(go, "Set Tag");
            try { go.tag = tag; }
            catch (UnityException e)
            {
                return OpResult.Fail($"tag '{tag}' is not registered (add it via Edit → Project Settings → Tags & Layers): {e.Message}");
            }
            return OpResult.Success(id, $"tag = '{tag}'");
        }

        public static OpResult SetLayer(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            int layer = -1;
            var rawLayer = args["layer"];
            if (rawLayer == null) return OpResult.Fail("layer is required (int 0..31 or layer name)");
            if (rawLayer.Type == JTokenType.Integer) layer = (int)rawLayer;
            else if (rawLayer.Type == JTokenType.String)
            {
                layer = LayerMask.NameToLayer((string)rawLayer);
                if (layer < 0) return OpResult.Fail($"layer name '{(string)rawLayer}' not found (define it in Project Settings → Tags & Layers)");
            }
            else return OpResult.Fail("layer must be int or string");

            if (layer < 0 || layer > 31) return OpResult.Fail($"layer index out of range: {layer}");

            Undo.RecordObject(go, "Set Layer");
            bool recursive = (bool?)args["recursive"] ?? false;
            if (recursive)
            {
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                {
                    Undo.RecordObject(t.gameObject, "Set Layer");
                    t.gameObject.layer = layer;
                }
            }
            else
            {
                go.layer = layer;
            }
            return OpResult.Success(id, $"layer = {layer}{(recursive ? " (recursive)" : "")}");
        }
    }
}
