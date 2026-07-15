using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.URP
{
    [InitializeOnLoad]
    internal static class UrpBootstrap
    {
        private const string PackageId = "com.unity.render-pipelines.universal";
        private static readonly string[] PipelineAssets = { "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset" };
        private static readonly string[] RendererData = { "UnityEngine.Rendering.Universal.UniversalRendererData", "UnityEngine.Rendering.Universal.Renderer2DData", "UnityEngine.Rendering.Universal.ScriptableRendererData" };
        private static readonly IReadOnlyDictionary<string, string[]> Types = new Dictionary<string, string[]> { ["pipelineAsset"] = PipelineAssets, ["rendererData"] = RendererData };
        private static readonly IReadOnlyDictionary<string, string[]> PipelineProperties = new Dictionary<string, string[]>
        {
            ["renderScale"] = new[] { "m_RenderScale" },
            ["msaaSampleCount"] = new[] { "m_MSAA" },
            ["supportsHDR"] = new[] { "m_SupportsHDR" },
            ["supportsCameraDepthTexture"] = new[] { "m_SupportsCameraDepthTexture" },
            ["supportsCameraOpaqueTexture"] = new[] { "m_SupportsCameraOpaqueTexture" },
            ["shadowDistance"] = new[] { "m_ShadowDistance" },
            ["supportsSoftShadows"] = new[] { "m_SupportsSoftShadows" },
            ["additionalLightsPerObjectLimit"] = new[] { "m_AdditionalLightsPerObjectLimit" },
        };
        private static readonly IReadOnlyDictionary<string, string[]> RendererProperties = new Dictionary<string, string[]>
        {
            ["opaqueLayerMask"] = new[] { "m_OpaqueLayerMask" },
            ["transparentLayerMask"] = new[] { "m_TransparentLayerMask" },
            ["useNativeRenderPass"] = new[] { "m_UseNativeRenderPass" },
            ["intermediateTextureMode"] = new[] { "m_IntermediateTextureMode" },
        };

        static UrpBootstrap() { EditorApplication.delayCall += Initialize; }
        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("urp", PackageId,
                    new OptionalContextTool("get_urp_context", "Reports URP package and asset type context.", PackageId, Types),
                    new OptionalListAssetsTool("list_urp_pipeline_assets", "Lists URP pipeline and renderer data assets.", PackageId, PipelineAssets, RendererData),
                    new OptionalConfigureAssetTool("configure_urp_pipeline_asset", "Configure URP pipeline asset", new[] { PipelineAssets }, PipelineProperties),
                    new OptionalConfigureAssetTool("configure_urp_renderer_data", "Configure URP renderer data", new[] { RendererData }, RendererProperties),
                    new ValidateUrpSetupTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private sealed class ValidateUrpSetupTool : IMcpTool
        {
            public string Name => "validate_urp_setup";
            public string Description => "Validates URP package assets and active pipeline assignment.";
            public JObject InputSchema => OptionalToolSupport.EmptySchema();
            public bool IsWrite => false;
            public ToolResult Execute(JObject arguments)
            {
                var assets = OptionalAssetToolSupport.FindAssets(PipelineAssets);
                var current = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                var issues = new JArray();
                if (assets.Length == 0) issues.Add(new JObject { ["severity"] = "error", ["code"] = "NO_URP_PIPELINE_ASSET" });
                if (current == null || current.GetType().FullName != PipelineAssets[0])
                    issues.Add(new JObject { ["severity"] = "warning", ["code"] = "URP_NOT_CURRENT_PIPELINE", ["current"] = current != null ? current.GetType().FullName : "BuiltIn" });
                return ToolResult.Ok($"{issues.Count} URP issue(s)", new JObject { ["valid"] = !issues.Any(token => (string)token["severity"] == "error"), ["issues"] = issues });
            }
        }
    }
}
