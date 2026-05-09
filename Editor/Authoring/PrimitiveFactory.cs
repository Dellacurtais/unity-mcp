using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class PrimitiveFactory
    {
        public static OpResult Create(JObject args)
        {
            var kindStr = (string)args["kind"] ?? "Cube";
            if (!Enum.TryParse(kindStr, true, out PrimitiveType kind))
                return OpResult.Fail($"unknown primitive kind '{kindStr}'");

            var go = GameObject.CreatePrimitive(kind);
            Undo.RegisterCreatedObjectUndo(go, $"Create {kind}");

            var name = (string)args["name"];
            if (!string.IsNullOrEmpty(name)) go.name = name;

            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");

            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            go.transform.localRotation = JsonHelpers.ParseRotation(args["rotation"]);
            go.transform.localScale = JsonHelpers.ParseVector3(args["scale"], Vector3.one);

            return OpResult.Success(go.GetInstanceID(), $"created {kind} '{go.name}'");
        }
    }
}
