using System;
using System.Collections.Generic;
using System.Linq;
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
            public string Description => "Validates HDRP package assets and active pipeline assignment.";
            public JObject InputSchema => OptionalToolSupport.EmptySchema();
            public bool IsWrite => false;
            public ToolResult Execute(JObject arguments)
            {
                var assets = OptionalAssetToolSupport.FindAssets(PipelineAssets);
                var current = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                var issues = new JArray();
                if (assets.Length == 0) issues.Add(new JObject { ["severity"] = "error", ["code"] = "NO_HDRP_PIPELINE_ASSET" });
                if (current == null || current.GetType().FullName != PipelineAssets[0])
                    issues.Add(new JObject { ["severity"] = "warning", ["code"] = "HDRP_NOT_CURRENT_PIPELINE", ["current"] = current != null ? current.GetType().FullName : "BuiltIn" });
                return ToolResult.Ok($"{issues.Count} HDRP issue(s)", new JObject { ["valid"] = !issues.Any(token => (string)token["severity"] == "error"), ["issues"] = issues });
            }
        }
    }
}
