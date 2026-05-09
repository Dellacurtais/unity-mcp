using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class CameraFactory
    {
        public static OpResult Create(JObject args)
        {
            var name = (string)args["name"] ?? "Camera";
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Camera");
            var cam = go.AddComponent<Camera>();

            ApplyProps(cam, args);

            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");
            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            go.transform.localRotation = JsonHelpers.ParseRotation(args["rotation"]);

            if ((bool?)args["isMain"] == true)
            {
                // Untag any previous MainCamera so there's only one.
                foreach (var existing in GameObject.FindGameObjectsWithTag("MainCamera"))
                {
                    if (existing != go) existing.tag = "Untagged";
                }
                go.tag = "MainCamera";
            }

            return OpResult.Success(go.GetInstanceID(), $"created camera '{name}'");
        }

        public static OpResult SetProps(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var cam = go.GetComponent<Camera>();
            if (cam == null) return OpResult.Fail("GameObject has no Camera component");
            Undo.RecordObject(cam, "Set Camera Props");
            ApplyProps(cam, args);
            return OpResult.Success(id, "camera props updated");
        }

        private static void ApplyProps(Camera cam, JObject args)
        {
            if (args["fieldOfView"] != null) cam.fieldOfView = (float)args["fieldOfView"];
            if (args["nearClipPlane"] != null) cam.nearClipPlane = (float)args["nearClipPlane"];
            if (args["farClipPlane"] != null) cam.farClipPlane = (float)args["farClipPlane"];
            if (args["depth"] != null) cam.depth = (float)args["depth"];
            if (args["orthographic"] != null) cam.orthographic = (bool)args["orthographic"];
            if (args["orthographicSize"] != null) cam.orthographicSize = (float)args["orthographicSize"];
            if (args["clearFlags"] != null
                && Enum.TryParse((string)args["clearFlags"], true, out CameraClearFlags flags))
                cam.clearFlags = flags;
            if (args["backgroundColor"] != null)
                cam.backgroundColor = JsonHelpers.ParseColor(args["backgroundColor"]);
            if (args["cullingMask"] != null)
                cam.cullingMask = (int)args["cullingMask"];
        }
    }
}
