using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class GetProjectMetaTool : IMcpTool
    {
        public string Name => "get_project_meta";
        public string Description =>
            "Returns Unity version, render pipeline (Built-in/URP/HDRP), color space, active build target, and project name. Call this first when authoring so the agent picks compatible materials and prefabs.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["unityVersion"] = new JObject { ["type"] = "string" },
                ["projectName"] = new JObject { ["type"] = "string" },
                ["companyName"] = new JObject { ["type"] = "string" },
                ["renderPipeline"] = new JObject { ["type"] = "string" },
                ["defaultRenderPipeline"] = new JObject { ["type"] = new JArray { "object", "null" } },
                ["qualityRenderPipelineOverride"] = new JObject { ["type"] = new JArray { "object", "null" } },
                ["activeRenderPipeline"] = new JObject { ["type"] = new JArray { "object", "null" } },
                ["colorSpace"] = new JObject { ["type"] = "string" },
                ["desiredColorSpace"] = new JObject { ["type"] = "string" },
                ["activeColorSpace"] = new JObject { ["type"] = "string" },
                ["activeBuildTarget"] = new JObject { ["type"] = "string" },
                ["dataPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray
            {
                "unityVersion", "projectName", "companyName", "renderPipeline", "defaultRenderPipeline",
                "qualityRenderPipelineOverride", "activeRenderPipeline", "colorSpace", "desiredColorSpace",
                "activeColorSpace", "activeBuildTarget", "dataPath",
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var pipeline = "Built-in";
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp != null)
            {
                var typeName = rp.GetType().FullName ?? "";
                if (typeName.Contains("Universal")) pipeline = "URP";
                else if (typeName.Contains("HighDefinition") || typeName.Contains("HD")) pipeline = "HDRP";
                else pipeline = typeName;
            }

            var defaultPipeline = GraphicsSettings.defaultRenderPipeline;
            var qualityPipeline = QualitySettings.renderPipeline;
            var payload = new JObject
            {
                ["unityVersion"] = Application.unityVersion,
                ["projectName"] = Application.productName,
                ["companyName"] = Application.companyName,
                ["renderPipeline"] = pipeline,
                ["defaultRenderPipeline"] = PipelineJson(defaultPipeline),
                ["qualityRenderPipelineOverride"] = PipelineJson(qualityPipeline),
                ["activeRenderPipeline"] = PipelineJson(rp),
                ["colorSpace"] = PlayerSettings.colorSpace.ToString(),
                ["desiredColorSpace"] = QualitySettings.desiredColorSpace.ToString(),
                ["activeColorSpace"] = QualitySettings.activeColorSpace.ToString(),
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["dataPath"] = Application.dataPath,
            };

            return ToolResult.Ok(
                $"Unity {Application.unityVersion} · {pipeline} · {EditorUserBuildSettings.activeBuildTarget}",
                payload);
        }

        private static JObject PipelineJson(RenderPipelineAsset pipeline)
        {
            if (pipeline == null) return null;
            var path = AssetDatabase.GetAssetPath(pipeline);
            return new JObject
            {
                ["name"] = pipeline.name,
                ["type"] = pipeline.GetType().FullName,
                ["assetPath"] = string.IsNullOrEmpty(path) ? null : path,
                ["assetGuid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
            };
        }
    }
}
