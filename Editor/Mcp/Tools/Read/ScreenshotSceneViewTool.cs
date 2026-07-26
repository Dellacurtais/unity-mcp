using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ScreenshotSceneViewTool : IMcpTool
    {
        public string Name => "screenshot_scene_view";
        public string Description =>
            "Renders the active Unity SceneView camera into a base64-encoded PNG and returns it as an MCP image content block, so the LLM can visually reason about the current scene appearance (lighting, layout, post-fx). Defaults to 1024x576; cap at 2048x2048.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["width"] = new JObject { ["type"] = "integer", ["default"] = 1024, ["minimum"] = 64, ["maximum"] = 2048 },
                ["height"] = new JObject { ["type"] = "integer", ["default"] = 576, ["minimum"] = 64, ["maximum"] = 2048 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var width = Mathf.Clamp((int?)args["width"] ?? 1024, 64, 2048);
            var height = Mathf.Clamp((int?)args["height"] ?? 576, 64, 2048);

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
                return ToolResult.Error("NO_SCENE_VIEW",
                    "no active SceneView; open the Scene tab in the editor and try again");

            var camera = sceneView.camera;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                var png = tex.EncodeToPNG();
                var b64 = Convert.ToBase64String(png);
                return ToolResult.Image(b64, "image/png",
                    $"SceneView screenshot {width}×{height} ({png.Length:N0} bytes)");
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
