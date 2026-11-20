using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class RenderSettingsEditor
    {
        private sealed class PendingSettings
        {
            public readonly List<string> Changed = new List<string>();
            public bool HasAmbientMode;
            public AmbientMode AmbientMode;
            public bool HasAmbientLight;
            public Color AmbientLight;
            public bool HasAmbientSkyColor;
            public Color AmbientSkyColor;
            public bool HasAmbientEquatorColor;
            public Color AmbientEquatorColor;
            public bool HasAmbientGroundColor;
            public Color AmbientGroundColor;
            public bool HasAmbientIntensity;
            public float AmbientIntensity;
            public bool HasFog;
            public bool Fog;
            public bool HasFogColor;
            public Color FogColor;
            public bool HasFogDensity;
            public float FogDensity;
            public bool HasFogStartDistance;
            public float FogStartDistance;
            public bool HasFogEndDistance;
            public float FogEndDistance;
            public bool HasFogMode;
            public FogMode FogMode;
            public bool HasSkybox;
            public Material Skybox;
            public bool HasReflectionIntensity;
            public float ReflectionIntensity;
        }

        public static OpResult Apply(JObject args)
        {
            if (args == null || args.Count == 0)
                return OpResult.Fail("at least one render setting is required");
            if (!TryPrepare(args, out var pending, out var error))
                return OpResult.Fail(error);

            var undoRegistered = TryRegisterUndo();
            ApplyPending(pending);
            var scene = SceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);

            return new OpResult
            {
                Ok = true,
                Summary = $"render settings updated ({string.Join(", ", pending.Changed)})" +
                          (undoRegistered ? string.Empty : " (no-undo)"),
                Data = Snapshot(pending.Changed, undoRegistered, scene),
            };
        }

        private static bool TryPrepare(JObject args, out PendingSettings pending, out string error)
        {
            pending = new PendingSettings();
            error = null;

            if (args["ambientMode"] != null)
            {
                if (!TryReadEnum(args["ambientMode"], "ambientMode", out pending.AmbientMode, out error)) return false;
                pending.HasAmbientMode = true;
                pending.Changed.Add("ambientMode");
            }
            if (!TryReadOptionalColor(args, "ambientLight", out pending.HasAmbientLight, out pending.AmbientLight, pending.Changed, out error) ||
                !TryReadOptionalColor(args, "ambientSkyColor", out pending.HasAmbientSkyColor, out pending.AmbientSkyColor, pending.Changed, out error) ||
                !TryReadOptionalColor(args, "ambientEquatorColor", out pending.HasAmbientEquatorColor, out pending.AmbientEquatorColor, pending.Changed, out error) ||
                !TryReadOptionalColor(args, "ambientGroundColor", out pending.HasAmbientGroundColor, out pending.AmbientGroundColor, pending.Changed, out error))
                return false;
            if (!TryReadOptionalFloat(args, "ambientIntensity", 0f, out pending.HasAmbientIntensity, out pending.AmbientIntensity, pending.Changed, out error))
                return false;

            if (args["fog"] != null)
            {
                if (args["fog"].Type != JTokenType.Boolean)
                {
                    error = "fog must be a boolean";
                    return false;
                }
                pending.HasFog = true;
                pending.Fog = (bool)args["fog"];
                pending.Changed.Add("fog");
            }
            if (!TryReadOptionalColor(args, "fogColor", out pending.HasFogColor, out pending.FogColor, pending.Changed, out error) ||
                !TryReadOptionalFloat(args, "fogDensity", 0f, out pending.HasFogDensity, out pending.FogDensity, pending.Changed, out error) ||
                !TryReadOptionalFloat(args, "fogStartDistance", 0f, out pending.HasFogStartDistance, out pending.FogStartDistance, pending.Changed, out error) ||
                !TryReadOptionalFloat(args, "fogEndDistance", 0f, out pending.HasFogEndDistance, out pending.FogEndDistance, pending.Changed, out error))
                return false;
            if (args["fogMode"] != null)
            {
                if (!TryReadEnum(args["fogMode"], "fogMode", out pending.FogMode, out error)) return false;
                pending.HasFogMode = true;
                pending.Changed.Add("fogMode");
            }

            var finalFogStart = pending.HasFogStartDistance ? pending.FogStartDistance : RenderSettings.fogStartDistance;
            var finalFogEnd = pending.HasFogEndDistance ? pending.FogEndDistance : RenderSettings.fogEndDistance;
            if ((pending.HasFogStartDistance || pending.HasFogEndDistance) && finalFogEnd < finalFogStart)
            {
                error = "fogEndDistance must be greater than or equal to fogStartDistance";
                return false;
            }

            var hasSkybox = args["skybox"] != null;
            var hasSkyboxGuid = args["skyboxGuid"] != null;
            if (hasSkybox && hasSkyboxGuid)
            {
                error = "pass either skybox or skyboxGuid, not both";
                return false;
            }
            if (hasSkybox || hasSkyboxGuid)
            {
                var reference = (string)(args["skybox"] ?? args["skyboxGuid"]);
                if (string.IsNullOrWhiteSpace(reference))
                {
                    error = "skybox reference must not be empty";
                    return false;
                }
                reference = reference.Trim().Replace('\\', '/');
                var path = reference.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? reference
                    : AssetDatabase.GUIDToAssetPath(reference);
                pending.Skybox = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (pending.Skybox == null)
                {
                    error = $"ASSET_NOT_FOUND: skybox material '{reference}'";
                    return false;
                }
                pending.HasSkybox = true;
                pending.Changed.Add("skybox");
            }

            if (!TryReadOptionalFloat(args, "reflectionIntensity", 0f, out pending.HasReflectionIntensity, out pending.ReflectionIntensity, pending.Changed, out error))
                return false;
            return true;
        }

        private static void ApplyPending(PendingSettings pending)
        {
            if (pending.HasAmbientMode) RenderSettings.ambientMode = pending.AmbientMode;
            if (pending.HasAmbientLight) RenderSettings.ambientLight = pending.AmbientLight;
            if (pending.HasAmbientSkyColor) RenderSettings.ambientSkyColor = pending.AmbientSkyColor;
            if (pending.HasAmbientEquatorColor) RenderSettings.ambientEquatorColor = pending.AmbientEquatorColor;
            if (pending.HasAmbientGroundColor) RenderSettings.ambientGroundColor = pending.AmbientGroundColor;
            if (pending.HasAmbientIntensity) RenderSettings.ambientIntensity = pending.AmbientIntensity;
            if (pending.HasFog) RenderSettings.fog = pending.Fog;
            if (pending.HasFogColor) RenderSettings.fogColor = pending.FogColor;
            if (pending.HasFogDensity) RenderSettings.fogDensity = pending.FogDensity;
            if (pending.HasFogStartDistance) RenderSettings.fogStartDistance = pending.FogStartDistance;
            if (pending.HasFogEndDistance) RenderSettings.fogEndDistance = pending.FogEndDistance;
            if (pending.HasFogMode) RenderSettings.fogMode = pending.FogMode;
            if (pending.HasSkybox) RenderSettings.skybox = pending.Skybox;
            if (pending.HasReflectionIntensity) RenderSettings.reflectionIntensity = pending.ReflectionIntensity;
        }

        private static bool TryReadOptionalColor(
            JObject args,
            string name,
            out bool hasValue,
            out Color value,
            ICollection<string> changed,
            out string error)
        {
            hasValue = args[name] != null;
            value = default;
            error = null;
            if (!hasValue) return true;
            if (!TryReadColor(args[name], name, out value, out error)) return false;
            changed.Add(name);
            return true;
        }

        private static bool TryReadOptionalFloat(
            JObject args,
            string name,
            float minimum,
            out bool hasValue,
            out float value,
            ICollection<string> changed,
            out string error)
        {
            hasValue = args[name] != null;
            value = default;
            error = null;
            if (!hasValue) return true;
            var token = args[name];
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                error = $"{name} must be a number";
                return false;
            }
            value = (float)token;
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                error = $"{name} must be finite";
                return false;
            }
            if (value < minimum)
            {
                error = $"{name} must be at least {minimum}";
                return false;
            }
            changed.Add(name);
            return true;
        }

        private static bool TryReadEnum<T>(JToken token, string name, out T value, out string error)
            where T : struct
        {
            value = default;
            var requested = token.Type == JTokenType.String ? (string)token : null;
            if (string.IsNullOrWhiteSpace(requested) || !Enum.TryParse(requested, true, out value) || !Enum.IsDefined(typeof(T), value))
            {
                error = $"invalid {name} '{token}'; expected one of [{string.Join(", ", Enum.GetNames(typeof(T)))}]";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryReadColor(JToken token, string name, out Color color, out string error)
        {
            color = default;
            error = null;
            if (token.Type == JTokenType.String)
            {
                if (ColorUtility.TryParseHtmlString((string)token, out color)) return true;
                error = $"invalid {name} color '{(string)token}'";
                return false;
            }
            if (token is JArray array && (array.Count == 3 || array.Count == 4))
            {
                if (!TryReadColorChannel(array[0], $"{name}.r", out var r, out error) ||
                    !TryReadColorChannel(array[1], $"{name}.g", out var g, out error) ||
                    !TryReadColorChannel(array[2], $"{name}.b", out var b, out error) ||
                    !TryReadColorChannel(array.Count == 4 ? array[3] : null, $"{name}.a", out var a, out error, 1f)) return false;
                color = new Color(r, g, b, a);
                return true;
            }
            if (token is JObject obj && obj["r"] != null && obj["g"] != null && obj["b"] != null)
            {
                if (!TryReadColorChannel(obj["r"], $"{name}.r", out var r, out error) ||
                    !TryReadColorChannel(obj["g"], $"{name}.g", out var g, out error) ||
                    !TryReadColorChannel(obj["b"], $"{name}.b", out var b, out error) ||
                    !TryReadColorChannel(obj["a"], $"{name}.a", out var a, out error, 1f)) return false;
                color = new Color(r, g, b, a);
                return true;
            }
            error = $"{name} must be a hex string, RGB[A] array, or {{r,g,b,a}} object";
            return false;
        }

        private static bool TryReadColorChannel(JToken token, string name, out float value, out string error, float fallback = 0f)
        {
            value = fallback;
            error = null;
            if (token == null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                error = $"{name} must be a number";
                return false;
            }
            value = (float)token;
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                error = $"{name} must be finite";
                return false;
            }
            return true;
        }

        private static JObject Snapshot(ICollection<string> changed, bool undoRegistered, Scene scene)
        {
            var skyboxPath = RenderSettings.skybox != null ? AssetDatabase.GetAssetPath(RenderSettings.skybox) : null;
            return new JObject
            {
                ["changed"] = new JArray(changed),
                ["undoRegistered"] = undoRegistered,
                ["activeScene"] = new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["handle"] = scene.GetMcpSceneHandle(),
                },
                ["ambient"] = new JObject
                {
                    ["mode"] = RenderSettings.ambientMode.ToString(),
                    ["light"] = ColorJson(RenderSettings.ambientLight),
                    ["skyColor"] = ColorJson(RenderSettings.ambientSkyColor),
                    ["equatorColor"] = ColorJson(RenderSettings.ambientEquatorColor),
                    ["groundColor"] = ColorJson(RenderSettings.ambientGroundColor),
                    ["intensity"] = RenderSettings.ambientIntensity,
                },
                ["fog"] = new JObject
                {
                    ["enabled"] = RenderSettings.fog,
                    ["mode"] = RenderSettings.fogMode.ToString(),
                    ["color"] = ColorJson(RenderSettings.fogColor),
                    ["density"] = RenderSettings.fogDensity,
                    ["startDistance"] = RenderSettings.fogStartDistance,
                    ["endDistance"] = RenderSettings.fogEndDistance,
                },
                ["skybox"] = new JObject
                {
                    ["name"] = RenderSettings.skybox != null ? RenderSettings.skybox.name : null,
                    ["path"] = skyboxPath,
                    ["guid"] = string.IsNullOrEmpty(skyboxPath) ? null : AssetDatabase.AssetPathToGUID(skyboxPath),
                },
                ["reflectionIntensity"] = RenderSettings.reflectionIntensity,
            };
        }

        private static JObject ColorJson(Color color) => new JObject
        {
            ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a,
        };

        private static bool TryRegisterUndo()
        {
            try
            {
                var method = typeof(RenderSettings).GetMethod(
                    "GetRenderSettings",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) return false;
                if (method.Invoke(null, null) is UnityEngine.Object backing && backing != null)
                {
                    Undo.RegisterCompleteObjectUndo(backing, "Apply Render Settings");
                    return true;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[RenderSettingsEditor] could not register Undo: {exception.Message}");
            }
            return false;
        }
    }
}
