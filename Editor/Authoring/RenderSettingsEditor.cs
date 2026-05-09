using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Tweaks scene-level rendering settings: ambient lighting, fog, skybox, and
    /// reflection intensity. RenderSettings is a static API, so individual
    /// undo isn't reliable; we mark the scene dirty so it's at least obvious
    /// that something changed.
    public static class RenderSettingsEditor
    {
        public static OpResult Apply(JObject args)
        {
            // Ambient
            if (args["ambientMode"] != null
                && Enum.TryParse((string)args["ambientMode"], true, out AmbientMode mode))
                RenderSettings.ambientMode = mode;

            if (args["ambientLight"] != null)
                RenderSettings.ambientLight = JsonHelpers.ParseColor(args["ambientLight"]);
            if (args["ambientSkyColor"] != null)
                RenderSettings.ambientSkyColor = JsonHelpers.ParseColor(args["ambientSkyColor"]);
            if (args["ambientEquatorColor"] != null)
                RenderSettings.ambientEquatorColor = JsonHelpers.ParseColor(args["ambientEquatorColor"]);
            if (args["ambientGroundColor"] != null)
                RenderSettings.ambientGroundColor = JsonHelpers.ParseColor(args["ambientGroundColor"]);
            if (args["ambientIntensity"] != null)
                RenderSettings.ambientIntensity = (float)args["ambientIntensity"];

            // Fog
            if (args["fog"] != null) RenderSettings.fog = (bool)args["fog"];
            if (args["fogColor"] != null) RenderSettings.fogColor = JsonHelpers.ParseColor(args["fogColor"]);
            if (args["fogDensity"] != null) RenderSettings.fogDensity = (float)args["fogDensity"];
            if (args["fogStartDistance"] != null) RenderSettings.fogStartDistance = (float)args["fogStartDistance"];
            if (args["fogEndDistance"] != null) RenderSettings.fogEndDistance = (float)args["fogEndDistance"];
            if (args["fogMode"] != null
                && Enum.TryParse((string)args["fogMode"], true, out FogMode fm))
                RenderSettings.fogMode = fm;

            // Skybox
            if (args["skyboxGuid"] != null || args["skybox"] != null)
            {
                var matRef = (string)(args["skyboxGuid"] ?? args["skybox"]);
                var path = matRef.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? matRef
                    : AssetDatabase.GUIDToAssetPath(matRef);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) return OpResult.Fail($"skybox material not found: {matRef}");
                RenderSettings.skybox = mat;
            }

            // Reflection
            if (args["reflectionIntensity"] != null)
                RenderSettings.reflectionIntensity = (float)args["reflectionIntensity"];

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return OpResult.Success(0, "render settings updated");
        }
    }
}
