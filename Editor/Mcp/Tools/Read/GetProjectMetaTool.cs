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

            var payload = new JObject
            {
                ["unityVersion"] = Application.unityVersion,
                ["projectName"] = Application.productName,
                ["companyName"] = Application.companyName,
                ["renderPipeline"] = pipeline,
                ["colorSpace"] = QualitySettings.activeColorSpace.ToString(),
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["dataPath"] = Application.dataPath,
            };

            return ToolResult.Ok(
                $"Unity {Application.unityVersion} · {pipeline} · {EditorUserBuildSettings.activeBuildTarget}",
                payload);
        }
    }
}
