using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Creates a Volume GameObject with an associated VolumeProfile asset. Works
    /// for both URP and HDRP since both reuse the SRP Core Volume framework.
    public static class VolumeFactory
    {
        public static OpResult Create(JObject args)
        {
            var name = (string)args["name"] ?? "Global Volume";
            var profilePath = (string)args["profilePath"]
                ?? $"Assets/Settings/{name.Replace(' ', '_')}_Profile.asset";

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Volume");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = (bool?)args["isGlobal"] ?? true;
            volume.priority = (float?)args["priority"] ?? 0f;
            volume.weight = (float?)args["weight"] ?? 1f;
            volume.blendDistance = (float?)args["blendDistance"] ?? 0f;

            // Volume profile is an asset on disk. Reuse if it exists, otherwise create.
            VolumeProfile profile = null;
            if (File.Exists(profilePath))
            {
                profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            }
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                EnsureFolder(Path.GetDirectoryName(profilePath));
                AssetDatabase.CreateAsset(profile, profilePath);
                AssetDatabase.SaveAssets();
            }
            volume.sharedProfile = profile;

            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");
            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);

            return OpResult.Success(go.GetInstanceID(), $"created Volume '{name}' with profile {profilePath}");
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            var name = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(parent)) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
