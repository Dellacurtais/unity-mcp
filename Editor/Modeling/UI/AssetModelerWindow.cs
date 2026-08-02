using System;
using System.Collections.Generic;
using System.Globalization;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Deform;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.SurfaceFit;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Transform;
using Maicon.SceneAnalyzer.Editor.Modeling.SceneTools;
using Maicon.SceneAnalyzer.Editor.Modeling.Validation;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.UI
{
    public sealed class AssetModelerWindow : EditorWindow
    {
        private enum WorkspaceTab { Session, Objects, Topology, Deform, SurfaceFit, SmartSplit }

        private GameObject _target;
        private MeshEditDocument _document;
        private Renderer _workingRenderer;
        private WorkspaceTab _tab;
        private Vector2 _scroll;
        private string _status;
        private MessageType _statusType = MessageType.Info;
        private MeshOperationPreview _preview;

        private Vector3 _translation;
        private Vector3 _rotation;
        private Vector3 _scale = Vector3.one;
        private int _axis;
        private bool _mirrorCopy = true;
        private float _weldTolerance = 0.00001f;
        private float _cleanupEpsilon = 1e-12f;
        private string _faceSelection = string.Empty;
        private float _extrudeDistance = 0.05f;
        private float _bevelInset = 0.2f;
        private float _bevelHeight;
        private float _maximumEdgeLength = 0.1f;
        private string _loopA = string.Empty;
        private string _loopB = string.Empty;
        private string _edgePairs = string.Empty;
        private string _holeLoop = string.Empty;
        private string _regionName = "Region_01";

        private MeshDeformMode _deformMode = MeshDeformMode.Grab;
        private float _deformStrength = 0.2f;
        private float _deformRadius = 1f;
        private int _deformIterations = 8;
        private Vector3 _deformDirection = Vector3.up;
        private string _vertexSelection = string.Empty;
        private string _pinnedSelection = string.Empty;
        private string _splinePoints = "0,0,0; 0,0.5,0.2; 0,1,0";

        private Renderer _fitTarget;
        private float _fitClearance = 0.005f;
        private float _fitMaxDistance = 1f;
        private float _fitStrength = 1f;
        private float _fitShapePreservation = 0.35f;
        private int _fitIterations = 8;
        private bool _fitAdaptiveRefine = true;
        private float _fitMaximumEdgeLength;
        private int _fitRefinementPasses = 2;

        private SceneArraySettings _array = new SceneArraySettings();
        private string _outputFolder = AssetModelerAssetPaths.DefaultRoot;
        private string _fbxPath = "Assets/AssetModeler/Exports/model.fbx";
        private bool _compactMergeMaterials = true;

        [MenuItem("Window/Asset Modeler (Unity MCP)", false, 1200)]
        public static void OpenWindow()
        {
            var window = GetWindow<AssetModelerWindow>();
            window.titleContent = new GUIContent("Asset Modeler");
            window.minSize = new Vector2(430f, 520f);
            window.Show();
        }

        [MenuItem("GameObject/Asset Modeler/Open in Asset Modeler", false, 47)]
        private static void OpenSelection()
        {
            OpenWindow();
            var window = GetWindow<AssetModelerWindow>();
            window.SetTarget(Selection.activeGameObject);
        }

        [MenuItem("GameObject/Asset Modeler/Open in Asset Modeler", true)]
        private static bool ValidateOpenSelection() => Selection.activeGameObject != null;

        private void OnEnable()
        {
            ModelingOperationCatalog.RegisterAll();
            Selection.selectionChanged += OnSelectionChanged;
            if (_target == null) SetTarget(Selection.activeGameObject);
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            ClearPreview();
        }

        private void OnSelectionChanged()
        {
            if (Selection.activeObject is MeshEditDocument document)
            {
                _document = document;
                _status = "Documento de edição selecionado.";
                Repaint();
                return;
            }
            if (Selection.activeGameObject != null) SetTarget(Selection.activeGameObject);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUI.BeginChangeCheck();
            var target = (GameObject)EditorGUILayout.ObjectField("Objeto", _target, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) SetTarget(target);
            _document = (MeshEditDocument)EditorGUILayout.ObjectField("Documento", _document, typeof(MeshEditDocument), false);
            _outputFolder = EditorGUILayout.TextField("Pasta de saída", _outputFolder);

            DrawNativeTools();
            _tab = (WorkspaceTab)GUILayout.Toolbar((int)_tab, new[] { "Sessão", "Objetos", "Topologia", "Deformar", "Vestir", "Split" });
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_tab)
            {
                case WorkspaceTab.Session: DrawSession(); break;
                case WorkspaceTab.Objects: DrawObjects(); break;
                case WorkspaceTab.Topology: DrawTopology(); break;
                case WorkspaceTab.Deform: DrawDeform(); break;
                case WorkspaceTab.SurfaceFit: DrawSurfaceFit(); break;
                case WorkspaceTab.SmartSplit: DrawSmartSplit(); break;
            }
            EditorGUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);
        }

        private void DrawNativeTools()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Toggle(Tools.current == Tool.Move, "Mover", EditorStyles.toolbarButton)) Tools.current = Tool.Move;
            if (GUILayout.Toggle(Tools.current == Tool.Rotate, "Rotação", EditorStyles.toolbarButton)) Tools.current = Tool.Rotate;
            if (GUILayout.Toggle(Tools.current == Tool.Scale, "Escala", EditorStyles.toolbarButton)) Tools.current = Tool.Scale;
            if (GUILayout.Toggle(Tools.current == Tool.Rect, "Bounds", EditorStyles.toolbarButton)) Tools.current = Tool.Rect;
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSession()
        {
            EditorGUILayout.LabelField("Documento editável", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_target == null))
                if (GUILayout.Button("Criar cópia editável", GUILayout.Height(28f))) CreateEditableCopy();
            if (_document == null) return;
            EditorGUILayout.LabelField("Sessão", _document.SessionId);
            EditorGUILayout.LabelField("Revisão", _document.Revision.ToString());
            EditorGUILayout.LabelField("Estado", _document.State.ToString());
            EditorGUILayout.LabelField("Mesh", _document.WorkingMesh != null ? _document.WorkingMesh.name : "Ausente");
            EditorGUILayout.LabelField("Source drift", _document.HasSourceDrift() ? "SIM" : "Não");
            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Validar")) ValidateDocument();
            if (GUILayout.Button("Commit")) Run(() => ModelingCommandService.Commit(_document), "Commit concluído.");
            if (GUILayout.Button("Rebase")) Run(() => { ModelingCommandService.Rebase(_document); return null; }, "Sessão rebaseada na fonte.");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Modifier Stack", EditorStyles.boldLabel);
            for (int i = 0; i < _document.ModifierStack.Count; i++)
            {
                var modifier = _document.ModifierStack[i];
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                bool enabled = EditorGUILayout.Toggle(modifier.Enabled, GUILayout.Width(18f));
                if (enabled != modifier.Enabled) Run(() => ModelingCommandService.SetModifierEnabled(_document, i, enabled, _document.Revision), "Modifier atualizada.");
                EditorGUILayout.LabelField(modifier.DisplayName);
                using (new EditorGUI.DisabledScope(i == 0)) if (GUILayout.Button("↑", GUILayout.Width(25f))) { Run(() => ModelingCommandService.ReorderModifier(_document, i, i - 1, _document.Revision), "Modifier reordenada."); GUIUtility.ExitGUI(); }
                using (new EditorGUI.DisabledScope(i == _document.ModifierStack.Count - 1)) if (GUILayout.Button("↓", GUILayout.Width(25f))) { Run(() => ModelingCommandService.ReorderModifier(_document, i, i + 1, _document.Revision), "Modifier reordenada."); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("×", GUILayout.Width(25f))) { Run(() => ModelingCommandService.RemoveModifier(_document, i, _document.Revision), "Modifier removida."); GUIUtility.ExitGUI(); }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawObjects()
        {
            EditorGUILayout.LabelField("Transformação precisa", EditorStyles.boldLabel);
            _translation = EditorGUILayout.Vector3Field("Mover", _translation);
            _rotation = EditorGUILayout.Vector3Field("Rotacionar", _rotation);
            _scale = EditorGUILayout.Vector3Field("Escala", _scale);
            DrawOperationButtons(() => new TransformVerticesOperation(Matrix4x4.TRS(_translation, Quaternion.Euler(_rotation), _scale)));

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Espelho", EditorStyles.boldLabel);
            _axis = GUILayout.Toolbar(_axis, new[] { "X", "Y", "Z" });
            _mirrorCopy = EditorGUILayout.Toggle("Criar cópia", _mirrorCopy);
            DrawOperationButtons(() => new MirrorMeshOperation(_axis, 0f, _mirrorCopy));

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Array de objetos", EditorStyles.boldLabel);
            _array.Mode = (SceneArrayMode)EditorGUILayout.EnumPopup("Modo", _array.Mode);
            _array.Count = Mathf.Max(1, EditorGUILayout.IntField("Quantidade", _array.Count));
            _array.Step = EditorGUILayout.Vector3Field("Passo/raio", _array.Step);
            _array.AttachedByBounds = EditorGUILayout.Toggle("Encostar pelos bounds", _array.AttachedByBounds);
            if (_array.Mode == SceneArrayMode.Grid)
            {
                _array.GridCount = EditorGUILayout.Vector3IntField("Contagem XYZ", _array.GridCount);
                _array.GridSpacing = EditorGUILayout.Vector3Field("Espaçamento XYZ", _array.GridSpacing);
            }
            if (_array.Mode == SceneArrayMode.Radial)
            {
                _array.RadialAxis = EditorGUILayout.Vector3Field("Eixo radial", _array.RadialAxis);
                _array.RadialAngle = EditorGUILayout.FloatField("Ângulo", _array.RadialAngle);
            }
            using (new EditorGUI.DisabledScope(_target == null))
                if (GUILayout.Button("Criar Array")) Run(() => SceneObjectOperations.CreateArray(_target, _array), "Array criado.");

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(Selection.gameObjects.Length == 0))
            {
                _compactMergeMaterials = EditorGUILayout.Toggle("Compactar materiais iguais", _compactMergeMaterials);
                if (GUILayout.Button("Merge das meshes estáticas selecionadas"))
                    Run(() => SceneObjectOperations.MergeStaticMeshes(Selection.gameObjects, _outputFolder, _compactMergeMaterials), "Meshes combinadas em um único asset.");
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Exportação FBX opcional", EditorStyles.boldLabel);
            _fbxPath = EditorGUILayout.TextField("Arquivo FBX", _fbxPath);
            using (new EditorGUI.DisabledScope(_target == null || !FbxExportAdapter.IsAvailable))
                if (GUILayout.Button(FbxExportAdapter.IsAvailable ? "Exportar FBX" : "Instale com.unity.formats.fbx"))
                    Run(() => FbxExportAdapter.Export(_target, _fbxPath), "FBX exportado.");
        }

        private void DrawTopology()
        {
            EditorGUILayout.LabelField("Limpeza segura", EditorStyles.boldLabel);
            _cleanupEpsilon = EditorGUILayout.FloatField("Área degenerada", _cleanupEpsilon);
            DrawOperationButtons(() => new CleanupMeshOperation(_cleanupEpsilon, true), "Cleanup");
            _weldTolerance = EditorGUILayout.FloatField("Weld tolerance", _weldTolerance);
            DrawOperationButtons(() => new WeldVerticesOperation(_weldTolerance, true), "Weld preservando seams");

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Faces", EditorStyles.boldLabel);
            _faceSelection = EditorGUILayout.TextField(new GUIContent("IDs de faces", "Ex.: 0,1,4-8. Vazio seleciona todas apenas nas operações permitidas."), _faceSelection);
            EditorGUILayout.BeginHorizontal();
            _regionName = EditorGUILayout.TextField("Região", _regionName);
            if (GUILayout.Button("Salvar", GUILayout.Width(60f))) SaveFaceRegion();
            if (GUILayout.Button("Usar", GUILayout.Width(50f))) UseRegion();
            EditorGUILayout.EndHorizontal();
            DrawOperationButtons(() => new SubdivideTrianglesOperation(ParseIndices(_faceSelection)), "Subdividir");
            _extrudeDistance = EditorGUILayout.FloatField("Extrude", _extrudeDistance);
            DrawOperationButtons(() => new ExtrudeFacesOperation(ParseIndices(_faceSelection), _extrudeDistance), "Extrude");
            _bevelInset = EditorGUILayout.Slider("Inset", _bevelInset, 0f, 0.95f);
            _bevelHeight = EditorGUILayout.FloatField("Altura do bevel", _bevelHeight);
            DrawOperationButtons(() => new BevelFacesOperation(ParseIndices(_faceSelection), _bevelInset, _bevelHeight), "Inset / Bevel");
            _maximumEdgeLength = EditorGUILayout.FloatField("Comprimento máximo", _maximumEdgeLength);
            DrawOperationButtons(() => new AdaptiveRefineOperation(_maximumEdgeLength), "Refino adaptativo");

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Loops de vértices", EditorStyles.boldLabel);
            _edgePairs = EditorGUILayout.TextField(new GUIContent("Arestas (pares)", "Ex.: 0,1, 1,2 para extrudar duas arestas de borda."), _edgePairs);
            DrawOperationButtons(() => new ExtrudeBoundaryEdgesOperation(ParseIndicesPreserveOrder(_edgePairs), _extrudeDistance), "Extrude arestas");
            _loopA = EditorGUILayout.TextField("Loop A", _loopA);
            _loopB = EditorGUILayout.TextField("Loop B", _loopB);
            DrawOperationButtons(() => new BridgeLoopsOperation(ParseIndices(_loopA), ParseIndices(_loopB)), "Bridge");
            _holeLoop = EditorGUILayout.TextField("Contorno do buraco", _holeLoop);
            DrawOperationButtons(() => new FillHoleOperation(ParseIndices(_holeLoop)), "Fechar buraco");
        }

        private void DrawDeform()
        {
            EditorGUILayout.LabelField("Deformação preservando canais", EditorStyles.boldLabel);
            _deformMode = (MeshDeformMode)EditorGUILayout.EnumPopup("Modo", _deformMode);
            _axis = GUILayout.Toolbar(_axis, new[] { "X", "Y", "Z" });
            _deformStrength = EditorGUILayout.FloatField("Força/ângulo", _deformStrength);
            _deformRadius = EditorGUILayout.FloatField("Raio soft selection", _deformRadius);
            _deformIterations = EditorGUILayout.IntSlider("Iterações", _deformIterations, 1, 100);
            _deformDirection = EditorGUILayout.Vector3Field("Direção/handle", _deformDirection);
            _vertexSelection = EditorGUILayout.TextField("Vértices selecionados", _vertexSelection);
            _pinnedSelection = EditorGUILayout.TextField("Vértices fixos", _pinnedSelection);
            if (_deformMode == MeshDeformMode.Spline)
                _splinePoints = EditorGUILayout.TextField(new GUIContent("Pontos da curva", "XYZ separados por vírgula; pontos separados por ponto e vírgula."), _splinePoints);
            DrawOperationButtons(() => new DeformMeshOperation(new MeshDeformParameters
            {
                mode = _deformMode,
                axis = _axis,
                strength = _deformStrength,
                radius = _deformRadius,
                iterations = _deformIterations,
                direction = new[] { _deformDirection.x, _deformDirection.y, _deformDirection.z },
                selectedVertices = ParseIndices(_vertexSelection),
                pinnedVertices = ParseIndices(_pinnedSelection),
                splinePoints = ParseVectorList(_splinePoints),
            }), "Deformar");
        }

        private void DrawSurfaceFit()
        {
            EditorGUILayout.LabelField("Smart Surface Fit", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Selecione a roupa como objeto/documento editável e informe o corpo alvo. O preview usa BVH, clearance e preservação Laplacian.", MessageType.Info);
            _fitTarget = (Renderer)EditorGUILayout.ObjectField("Corpo alvo", _fitTarget, typeof(Renderer), true);
            _fitClearance = EditorGUILayout.FloatField("Distância da pele", _fitClearance);
            _fitMaxDistance = EditorGUILayout.FloatField("Distância máxima", _fitMaxDistance);
            _fitStrength = EditorGUILayout.Slider("Atração", _fitStrength, 0f, 1f);
            _fitShapePreservation = EditorGUILayout.Slider("Preservar forma", _fitShapePreservation, 0f, 1f);
            _fitIterations = EditorGUILayout.IntSlider("Iterações", _fitIterations, 1, 100);
            _fitAdaptiveRefine = EditorGUILayout.Toggle("Refinar malha automaticamente", _fitAdaptiveRefine);
            if (_fitAdaptiveRefine)
            {
                _fitMaximumEdgeLength = EditorGUILayout.FloatField(new GUIContent("Aresta máxima", "Zero calcula automaticamente a partir do clearance e bounds."), _fitMaximumEdgeLength);
                _fitRefinementPasses = EditorGUILayout.IntSlider("Passes de refino", _fitRefinementPasses, 1, 4);
            }
            DrawOperationButtons(CreateSurfaceFitOperation, "Ajustar roupa");
        }

        private void DrawSmartSplit()
        {
            EditorGUILayout.LabelField("Smart Body Split", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Presets Core, mãos/pés, modular, FPS, dano e roupas; inclui correção manual de fronteiras, Prefab modular e recombinação.", MessageType.Info);
            using (new EditorGUI.DisabledScope(_target == null))
                if (GUILayout.Button("Abrir Smart Body Split", GUILayout.Height(32f))) SmartBodySplitWindow.Open(_target);
        }

        private SmartSurfaceFitOperation CreateSurfaceFitOperation()
        {
            if (_fitTarget == null) throw new InvalidOperationException("Selecione o corpo alvo.");
            if (_workingRenderer == null) ResolveWorkingRenderer();
            if (_workingRenderer == null) throw new InvalidOperationException("O objeto editável não possui renderer associado.");
            var parameters = SmartSurfaceFitOperation.CreateParameters(
                _workingRenderer,
                new[] { _fitTarget },
                _fitClearance,
                _fitMaxDistance);
            parameters.strength = _fitStrength;
            parameters.shapePreservation = _fitShapePreservation;
            parameters.iterations = _fitIterations;
            parameters.adaptiveRefine = _fitAdaptiveRefine;
            parameters.maximumEdgeLength = _fitMaximumEdgeLength;
            parameters.refinementPasses = _fitRefinementPasses;
            return new SmartSurfaceFitOperation(parameters);
        }

        private void DrawOperationButtons(Func<IMeshOperation> factory, string label = null)
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_document == null))
            {
                if (GUILayout.Button("Preview" + (string.IsNullOrEmpty(label) ? string.Empty : " " + label)))
                {
                    try { PreviewOperation(factory()); }
                    catch (Exception exception) { Failure(exception); }
                }
                using (new EditorGUI.DisabledScope(_preview == null))
                    if (GUILayout.Button("Aplicar"))
                    {
                        try { ApplyOperation(factory()); }
                        catch (Exception exception) { Failure(exception); }
                    }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void PreviewOperation(IMeshOperation operation)
        {
            RequireDocument();
            ClearPreview();
            _preview = ModelingCommandService.Preview(_document, operation, _document.Revision);
            ResolveWorkingRenderer();
            MeshOperationPreviewController.Show(_preview, _workingRenderer);
            _status = $"Preview pronto: {operation.Descriptor.DisplayName}; hash {_preview.PreviewHash.Substring(0, 12)}.";
            _statusType = MessageType.Info;
        }

        private void ApplyOperation(IMeshOperation operation)
        {
            RequireDocument();
            if (_preview == null) throw new InvalidOperationException("Gere e revise o preview antes de aplicar.");
            var result = ModelingCommandService.Apply(_document, operation, _document.Revision, _preview.PreviewHash);
            ClearPreview();
            _status = $"{operation.Descriptor.DisplayName} aplicado. Revisão {result.RevisionAfter}.";
            _statusType = MessageType.Info;
        }

        private void CreateEditableCopy()
        {
            Run(() =>
            {
                var result = EditableMeshFactory.Create(_target, _outputFolder + "/Editable/" + AssetModelerAssetPaths.SafeName(_target.name));
                _target = result.EditableObject;
                _workingRenderer = result.EditableRenderer;
                _document = result.Document;
                Selection.activeGameObject = result.EditableObject;
                return result;
            }, "Cópia editável criada; o asset importado continua protegido.");
        }

        private void ValidateDocument()
        {
            RequireDocument();
            var report = MeshValidator.Validate(_document.WorkingMesh);
            _status = report.IsValid ? "Mesh válida." : string.Join(" | ", Issues(report));
            _statusType = report.IsValid ? MessageType.Info : MessageType.Error;
        }

        private void Rebuild()
        {
            try
            {
                var report = ModelingCommandService.Rebuild(_document);
                _status = report.IsValid ? "Modifier Stack reconstruída." : "Reconstrução produziu erros.";
                _statusType = report.IsValid ? MessageType.Info : MessageType.Error;
            }
            catch (Exception exception) { Failure(exception); }
        }

        private void SaveFaceRegion()
        {
            try
            {
                RequireDocument();
                Undo.RecordObject(_document, "Save mesh region");
                _document.Selection.SetSelection(MeshElementKind.Face, ParseIndices(_faceSelection), _document.Revision.Topology);
                _document.Selection.SaveRegion(_regionName);
                EditorUtility.SetDirty(_document);
                _status = $"Região '{_regionName}' salva.";
                _statusType = MessageType.Info;
            }
            catch (Exception exception) { Failure(exception); }
        }

        private void UseRegion()
        {
            try
            {
                RequireDocument();
                if (!_document.Selection.SelectRegion(_regionName, _document.Revision.Topology))
                    throw new InvalidOperationException($"A região '{_regionName}' não existe.");
                _faceSelection = string.Join(",", _document.Selection.Indices);
                _status = $"Região '{_regionName}' carregada.";
                _statusType = MessageType.Info;
            }
            catch (Exception exception) { Failure(exception); }
        }

        private void SetTarget(GameObject target)
        {
            ClearPreview();
            _target = target;
            _workingRenderer = null;
            ResolveWorkingRenderer();
            Repaint();
        }

        private void ResolveWorkingRenderer()
        {
            if (_target != null && EditableMeshFactory.TryResolveRenderer(_target, out var renderer, out _, out _))
                _workingRenderer = renderer;
        }

        private void ClearPreview()
        {
            if (_preview != null)
            {
                MeshOperationPreviewController.Detach();
                _preview.Dispose();
                _preview = null;
            }
        }

        private void RequireDocument()
        {
            if (_document == null || _document.WorkingMesh == null)
                throw new InvalidOperationException("Crie ou selecione um MeshEditDocument primeiro.");
        }

        private void Run(Func<object> action, string success)
        {
            try { action(); _status = success; _statusType = MessageType.Info; }
            catch (Exception exception) { Failure(exception); }
        }

        private void Failure(Exception exception)
        {
            _status = exception?.Message ?? "Erro desconhecido.";
            _statusType = MessageType.Error;
        }

        private static IEnumerable<string> Issues(MeshValidationReport report)
        {
            for (int i = 0; i < report.Issues.Count; i++) yield return report.Issues[i].Code + ": " + report.Issues[i].Message;
        }

        private static int[] ParseIndices(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int>();
            var result = new SortedSet<int>();
            var tokens = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
            {
                var range = tokens[tokenIndex].Split('-');
                if (range.Length == 1)
                {
                    if (!int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
                        throw new FormatException($"Índice inválido: '{tokens[tokenIndex]}'.");
                    result.Add(index);
                    continue;
                }
                if (range.Length != 2
                    || !int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
                    || !int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
                    || start < 0 || end < start || end - start > 1000000)
                    throw new FormatException($"Intervalo inválido: '{tokens[tokenIndex]}'.");
                for (int index = start; index <= end; index++) result.Add(index);
            }
            return new List<int>(result).ToArray();
        }

        private static float[] ParseVectorList(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<float>();
            var result = new List<float>();
            var points = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int point = 0; point < points.Length; point++)
            {
                var components = points[point].Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (components.Length != 3) throw new FormatException($"Ponto de curva inválido: '{points[point]}'.");
                for (int component = 0; component < 3; component++)
                    if (float.TryParse(components[component], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) result.Add(parsed);
                    else throw new FormatException($"Coordenada inválida: '{components[component]}'.");
            }
            return result.ToArray();
        }

        private static int[] ParseIndicesPreserveOrder(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int>();
            var result = new List<int>();
            var tokens = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
                    throw new FormatException($"Índice inválido: '{tokens[i]}'.");
                result.Add(index);
            }
            return result.ToArray();
        }
    }
}
