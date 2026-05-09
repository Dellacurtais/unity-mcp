using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class LightFactory
    {
        public static OpResult Create(JObject args)
        {
            var typeStr = (string)args["type"] ?? "Directional";
            if (!Enum.TryParse(typeStr, true, out LightType type))
                return OpResult.Fail($"unknown light type '{typeStr}'");

            var go = new GameObject(string.IsNullOrEmpty((string)args["name"])
                ? $"{type} Light"
                : (string)args["name"]);
            Undo.RegisterCreatedObjectUndo(go, $"Create {type} Light");

            var light = go.AddComponent<Light>();
            light.type = type;

            light.intensity = (float?)args["intensity"] ?? 1f;
            light.range = (float?)args["range"] ?? 10f;
            light.color = JsonHelpers.ParseColor(args["color"], Color.white);

            var shadowsStr = (string)args["shadows"] ?? "Soft";
            if (Enum.TryParse(shadowsStr, true, out LightShadows shadows))
                light.shadows = shadows;

            var modeStr = (string)args["mode"];
            if (!string.IsNullOrEmpty(modeStr) && Enum.TryParse(modeStr, true, out LightmapBakeType bake))
                light.lightmapBakeType = bake;

            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");

            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            go.transform.localRotation = JsonHelpers.ParseRotation(args["rotation"]);

            return OpResult.Success(go.GetInstanceID(),
                $"created {type} light intensity={light.intensity} range={light.range}");
        }
    }
}
