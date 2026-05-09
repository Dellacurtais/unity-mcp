using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class TransformEditor
    {
        public static OpResult SetTransform(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            Undo.RecordObject(go.transform, "Set Transform");

            var space = (string)args["space"] ?? "World";
            bool world = string.Equals(space, "World", System.StringComparison.OrdinalIgnoreCase);

            if (args["position"] != null)
            {
                var p = JsonHelpers.ParseVector3(args["position"]);
                if (world) go.transform.position = p; else go.transform.localPosition = p;
            }
            if (args["rotation"] != null)
            {
                var r = JsonHelpers.ParseRotation(args["rotation"]);
                if (world) go.transform.rotation = r; else go.transform.localRotation = r;
            }
            if (args["scale"] != null)
            {
                go.transform.localScale = JsonHelpers.ParseVector3(args["scale"], Vector3.one);
            }

            return OpResult.Success(id, "transform updated");
        }

        public static OpResult Parent(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var parent = JsonHelpers.ResolveParent(args["newParentId"]);
            bool worldStays = (bool?)args["worldPositionStays"] ?? true;
            Undo.SetTransformParent(go.transform, parent != null ? parent.transform : null, "Reparent");
            if (!worldStays)
            {
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
            }
            return OpResult.Success(id, "reparented");
        }
    }
}
