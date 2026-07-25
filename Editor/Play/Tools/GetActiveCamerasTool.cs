using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class GetActiveCamerasTool : IMcpTool
    {
        public string Name => "get_active_cameras";
        public string Description =>
            "Lists every enabled Camera in the scene with its instanceId, name, depth, tag, projection mode, position, and forward vector. " +
            "Use to pick a target for screenshot_game_view, or to confirm overlay/base camera setup in URP. " +
            "Detects URP renderType (Base/Overlay) via reflection when present.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;
        public JObject OutputSchema => SceneToolSchemas.ActiveCameras;

        public ToolResult Execute(JObject args)
        {
            var cams = Camera.allCameras;
            var arr = new JArray();
            foreach (var c in cams)
            {
                if (c == null) continue;
                var t = c.transform;
                var scene = c.gameObject.scene;
                var scenePath = scene.path ?? string.Empty;
                var entry = new JObject
                {
                    ["instanceId"] = c.GetInstanceID(),
                    ["name"] = c.name,
                    ["sceneName"] = scene.name,
                    ["scenePath"] = scenePath,
                    ["sceneGuid"] = string.IsNullOrEmpty(scenePath) ? null : AssetDatabase.AssetPathToGUID(scenePath),
                    ["sceneHandle"] = (int)scene.handle,
                    ["enabled"] = c.enabled,
                    ["tag"] = c.tag,
                    ["depth"] = c.depth,
                    ["isOrthographic"] = c.orthographic,
                    ["fov"] = c.fieldOfView,
                    ["nearClip"] = c.nearClipPlane,
                    ["farClip"] = c.farClipPlane,
                    ["cullingMask"] = c.cullingMask,
                    ["position"] = new JArray { t.position.x, t.position.y, t.position.z },
                    ["forward"] = new JArray { t.forward.x, t.forward.y, t.forward.z },
                };
                var renderType = TryGetUrpRenderType(c);
                if (renderType != null) entry["renderType"] = renderType;
                arr.Add(entry);
            }

            var main = Camera.main;
            string summary = $"{arr.Count} enabled camera(s)";
            if (main != null) summary += $" (main: '{main.name}')";
            return ToolResult.Ok(summary, new JObject
            {
                ["count"] = arr.Count,
                ["mainCameraInstanceId"] = main != null ? main.GetInstanceID() : (JToken)null,
                ["cameras"] = arr,
            });
        }

        private static string TryGetUrpRenderType(Camera c)
        {
            try
            {
                var addType = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
                if (addType == null) return null;
                var add = c.GetComponent(addType);
                if (add == null) return null;
                var prop = addType.GetProperty("renderType");
                if (prop == null) return null;
                var val = prop.GetValue(add);
                return val?.ToString();
            }
            catch { return null; }
        }
    }
}
