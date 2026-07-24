using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Creates global or box-bounded local Volumes with persistent shared profiles.
    public static class VolumeFactory
    {
        public static OpResult Create(JObject args)
        {
            var name = ((string)args["name"] ?? "Global Volume").Trim();
            if (string.IsNullOrEmpty(name)) return OpResult.Fail("name must not be empty");

            if (!TryReadSettings(args, out var isGlobal, out var priority, out var weight, out var blendDistance, out var settingsError))
                return OpResult.Fail(settingsError);
            if (!TryNormalizeProfilePath((string)args["profilePath"], name, out var profilePath, out var pathError))
                return OpResult.Fail(pathError);

            var parentToken = args["parent"];
            var parent = JsonHelpers.ResolveParent(parentToken);
            if (parentToken != null && parentToken.Type != JTokenType.Null && parent == null)
                return OpResult.Fail($"parent GameObject not found: instanceId={(int)parentToken}");

            var existingAsset = AssetDatabase.LoadMainAssetAtPath(profilePath);
            if (existingAsset != null && !(existingAsset is VolumeProfile))
                return OpResult.Fail($"asset at {profilePath} is {existingAsset.GetType().Name}, not a VolumeProfile");

            var profile = existingAsset as VolumeProfile;
            var profileWasCreated = false;
            GameObject gameObject = null;
            try
            {
                if (profile == null)
                {
                    EnsureFolder(Path.GetDirectoryName(profilePath)?.Replace('\\', '/'));
                    profile = ScriptableObject.CreateInstance<VolumeProfile>();
                    profile.name = Path.GetFileNameWithoutExtension(profilePath);
                    AssetDatabase.CreateAsset(profile, profilePath);
                    EditorUtility.SetDirty(profile);
                    AssetDatabase.SaveAssets();
                    profileWasCreated = true;
                }

                gameObject = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(gameObject, "Create Volume");

                BoxCollider localCollider = null;
                if (!isGlobal)
                {
                    localCollider = gameObject.AddComponent<BoxCollider>();
                    localCollider.isTrigger = true;
                }

                var volume = gameObject.AddComponent<Volume>();
                volume.isGlobal = isGlobal;
                volume.priority = priority;
                volume.weight = weight;
                volume.blendDistance = blendDistance;
                volume.sharedProfile = profile;

                if (parent != null) Undo.SetTransformParent(gameObject.transform, parent.transform, "Parent Volume");
                gameObject.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
                EditorSceneManager.MarkSceneDirty(gameObject.scene);

                var profileGuid = AssetDatabase.AssetPathToGUID(profilePath);
                var data = new JObject
                {
                    ["instanceId"] = gameObject.GetInstanceID(),
                    ["volumeInstanceId"] = gameObject.GetInstanceID(),
                    ["volumeComponentInstanceId"] = volume.GetInstanceID(),
                    ["name"] = gameObject.name,
                    ["isGlobal"] = volume.isGlobal,
                    ["priority"] = volume.priority,
                    ["weight"] = volume.weight,
                    ["blendDistance"] = volume.blendDistance,
                    ["profilePath"] = profilePath,
                    ["profileGuid"] = profileGuid,
                    ["profileCreated"] = profileWasCreated,
                    ["collider"] = localCollider == null
                        ? null
                        : new JObject
                        {
                            ["type"] = typeof(BoxCollider).FullName,
                            ["instanceId"] = localCollider.GetInstanceID(),
                            ["isTrigger"] = localCollider.isTrigger,
                        },
                };

                return new OpResult
                {
                    Ok = true,
                    InstanceID = gameObject.GetInstanceID(),
                    Summary = $"created {(isGlobal ? "global" : "local box")} Volume '{name}' with profile {profilePath}",
                    CreatedAssetPath = profileWasCreated ? profilePath : null,
                    Data = data,
                };
            }
            catch (Exception exception)
            {
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
                if (profileWasCreated) AssetDatabase.DeleteAsset(profilePath);
                return OpResult.Fail($"could not create Volume: {exception.GetBaseException().Message}");
            }
        }

        private static bool TryReadSettings(
            JObject args,
            out bool isGlobal,
            out float priority,
            out float weight,
            out float blendDistance,
            out string error)
        {
            isGlobal = true;
            priority = 0f;
            weight = 1f;
            blendDistance = 0f;
            error = null;
            try
            {
                isGlobal = (bool?)args["isGlobal"] ?? true;
                priority = (float?)args["priority"] ?? 0f;
                weight = (float?)args["weight"] ?? 1f;
                blendDistance = (float?)args["blendDistance"] ?? 0f;
            }
            catch (Exception exception)
            {
                error = $"invalid volume setting: {exception.GetBaseException().Message}";
                return false;
            }

            if (!IsFinite(priority)) { error = "priority must be finite"; return false; }
            if (!IsFinite(weight) || weight < 0f || weight > 1f) { error = "weight must be between 0 and 1"; return false; }
            if (!IsFinite(blendDistance) || blendDistance < 0f) { error = "blendDistance must be non-negative and finite"; return false; }
            return true;
        }

        private static bool TryNormalizeProfilePath(string requested, string volumeName, out string path, out string error)
        {
            path = string.IsNullOrWhiteSpace(requested)
                ? $"Assets/Settings/{SafeFileName(volumeName)}_Profile.asset"
                : requested.Replace('\\', '/').Trim();
            error = null;
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = "profilePath must stay under Assets/";
                return false;
            }
            if (path.Contains("../") || path.EndsWith("/..", StringComparison.Ordinal))
            {
                error = "profilePath must not contain parent traversal";
                return false;
            }
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                error = "profilePath must end with .asset";
                return false;
            }
            return true;
        }

        private static string SafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var characters = value.ToCharArray();
            for (var index = 0; index < characters.Length; index++)
                if (Array.IndexOf(invalid, characters[index]) >= 0 || char.IsWhiteSpace(characters[index]))
                    characters[index] = '_';
            return new string(characters);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            folder = folder.Replace('\\', '/');
            var separator = folder.LastIndexOf('/');
            if (separator <= 0) throw new InvalidOperationException($"invalid asset folder: {folder}");
            var parent = folder.Substring(0, separator);
            var child = folder.Substring(separator + 1);
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, child)))
                throw new InvalidOperationException($"could not create asset folder: {folder}");
        }
    }
}
