using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Mcp;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling
{
    [InitializeOnLoad]
    internal static class ModelingBootstrap
    {
        static ModelingBootstrap()
        {
            EditorApplication.delayCall += Initialize;
        }

        private static void Initialize()
        {
            try
            {
                ModelingOperationCatalog.RegisterAll();
                if (Bootstrap.ToolRegistry == null
                    || Bootstrap.ToolRegistry.TryGet("mesh_analyze_body_parts", out _)) return;
                Bootstrap.ToolRegistry.RegisterRange(new IMcpTool[]
                {
                    new AnalyzeBodyPartsTool(),
                    new GetSmartBodySplitProposalTool(),
                    new PreviewSmartBodySplitTool(),
                    new ApplySmartBodySplitTool(),
                    new ValidateSmartBodySplitTool(),
                    new CreateModularCharacterPrefabTool(),
                    new RefineSmartBodySplitBoundaryTool(),
                    new RecombineSmartBodySplitTool(),
                    new BeginMeshEditSessionTool(),
                    new GetMeshEditContextTool(),
                    new PreviewMeshOperationTool(),
                    new ApplyMeshOperationTool(),
                    new CommitMeshEditSessionTool(),
                    new RebaseMeshEditSessionTool(),
                    new DiscardMeshEditSessionTool(),
                    new ValidateEditableMeshTool(),
                    new GetMeshSelectionTool(),
                    new SelectMeshElementsTool(),
                    new SelectMeshRegionTool(),
                    new LinkMeshRegionsTool(),
                    new PaintMeshRegionConstraintTool(),
                    new ReorderMeshModifierTool(),
                    new RemoveMeshModifierTool(),
                    new PreviewSurfaceFitTool(),
                    new ApplySurfaceFitTool(),
                    new EvaluatePoseClearanceTool(),
                    new StageExternalModelAssetTool(),
                    new ExportMeshFbxTool(),
                });
                Bootstrap.RecordEntry("[asset-modeler] registered 28 modeling tools");
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }
}
