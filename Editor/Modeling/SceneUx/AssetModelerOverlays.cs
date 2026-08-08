using Maicon.SceneAnalyzer.Editor.Modeling.UI;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SceneUx
{
    [Overlay(typeof(SceneView), "asset-modeler-modeling", "Asset Modeler")]
    internal sealed class AssetModelerToolsOverlay : IMGUIOverlay
    {
        private static readonly string[] ModeLabels = { "Objeto", "Vértice", "Aresta", "Face", "Região", "Bone" };
        private static readonly string[] OrientationLabels = { "Global", "Local", "Normal" };

        public override void OnGUI()
        {
            if (!AssetModelerToolContext.IsActive)
            {
                if (GUILayout.Button("Ativar Asset Modeler", EditorStyles.miniButton))
                    AssetModelerModelingState.ActivateMode(AssetModelerModelingState.Mode);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < ModeLabels.Length; i++)
            {
                bool active = (int)AssetModelerModelingState.Mode == i;
                if (GUILayout.Toggle(active, ModeLabels[i], EditorStyles.miniButton) && !active)
                    AssetModelerModelingState.Mode = (AssetModelerEditMode)i;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            int orientation = GUILayout.Toolbar((int)AssetModelerModelingState.Orientation, OrientationLabels, EditorStyles.miniButton);
            AssetModelerModelingState.Orientation = (ModelingHandleOrientation)orientation;
            AssetModelerModelingState.Symmetry = GUILayout.Toggle(AssetModelerModelingState.Symmetry, "Simetria X", EditorStyles.miniButton);
            AssetModelerModelingState.SnapEnabled = GUILayout.Toggle(AssetModelerModelingState.SnapEnabled, "Snap", EditorStyles.miniButton);
            EditorGUILayout.EndHorizontal();

            var modeler = AssetModelerSceneController.AttachedWindow;
            if (modeler == null)
            {
                EditorGUILayout.LabelField("Abra Window > Asset Modeler (Unity MCP).", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (modeler.SceneDocument == null)
            {
                using (new EditorGUI.DisabledScope(modeler.SceneTarget == null))
                    if (GUILayout.Button("Criar cópia editável", EditorStyles.miniButton)) modeler.SceneCreateEditableCopy();
            }
            else if (modeler.SceneSupportsPreview)
            {
                if (GUILayout.Button(modeler.SceneHasPreview ? "Atualizar" : "Preview", EditorStyles.miniButton)) modeler.ScenePreviewCurrent();
                using (new EditorGUI.DisabledScope(!modeler.SceneHasPreview))
                {
                    if (GUILayout.Button("Aplicar", EditorStyles.miniButton)) modeler.SceneApplyCurrent();
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(24f))) modeler.SceneCancelPreview();
                }
            }
            else EditorGUILayout.LabelField("A ferramenta atual executa direto na cena.", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }
    }

    [Overlay(typeof(SceneView), "asset-modeler-diagnostics", "Mesh Diagnostics")]
    internal sealed class AssetModelerDiagnosticsOverlay : IMGUIOverlay
    {
        public override void OnGUI()
        {
            var topology = AssetModelerSceneController.CurrentTopology;
            string boundaries = topology != null ? topology.BoundaryEdges.Count.ToString() : "—";
            string nonManifold = topology != null ? topology.NonManifoldEdges.Count.ToString() : "—";

            AssetModelerModelingState.ShowBoundaries = GUILayout.Toggle(
                AssetModelerModelingState.ShowBoundaries, $"Bordas abertas ({boundaries})", EditorStyles.miniButton);
            AssetModelerModelingState.ShowNonManifold = GUILayout.Toggle(
                AssetModelerModelingState.ShowNonManifold, $"Non-manifold ({nonManifold})", EditorStyles.miniButton);
            AssetModelerModelingState.ShowFlippedNormals = GUILayout.Toggle(
                AssetModelerModelingState.ShowFlippedNormals, $"Normais invertidas ({AssetModelerMeshDiagnostics.FlippedCount})", EditorStyles.miniButton);
            AssetModelerModelingState.ShowUvSeams = GUILayout.Toggle(
                AssetModelerModelingState.ShowUvSeams, $"UV seams ({AssetModelerMeshDiagnostics.UvSeamCount})", EditorStyles.miniButton);

            if (topology == null)
                EditorGUILayout.LabelField("Ative o contexto Asset Modeler com uma cópia editável.", EditorStyles.miniLabel);
        }
    }
}
