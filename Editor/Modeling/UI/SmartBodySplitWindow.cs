using System;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.UI
{
    public sealed class SmartBodySplitWindow : EditorWindow
    {
        private static readonly string[] PresetLabels =
        {
            SmartBodySplitPresets.Label(SmartBodySplitPreset.Core),
            SmartBodySplitPresets.Label(SmartBodySplitPreset.HandsAndFeet),
            SmartBodySplitPresets.Label(SmartBodySplitPreset.Modular),
            SmartBodySplitPresets.Label(SmartBodySplitPreset.Fps),
            SmartBodySplitPresets.Label(SmartBodySplitPreset.Damage),
            SmartBodySplitPresets.Label(SmartBodySplitPreset.Clothing),
        };

        private GameObject _target;
        private Renderer[] _renderers = Array.Empty<Renderer>();
        private int _rendererIndex;
        private SmartBodySplitProposal _proposal;
        private SmartBodySplitPreset _preset = SmartBodySplitPreset.Core;
        private string _outputFolder = string.Empty;
        private Vector2 _scroll;
        private string _error;
        private bool _showBoundaryEditor;
        private int _boundarySubMesh;
        private int _boundaryTriangle;
        private int _boundaryPartIndex;
        private int _boundaryGrowRings;
        private bool _closeCutSurfaces = true;

        [MenuItem("Window/Asset Modeler/Smart Body Split")]
        public static void OpenWindow() => Open(Selection.activeGameObject);

        [MenuItem("GameObject/Asset Modeler/Smart Body Split...", false, 49)]
        public static void OpenFromSelection() => Open(Selection.activeGameObject);

        [MenuItem("GameObject/Asset Modeler/Smart Body Split...", true)]
        private static bool ValidateOpenFromSelection()
            => Selection.activeGameObject != null
                && EditableMeshFactory.TryResolveRenderer(Selection.activeGameObject, out _, out _, out _);

        [MenuItem("GameObject/Asset Modeler/Make Editable Copy", false, 48)]
        private static void MakeEditableCopy()
        {
            try
            {
                var result = EditableMeshFactory.Create(Selection.activeGameObject);
                Selection.activeGameObject = result.EditableObject;
                EditorGUIUtility.PingObject(result.Document);
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("Asset Modeler", exception.Message, "OK");
            }
        }

        [MenuItem("GameObject/Asset Modeler/Make Editable Copy", true)]
        private static bool ValidateMakeEditableCopy() => ValidateOpenFromSelection();

        public static SmartBodySplitWindow Open(
            GameObject target,
            SmartBodySplitPreset preset = SmartBodySplitPreset.Core)
        {
            var window = GetWindow<SmartBodySplitWindow>();
            window.titleContent = new GUIContent("Asset Modeler — Smart Split");
            window.minSize = new Vector2(390f, 420f);
            window._preset = preset;
            var active = SmartBodySplitPreviewController.ActiveProposal;
            if (active != null && active.Target == target && active.Preset == preset)
            {
                window._target = target;
                window._renderers = target != null ? CollectRenderers(target) : Array.Empty<Renderer>();
                window._rendererIndex = Array.IndexOf(window._renderers, active.SourceRenderer);
                if (window._rendererIndex < 0) window._rendererIndex = 0;
                window._proposal = active;
                window._error = null;
                window.Repaint();
            }
            else window.SetTarget(target);
            window.Show();
            return window;
        }

        private void OnDisable()
        {
            if (SmartBodySplitPreviewController.ActiveProposal == _proposal)
                SmartBodySplitPreviewController.Clear();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Smart Body Split", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Analisa o personagem e cria partes principais preservando materiais, UVs, rig, pesos, bind poses e blend shapes. Revise as cores antes de criar os assets.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            var target = (GameObject)EditorGUILayout.ObjectField("Personagem", _target, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) SetTarget(target);

            if (_renderers.Length > 1)
            {
                var labels = new string[_renderers.Length];
                for (int i = 0; i < labels.Length; i++)
                    labels[i] = AnimationUtility.CalculateTransformPath(_renderers[i].transform, _target.transform)
                        + " — " + _renderers[i].GetType().Name;
                EditorGUI.BeginChangeCheck();
                _rendererIndex = EditorGUILayout.Popup("Mesh do personagem", _rendererIndex, labels);
                if (EditorGUI.EndChangeCheck()) Analyze();
                EditorGUILayout.HelpBox(
                    $"Este personagem possui {_renderers.Length} renderers. Cada mesh pode ser revisada e dividida separadamente.",
                    MessageType.Info);
            }

            EditorGUI.BeginChangeCheck();
            _preset = (SmartBodySplitPreset)EditorGUILayout.Popup("Preset", (int)_preset, PresetLabels);
            if (EditorGUI.EndChangeCheck() && _target != null) Analyze();

            _outputFolder = EditorGUILayout.TextField(
                new GUIContent("Pasta de saída", "Vazio usa Assets/AssetModeler/SmartSplits/<personagem>."),
                _outputFolder);
            _closeCutSurfaces = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Fechar cortes inteligentemente",
                    "Detecta loops criados entre as partes e gera tampas usando os mesmos vértices, rig e materiais."),
                _closeCutSurfaces);
            if (_closeCutSurfaces)
                EditorGUILayout.HelpBox(
                    "Os cortes receberão geometria real. A operação falha com orientação útil se encontrar uma fronteira aberta ou ramificada.",
                    MessageType.Info);

            using (new EditorGUI.DisabledScope(_target == null))
            {
                if (GUILayout.Button("Analisar novamente", GUILayout.Height(28f))) Analyze();
            }

            if (!string.IsNullOrEmpty(_error))
                EditorGUILayout.HelpBox(_error, MessageType.Error);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_proposal != null)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Proposta", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Preset", SmartBodySplitPresets.Label(_proposal.Preset));
                EditorGUILayout.LabelField("Rig", _proposal.RigType);
                EditorGUILayout.LabelField("Triângulos", _proposal.SourceTriangleCount.ToString("N0"));
                EditorGUILayout.LabelField("Confiança média", _proposal.AverageConfidence.ToString("P1"));
                EditorGUILayout.LabelField("Preview hash", ShortHash(_proposal.PreviewHash));
                EditorGUILayout.Space(4f);
                for (int i = 0; i < _proposal.Parts.Count; i++)
                {
                    var part = _proposal.Parts[i];
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                    EditorGUILayout.LabelField(part.Label, GUILayout.MinWidth(150f));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"{part.Triangles.Count:N0} tris", GUILayout.Width(90f));
                    EditorGUILayout.LabelField(part.AverageConfidence.ToString("P0"), GUILayout.Width(50f));
                    EditorGUILayout.EndHorizontal();
                }
                for (int i = 0; i < _proposal.Warnings.Count; i++)
                    EditorGUILayout.HelpBox(_proposal.Warnings[i], MessageType.Warning);

                EditorGUILayout.Space(6f);
                _showBoundaryEditor = EditorGUILayout.Foldout(
                    _showBoundaryEditor,
                    "Corrigir fronteira manualmente",
                    true);
                if (_showBoundaryEditor) DrawBoundaryEditor();
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(_proposal == null))
            {
                if (GUILayout.Button("Criar partes", GUILayout.Height(34f))) Apply();
            }
        }

        private void DrawBoundaryEditor()
        {
            var parts = SmartBodySplitPresets.Parts(_proposal.Preset);
            var labels = new string[parts.Count];
            for (int i = 0; i < parts.Count; i++) labels[i] = SmartBodySplitPartNames.Label(parts[i]);
            _boundarySubMesh = Mathf.Max(0, EditorGUILayout.IntField("Submesh", _boundarySubMesh));
            _boundaryTriangle = Mathf.Max(0, EditorGUILayout.IntField("Triângulo", _boundaryTriangle));
            _boundaryPartIndex = EditorGUILayout.Popup("Mover para", Mathf.Clamp(_boundaryPartIndex, 0, Math.Max(0, parts.Count - 1)), labels);
            _boundaryGrowRings = EditorGUILayout.IntSlider("Expandir anéis", _boundaryGrowRings, 0, 8);
            var lowConfidence = SmartBodySplitBoundaryRefiner.LowConfidenceTriangles(_proposal, 0.6f, 256);
            EditorGUILayout.LabelField("Triângulos de baixa confiança", lowConfidence.Count.ToString("N0"));
            if (lowConfidence.Count > 0 && GUILayout.Button("Usar próximo triângulo de baixa confiança"))
            {
                _boundarySubMesh = lowConfidence[0].SubMesh;
                _boundaryTriangle = lowConfidence[0].TriangleIndex;
            }
            if (GUILayout.Button("Reatribuir e atualizar preview"))
            {
                try
                {
                    SmartBodySplitBoundaryRefiner.Reassign(
                        _proposal,
                        _boundarySubMesh,
                        _boundaryTriangle,
                        parts[_boundaryPartIndex],
                        _boundaryGrowRings);
                    SmartBodySplitPreviewController.Show(_proposal);
                    _error = null;
                    SceneView.RepaintAll();
                }
                catch (Exception exception)
                {
                    _error = exception.Message;
                }
            }
        }

        private void SetTarget(GameObject target)
        {
            _target = target;
            _proposal = null;
            _error = null;
            _renderers = target != null ? CollectRenderers(target) : Array.Empty<Renderer>();
            _rendererIndex = Mathf.Clamp(_rendererIndex, 0, Math.Max(0, _renderers.Length - 1));
            if (_target != null) Analyze();
            else SmartBodySplitPreviewController.Clear();
            Repaint();
        }

        private void Analyze()
        {
            _error = null;
            _proposal = null;
            try
            {
                _proposal = _renderers.Length > 0
                    ? SmartBodySplitAnalyzer.AnalyzeRenderer(_target, _renderers[_rendererIndex], _preset)
                    : SmartBodySplitAnalyzer.Analyze(_target, _preset);
                SmartBodySplitPreviewController.Show(_proposal);
                SceneView.lastActiveSceneView?.FrameSelected();
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                SmartBodySplitPreviewController.Clear();
            }
            Repaint();
        }

        private void Apply()
        {
            if (_proposal == null) return;
            if (!EditorUtility.DisplayDialog(
                    "Criar partes do corpo",
                    $"Criar {_proposal.Parts.Count} meshes separadas? O FBX original não será modificado.",
                    "Criar partes",
                    "Cancelar")) return;

            try
            {
                var result = SmartBodySplitService.Apply(
                    _proposal,
                    string.IsNullOrWhiteSpace(_outputFolder) ? null : _outputFolder,
                    _proposal.SourceHash,
                    _proposal.PreviewHash,
                    _closeCutSurfaces);
                Selection.activeGameObject = result.PartsRoot;
                EditorGUIUtility.PingObject(result.BodyPartSet);
                EditorUtility.DisplayDialog(
                    "Smart Body Split",
                    $"{result.Manifest.Parts.Count} partes criadas e validadas em {result.OutputFolder}. " +
                    $"Tampas: {CapTriangleCount(result.Manifest):N0} triângulos em {CapLoopCount(result.Manifest):N0} cortes.",
                    "OK");
            }
            catch (Exception exception)
            {
                _error = exception.Message;
            }
        }

        private static string ShortHash(string value)
            => string.IsNullOrEmpty(value) || value.Length <= 12 ? value : value.Substring(0, 12);

        private static int CapTriangleCount(SmartBodySplitManifest manifest)
        {
            int count = 0;
            for (int i = 0; i < manifest.Parts.Count; i++) count += manifest.Parts[i].CapTriangleCount;
            return count;
        }

        private static int CapLoopCount(SmartBodySplitManifest manifest)
        {
            int count = 0;
            for (int i = 0; i < manifest.Parts.Count; i++) count += manifest.Parts[i].CapLoopCount;
            return count;
        }

        private static Renderer[] CollectRenderers(GameObject target)
        {
            var values = new System.Collections.Generic.List<Renderer>();
            var skinned = target.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++) if (skinned[i] != null && skinned[i].sharedMesh != null) values.Add(skinned[i]);
            var filters = target.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var renderer = filters[i] != null ? filters[i].GetComponent<MeshRenderer>() : null;
                if (renderer != null && filters[i].sharedMesh != null) values.Add(renderer);
            }
            return values.ToArray();
        }
    }
}
