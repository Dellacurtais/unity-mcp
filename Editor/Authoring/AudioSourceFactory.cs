using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class AudioSourceFactory
    {
        public static OpResult Create(JObject args)
        {
            var name = (string)args["name"] ?? "Audio Source";
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Audio Source");
            var src = go.AddComponent<AudioSource>();

            var clipRef = (string)args["clipGuid"] ?? (string)args["clip"];
            if (!string.IsNullOrEmpty(clipRef))
            {
                var path = clipRef.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)
                    ? clipRef
                    : AssetDatabase.GUIDToAssetPath(clipRef);
                src.clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }
            if (args["loop"] != null) src.loop = (bool)args["loop"];
            if (args["volume"] != null) src.volume = (float)args["volume"];
            if (args["pitch"] != null) src.pitch = (float)args["pitch"];
            if (args["spatial"] != null) src.spatialBlend = (bool)args["spatial"] ? 1f : 0f;
            if (args["playOnAwake"] != null) src.playOnAwake = (bool)args["playOnAwake"];

            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");
            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);

            return OpResult.Success(go.GetInstanceID(),
                $"created audio source '{name}'" + (src.clip != null ? $" with clip '{src.clip.name}'" : ""));
        }
    }
}
