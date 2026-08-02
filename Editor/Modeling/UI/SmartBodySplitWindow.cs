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
        };

        private GameObject _target;
        private SmartBodySplitProposal _proposal;
        private SmartBodySplitPreset _preset = SmartBodySplitPreset.Core;
        private string _outputFolder = string.Empty;
        private Vector2 _scroll;
        private string _error;

        [MenuItem("Window/Asset Modeler (Unity MCP)")]
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
            window.SetTarget(target);
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

            EditorGUI.BeginChangeCheck();
            _preset = (SmartBodySplitPreset)EditorGUILayout.Popup("Preset", (int)_preset, PresetLabels);
            if (EditorGUI.EndChangeCheck() && _target != null) Analyze();

            _outputFolder = EditorGUILayout.TextField(
                new GUIContent("Pasta de saída", "Vazio usa Assets/AssetModeler/SmartSplits/<personagem>."),
                _outputFolder);

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
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(_proposal == null))
            {
                if (GUILayout.Button("Criar partes", GUILayout.Height(34f))) Apply();
            }
        }

        private void SetTarget(GameObject target)
        {
            _target = target;
            _proposal = null;
            _error = null;
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
                _proposal = SmartBodySplitAnalyzer.Analyze(_target, _preset);
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
                    _proposal.PreviewHash);
                Selection.activeGameObject = result.PartsRoot;
                EditorGUIUtility.PingObject(result.BodyPartSet);
                EditorUtility.DisplayDialog(
                    "Smart Body Split",
                    $"{result.Manifest.Parts.Count} partes criadas e validadas em {result.OutputFolder}.",
                    "OK");
            }
            catch (Exception exception)
            {
                _error = exception.Message;
            }
        }

        private static string ShortHash(string value)
            => string.IsNullOrEmpty(value) || value.Length <= 12 ? value : value.Substring(0, 12);
    }
}
