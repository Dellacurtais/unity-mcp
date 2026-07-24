using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.HDRP
{
    [InitializeOnLoad]
    internal static class HdrpBootstrap
    {
        private const string PackageId = "com.unity.render-pipelines.high-definition";
        private static readonly string[] PipelineAssets = { "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset" };
        private static readonly string[] GlobalSettings = { "UnityEngine.Rendering.HighDefinition.HDRenderPipelineGlobalSettings" };
        private static readonly IReadOnlyDictionary<string, string[]> Types = new Dictionary<string, string[]> { ["pipelineAsset"] = PipelineAssets, ["globalSettings"] = GlobalSettings };
        private static readonly IReadOnlyDictionary<string, string[]> PipelineProperties = new Dictionary<string, string[]>
        {
            ["supportRayTracing"] = new[] { "m_RenderPipelineSettings.supportRayTracing", "m_RenderPipelineSettings.m_SupportRayTracing" },
            ["supportDecals"] = new[] { "m_RenderPipelineSettings.supportDecals", "m_RenderPipelineSettings.m_SupportDecals" },
            ["supportMotionVectors"] = new[] { "m_RenderPipelineSettings.supportMotionVectors", "m_RenderPipelineSettings.m_SupportMotionVectors" },
            ["supportTransparentBackface"] = new[] { "m_RenderPipelineSettings.supportTransparentBackface", "m_RenderPipelineSettings.m_SupportTransparentBackface" },
            ["lodBias"] = new[] { "m_RenderPipelineSettings.lodBias", "m_RenderPipelineSettings.m_LODBias" },
        };

        static HdrpBootstrap() { EditorApplication.delayCall += Initialize; }
        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("hdrp", PackageId,
                    new OptionalContextTool("get_hdrp_context", "Reports HDRP package and asset type context.", PackageId, Types),
                    new OptionalListAssetsTool("list_hdrp_pipeline_assets", "Lists HDRP pipeline and global settings assets.", PackageId, PipelineAssets, GlobalSettings),
                    new OptionalConfigureAssetTool("configure_hdrp_pipeline_asset", "Configure HDRP pipeline asset", new[] { PipelineAssets }, PipelineProperties),
                    new ValidateHdrpSetupTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private sealed class ValidateHdrpSetupTool : IMcpTool
        {
            public string Name => "validate_hdrp_setup";
            public string Description => "Validates local HDRP 17.x project assets plus current, default, and every quality-level pipeline assignment.";
            public JObject InputSchema => OptionalToolSupport.EmptySchema();
            public bool IsWrite => false;
            public ToolResult Execute(JObject arguments) =>
                OptionalAssetToolSupport.ValidateRenderPipelineSetup(
                    "hdrp", PackageId, PipelineAssets, "NO_HDRP_PIPELINE_ASSET", "HDRP_NOT_CURRENT_PIPELINE");
        }
    }
}
