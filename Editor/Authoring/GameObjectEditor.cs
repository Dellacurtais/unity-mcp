using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// GameObject-level mutations (other than transform/parent which live in
    /// TransformEditor). All ops register an Undo entry.
    public static class GameObjectEditor
    {
        public static OpResult Duplicate(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var clone = Object.Instantiate(go);
            Undo.RegisterCreatedObjectUndo(clone, "Duplicate");

            var name = (string)args["name"];
            clone.name = string.IsNullOrEmpty(name) ? go.name + " (Copy)" : name;

            // Parent: explicit > source's parent
            var parentArg = args["parent"];
            if (parentArg != null && parentArg.Type != JTokenType.Null)
            {
                var parent = JsonHelpers.ResolveParent(parentArg);
                if (parent != null) Undo.SetTransformParent(clone.transform, parent.transform, "Parent");
            }
            else if (go.transform.parent != null)
            {
                Undo.SetTransformParent(clone.transform, go.transform.parent, "Parent");
            }

            // Position: explicit override > source pos + offset > source pos (already set by Instantiate)
            if (args["position"] != null)
            {
                clone.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            }
            else if (args["offset"] != null)
            {
                clone.transform.localPosition = go.transform.localPosition + JsonHelpers.ParseVector3(args["offset"]);
            }

            if (args["rotation"] != null)
                clone.transform.localRotation = JsonHelpers.ParseRotation(args["rotation"]);
            if (args["scale"] != null)
                clone.transform.localScale = JsonHelpers.ParseVector3(args["scale"], Vector3.one);

            return OpResult.Success(clone.GetInstanceID(), $"duplicated '{go.name}' → '{clone.name}'");
        }

        public static OpResult LookAt(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            // Accept the explicit pair (targetInstanceId / targetPosition) OR a
            // generic `target` alias that auto-detects: integer → instanceId,
            // array/object → world position. Easier on agents that pick the
            // wrong field name.
            Vector3 worldTarget;
            var targetAlias = args["target"];
            if (args["targetInstanceId"] != null && args["targetInstanceId"].Type != JTokenType.Null)
            {
                var tid = (int?)args["targetInstanceId"] ?? 0;
                var tgo = EditorUtility.InstanceIDToObject(tid) as GameObject;
                if (tgo == null) return OpResult.Fail($"target GameObject not found: instanceId={tid}");
                worldTarget = tgo.transform.position;
            }
            else if (args["targetPosition"] != null)
            {
                worldTarget = JsonHelpers.ParseVector3(args["targetPosition"]);
            }
            else if (targetAlias != null && targetAlias.Type == JTokenType.Integer)
            {
                var tid = (int)targetAlias;
                var tgo = EditorUtility.InstanceIDToObject(tid) as GameObject;
                if (tgo == null) return OpResult.Fail($"target GameObject not found: instanceId={tid}");
                worldTarget = tgo.transform.position;
            }
            else if (targetAlias != null && (targetAlias is JArray || targetAlias is JObject))
            {
                worldTarget = JsonHelpers.ParseVector3(targetAlias);
            }
            else
            {
                return OpResult.Fail("provide either targetInstanceId, targetPosition, or target (instanceId int OR vec3 array/object)");
            }

            var up = args["worldUp"] != null ? JsonHelpers.ParseVector3(args["worldUp"]) : Vector3.up;

            Undo.RecordObject(go.transform, "Look At");
            go.transform.LookAt(worldTarget, up);

            // Cameras render along +Z; Quad/Plane normals are -Z. If `flipForCamera`
            // is true and the GO has no Camera, rotate 180 on Y so the visual face
            // points at the target. Default: respect Unity's LookAt convention.
            if ((bool?)args["flipForCamera"] == true && go.GetComponent<Camera>() == null)
                go.transform.Rotate(0f, 180f, 0f, Space.Self);

            return OpResult.Success(id, $"looking at {worldTarget}");
        }

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
