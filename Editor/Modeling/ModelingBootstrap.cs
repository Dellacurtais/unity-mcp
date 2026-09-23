using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Mcp;
using Maicon.SceneAnalyzer.Editor.Modeling.Rigging;
using System.Collections.Generic;
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
                if (Bootstrap.ToolRegistry == null) return;
                var candidates = new IMcpTool[]
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
                    new AnalyzeMeshCleanupTool(),
                    new PreviewMeshCleanupTool(),
                    new ApplyMeshCleanupTool(),
                    new PreviewModelScaleTool(),
                    new ApplyModelScaleTool(),
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
                    new RigInspectTool(),
                    new RigValidateClipsTool(),
                    new RigCreateEditableSkinTool(),
                    new RigApplyWeightOperationTool(),
                    new RigRebuildBindPosesTool(),
                    new RigTransferWeightsTool(),
                    new RigEditSkeletonTool(),
                    new AnimationCapturePoseTool(),
                    new AnimationApplyPoseTool(),
                    new AnimationTransformClipTool(),
                    new RigAnalyzeRetargetTool(),
                    new RigApplyRetargetPoseTool(),
                    new RigRetargetClipTool(),
                    new RigCreateIkTool(),
                    new RigPreviewIkTool(),
                    new RigBakeIkClipTool(),
                    new RigAnalyzeAutoRigTool(),
                    new RigPreviewAutoRigTool(),
                    new RigApplyAutoRigTool(),
                };
                var missing = new List<IMcpTool>(candidates.Length);
                for (int i = 0; i < candidates.Length; i++)
                    if (!Bootstrap.ToolRegistry.TryGet(candidates[i].Name, out _)) missing.Add(candidates[i]);
                if (missing.Count == 0) return;
                Bootstrap.ToolRegistry.RegisterRange(missing);
                Bootstrap.RecordEntry($"[asset-modeler] registered {missing.Count} of {candidates.Length} modeling and rigging tools");
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }
}
