using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
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

        public ToolResult Execute(JObject args)
        {
            var cams = Camera.allCameras;
            var arr = new JArray();
            foreach (var c in cams)
            {
                if (c == null) continue;
                var t = c.transform;
                var entry = new JObject
                {
                    ["instanceId"] = c.GetInstanceID(),
                    ["name"] = c.name,
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

            string summary = $"{cams.Length} active camera(s)";
            if (Camera.main != null) summary += $" (main: '{Camera.main.name}')";
            return ToolResult.Ok(summary, new JObject { ["cameras"] = arr });
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
