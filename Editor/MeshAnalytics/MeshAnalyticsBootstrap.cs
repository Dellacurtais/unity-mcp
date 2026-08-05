using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics
{
    /// Side-loaded init for mesh-analytics tools (submesh inspection, sharp-edge
    /// detection, plane RANSAC, region sampling, directed renders, region
    /// highlight overlays). Lives in its own asmdef so the folder can be
    /// deleted to disable the feature without touching the main plugin. Defers
    /// to delayCall so Bootstrap.ToolRegistry is populated first (same pattern
    /// as PostFxBootstrap / PlayBootstrap / BridgeBootstrap).
    [InitializeOnLoad]
    internal static class MeshAnalyticsBootstrap
    {
        static MeshAnalyticsBootstrap()
        {
            EditorApplication.delayCall += Init;
        }

        private static void Init()
        {
            try
            {
                if (Bootstrap.ToolRegistry == null || Bootstrap.ToolRegistry.TryGet("mesh_inspect_submeshes", out _)) return;

                Bootstrap.ToolRegistry.RegisterRange(new Maicon.SceneAnalyzer.Editor.Mcp.IMcpTool[]
                {
                    new InspectSubmeshesTool(),
                    new DetectSharpEdgesTool(),
                    new DetectPlanesTool(),
                    new DetectSmoothRegionsTool(),
                    new DetectParallelChainsTool(),
                    new SamplePointsOnRegionTool(),
                    new ScreenshotObjectTool(),
                    new RenderObjectViewsTool(),
                    new RenderRegionHighlightTool(),
                    new MeshPickFromScreenTool(),
                    new SegmentViaSidecarTool(),
                    new SidecarHealthTool(),
                    new EnableMeshReadWriteTool(),
                });

                Bootstrap.RecordEntry("[mesh-analytics] registered 13 mesh tools (9 in-process + 3 picking/sidecar + 1 enable_read_write)");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
