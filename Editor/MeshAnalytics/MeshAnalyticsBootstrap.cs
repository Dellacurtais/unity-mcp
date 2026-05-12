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
                if (Bootstrap.ToolRegistry == null) return;

                Bootstrap.ToolRegistry.Register(new InspectSubmeshesTool());
                Bootstrap.ToolRegistry.Register(new DetectSharpEdgesTool());
                Bootstrap.ToolRegistry.Register(new DetectPlanesTool());
                Bootstrap.ToolRegistry.Register(new DetectSmoothRegionsTool());
                Bootstrap.ToolRegistry.Register(new DetectParallelChainsTool());
                Bootstrap.ToolRegistry.Register(new SamplePointsOnRegionTool());
                Bootstrap.ToolRegistry.Register(new RenderObjectViewsTool());
                Bootstrap.ToolRegistry.Register(new RenderRegionHighlightTool());

                Bootstrap.RecordEntry("[mesh-analytics] registered 8 mesh inspection / render tools");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
