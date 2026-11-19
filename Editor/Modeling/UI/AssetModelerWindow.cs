using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        [SerializeField] private GameObject _target;
        [SerializeField] private GameObject _editableRoot;
        [SerializeField] private Renderer _selectedRenderer;
        [SerializeField] private MeshEditDocument _document;
        [SerializeField] private Renderer _workingRenderer;
        private readonly Dictionary<Renderer, MeshEditDocument> _rendererDocuments = new Dictionary<Renderer, MeshEditDocument>();
        [SerializeField] private AssetModelerNavigationState _navigation = new AssetModelerNavigationState();
        [SerializeField] private AssetModelerObjectTool _objectTool;
        [SerializeField] private AssetModelerTopologyTool _topologyTool;
        [SerializeField] private bool _sceneFaceSelection;
        [SerializeField] private bool _showContextDetails;
        [SerializeField] private bool _showAdvancedSelection;
        [SerializeField] private bool _showAdvancedIds;
        [SerializeField] private bool _showAdvancedTools;
        private GUIStyle _pageTitleStyle;
        private GUIStyle PageTitleStyle => _pageTitleStyle ?? (_pageTitleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 });
        private Vector2 _scroll;
        private string _status;
        private MessageType _statusType = MessageType.Info;
        private MeshOperationPreview _preview;

        private int _axis;
        private bool _mirrorCopy = true;
        private float _mirrorPlane;
        private bool _mirrorWeldSeam;
        private float _weldTolerance = 0.00001f;
        private float _cleanupEpsilon;
        private bool _cleanupRemoveUnused = true;
        private bool _cleanupRemoveLines;
        private bool _cleanupRemovePoints;
        private readonly HashSet<int> _cleanupComponents = new HashSet<int>();
        private int _cleanupPage;
        private MeshCleanupAnalysis _cleanupAnalysis;
        private MeshCleanupAnalysis _cleanupAfter;
        private string _cleanupAnalysisHash;
        private ModelScaleMode _sizeMode = ModelScaleMode.TargetDimension;
        private ModelScaleAxis _sizeAxis = ModelScaleAxis.Y;
        private float _targetDimension = 1.8f;
        private ModelScalePreview _sizePreview;
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
        private string _mergeName = "MergedMesh";
        private bool _mergeRemoveSources;
        private string _lastExportPath;
        [SerializeField] private string _savedPrefabPath;

        [MenuItem("Window/Asset Modeler (Unity MCP)", false, 1200)]
        public static void OpenWindow()
        {
            OpenWorkspace(AssetModelerWorkspace.Home, Selection.activeGameObject);
        }

        [MenuItem("Window/Asset Modeler/Home", false, 1201)]
        private static void OpenHome() => OpenWorkspace(AssetModelerWorkspace.Home, Selection.activeGameObject);

        [MenuItem("Window/Asset Modeler/Object Tools", false, 1202)]
        private static void OpenObjectTools() => OpenWorkspace(AssetModelerWorkspace.Objects, Selection.activeGameObject);

        [MenuItem("Window/Asset Modeler/Mesh Topology", false, 1203)]
        private static void OpenTopologyTools() => OpenWorkspace(AssetModelerWorkspace.Topology, Selection.activeGameObject);

        [MenuItem("Window/Asset Modeler/Deform", false, 1204)]
        private static void OpenDeformTools() => OpenWorkspace(AssetModelerWorkspace.Deform, Selection.activeGameObject);

        [MenuItem("Window/Asset Modeler/Surface Fit", false, 1205)]
        private static void OpenSurfaceFitTools() => OpenWorkspace(AssetModelerWorkspace.SurfaceFit, Selection.activeGameObject);

        internal static AssetModelerWindow OpenWorkspace(AssetModelerWorkspace workspace, GameObject target)
        {
            var window = GetWindow<AssetModelerWindow>();
            window.titleContent = new GUIContent("Asset Modeler");
            window.minSize = new Vector2(390f, 480f);
            window._navigation = window._navigation ?? new AssetModelerNavigationState();
            window._navigation.Open(workspace);
            if (target != null) window.SetTarget(target);
            window.Show();
            window.Repaint();
            AssetModelerSceneController.Attach(window);
            return window;
        }

        [MenuItem("GameObject/Asset Modeler/Open in Asset Modeler", false, 47)]
        private static void OpenSelection()
        {
            OpenWorkspace(AssetModelerWorkspace.Home, Selection.activeGameObject);
        }

        [MenuItem("GameObject/Asset Modeler/Open in Asset Modeler", true)]
        private static bool ValidateOpenSelection() => Selection.activeGameObject != null;

        private void OnEnable()
        {
            ModelingOperationCatalog.RegisterAll();
            _navigation = _navigation ?? new AssetModelerNavigationState();
            _objectTool = AssetModelerWorkspaceCatalog.VisibleObjectTool(_objectTool);
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            if (_target == null) SetTarget(Selection.activeGameObject);
            if (_selectedRenderer != null && _document != null && RendererMesh(_selectedRenderer) == _document.WorkingMesh)
                _rendererDocuments[_selectedRenderer] = _document;
            AssetModelerSceneController.Attach(this);
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            AssetModelerSceneController.Detach(this);
            ClearPreview();
        }

        private void OnSelectionChanged()
        {
            // Selection is also used for picking faces and inspecting the rig. Changing
            // it must never silently replace the model whose preview was approved.
            Repaint();
        }

        private void OnUndoRedo()
        {
            ClearPreview();
            _cleanupAnalysis = null;
            _cleanupComponents.Clear();
            Repaint();
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawContext();
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;
                if (_navigation.IsHome) DrawHome();
                else
                {
                    DrawWorkspaceHeader();
                    switch (_navigation.Workspace)
                    {
                        case AssetModelerWorkspace.Session: DrawSession(); break;
                        case AssetModelerWorkspace.Objects: DrawObjects(); break;
                        case AssetModelerWorkspace.Topology: DrawTopology(); break;
                        case AssetModelerWorkspace.Deform: DrawDeform(); break;
                        case AssetModelerWorkspace.SurfaceFit: DrawSurfaceFit(); break;
                        case AssetModelerWorkspace.Cleanup: DrawCleanup(); break;
                        case AssetModelerWorkspace.Size: DrawSize(); break;
                        case AssetModelerWorkspace.Save: DrawSave(); break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(EditorGUIUtility.IconContent("EditCollider"), GUILayout.Width(24f));
            GUILayout.Label("Asset Modeler", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (!_navigation.IsHome && GUILayout.Button("Início", EditorStyles.toolbarButton, GUILayout.Width(58f)))
            {
                _navigation.Back();
                _sceneFaceSelection = false;
                ClearPreview();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawContext()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var icon = _target != null ? EditorGUIUtility.IconContent("GameObject Icon") : EditorGUIUtility.IconContent("console.warnicon");
                    GUILayout.Label(icon, GUILayout.Width(22f), GUILayout.Height(20f));
                    EditorGUI.BeginChangeCheck();
                    var target = (GameObject)EditorGUILayout.ObjectField("Raiz do modelo", _target, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck()) SetTarget(target);
                    if (_target != null && GUILayout.Button("Focar", GUILayout.Width(52f))) FrameTarget();
                }
                using (new EditorGUI.DisabledScope(Selection.activeGameObject == null || Selection.activeGameObject == _target))
                    if (GUILayout.Button("Usar seleção como raiz")) SetTarget(Selection.activeGameObject);
                DrawRendererPicker();
                EditorGUILayout.LabelField(_editableRoot == _target && _target != null ? "Trabalhando em uma cópia" : "Modelo de origem", EditorStyles.miniLabel);
                _showContextDetails = EditorGUILayout.Foldout(_showContextDetails, "Avançado: documento e revisão", true);
                if (_showContextDetails)
                {
                    var document = (MeshEditDocument)EditorGUILayout.ObjectField("Documento", _document, typeof(MeshEditDocument), false);
                    if (document != _document)
                    {
                        ClearPreview();
                        if (document == null || RendererMesh(_selectedRenderer) == document.WorkingMesh)
                        {
                            _document = document;
                            _workingRenderer = document != null ? _selectedRenderer : null;
                            if (_selectedRenderer != null) _rendererDocuments[_selectedRenderer] = document;
                        }
                        else Failure(new InvalidOperationException("O documento deve pertencer à malha escolhida."));
                    }
                    if (_document != null)
                    {
                        EditorGUILayout.LabelField("Sessão", _document.SessionId);
                        EditorGUILayout.LabelField("Revisão", _document.Revision.ToString());
                    }
                }
            }
        }

        private void DrawHome()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Preparar seu modelo", PageTitleStyle);
            EditorGUILayout.LabelField(AssetModelerWorkspaceCatalog.Description(AssetModelerWorkspace.Home), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6f);
            DrawWorkspaceCard(AssetModelerWorkspace.Cleanup, "d_PreMatCube");
            DrawWorkspaceCard(AssetModelerWorkspace.Size, "ScaleTool");
            DrawWorkspaceCard(AssetModelerWorkspace.Save, "d_Prefab Icon");
            _showAdvancedTools = EditorGUILayout.Foldout(_showAdvancedTools, "Modelagem avançada", true);
            if (!_showAdvancedTools) return;
            DrawWorkspaceCard(AssetModelerWorkspace.Objects, "EditCollider");
            DrawWorkspaceCard(AssetModelerWorkspace.Topology, "ProBuilderMesh");
            DrawWorkspaceCard(AssetModelerWorkspace.Deform, "ClothInspector.PaintValue");
            DrawWorkspaceCard(AssetModelerWorkspace.SurfaceFit, "AvatarMask Icon");
            DrawWorkspaceCard(AssetModelerWorkspace.Session, "d_UnityEditor.InspectorWindow");
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Smart Body Split", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Separar personagens preservando rig, pesos e materiais.", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(_target == null))
                if (GUILayout.Button("Abrir Smart Body Split", GUILayout.Height(28f))) SmartBodySplitWindow.Open(_target);
            EditorGUILayout.EndVertical();
        }

        private void DrawWorkspaceCard(AssetModelerWorkspace workspace, string iconName)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label(EditorGUIUtility.IconContent(iconName), GUILayout.Width(34f), GUILayout.Height(34f));
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(AssetModelerWorkspaceCatalog.Label(workspace), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(AssetModelerWorkspaceCatalog.Description(workspace), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
            if (GUILayout.Button("Abrir", GUILayout.Width(58f), GUILayout.Height(34f))) OpenWorkspacePage(workspace);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawWorkspaceHeader()
        {
            EditorGUILayout.Space(5f);
            EditorGUILayout.LabelField(AssetModelerWorkspaceCatalog.Label(_navigation.Workspace), PageTitleStyle);
            EditorGUILayout.LabelField(AssetModelerWorkspaceCatalog.Description(_navigation.Workspace), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(5f);
        }

        private void DrawRendererPicker()
        {
            if (_target == null) return;
            var renderers = _target.GetComponentsInChildren<Renderer>(true).Where(r => RendererMesh(r) != null).ToArray();
            if (_selectedRenderer != null && !renderers.Contains(_selectedRenderer)) SetRenderer(null);
            var labels = new[] { "Escolha a malha para limpar / modelar" }
                .Concat(renderers.Select(r => AnimationUtility.CalculateTransformPath(r.transform, _target.transform) + "  (" + RendererMesh(r).name + ")")).ToArray();
            int current = Array.IndexOf(renderers, _selectedRenderer) + 1;
            int chosen = EditorGUILayout.Popup("Malha para edição", current, labels);
            if (chosen != current) SetRenderer(chosen == 0 ? null : renderers[chosen - 1]);
            if (renderers.Length > 1)
                EditorGUILayout.LabelField($"{renderers.Length} malhas. O tamanho usa a raiz inteira; a limpeza usa a malha escolhida.", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawCreateCopy()
        {
            EditorGUILayout.HelpBox("Escolha a malha e crie uma cópia para editar. O modelo importado permanece protegido.", MessageType.Info);
            using (new EditorGUI.DisabledScope(_selectedRenderer == null))
                if (GUILayout.Button(CanSaveCopy ? "Preparar esta malha para edição" : "Criar cópia editável", GUILayout.Height(30f))) CreateEditableCopy();
        }

        private void DrawCleanup()
        {
            if (_document == null) { DrawCreateCopy(); return; }
            EditorGUI.BeginChangeCheck();
            _cleanupRemoveUnused = EditorGUILayout.ToggleLeft("Remover vértices sem uso", _cleanupRemoveUnused);
            _cleanupRemoveLines = EditorGUILayout.ToggleLeft("Remover linhas soltas", _cleanupRemoveLines);
            _cleanupRemovePoints = EditorGUILayout.ToggleLeft("Remover pontos soltos", _cleanupRemovePoints);
            _cleanupEpsilon = EditorGUILayout.FloatField(new GUIContent("Área mínima das faces", "Área em unidades locais². Zero remove apenas faces sem área."), _cleanupEpsilon);
            if (EditorGUI.EndChangeCheck()) { ClearPreview(); _cleanupAnalysis = null; _cleanupComponents.Clear(); }
            if (_cleanupEpsilon > 0f)
                EditorGUILayout.HelpBox("Um limite maior que zero também pode remover superfícies finas válidas. Use zero para a limpeza conservadora.", MessageType.Warning);
            if (GUILayout.Button("Analisar geometria")) AnalyzeCleanup();
            if (_cleanupAnalysis != null)
            {
                EditorGUILayout.LabelField($"Faces: {_cleanupAnalysis.FaceCount}  •  Vértices: {_cleanupAnalysis.VertexCount}");
                EditorGUILayout.LabelField($"Faces sem área / abaixo do limite: {_cleanupAnalysis.DegenerateFaceCount}");
                EditorGUILayout.LabelField($"Vértices sem uso: {_cleanupAnalysis.UnreferencedVertexCount}  •  Linhas soltas: {_cleanupAnalysis.LooseLineCount}  •  Pontos soltos: {_cleanupAnalysis.LoosePointCount}", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Fragmentos: marque somente os que deseja remover", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Partes desconectadas podem ser intencionais, inclusive costuras de UV. Nenhum fragmento é removido automaticamente.", MessageType.Info);
                const int pageSize = 20;
                int pages = Math.Max(1, (_cleanupAnalysis.Components.Count + pageSize - 1) / pageSize);
                _cleanupPage = Mathf.Clamp(_cleanupPage, 0, pages - 1);
                if (pages > 1)
                {
                    EditorGUILayout.BeginHorizontal();
                    using (new EditorGUI.DisabledScope(_cleanupPage == 0)) if (GUILayout.Button("Anterior")) _cleanupPage--;
                    EditorGUILayout.LabelField($"Página {_cleanupPage + 1} de {pages}");
                    using (new EditorGUI.DisabledScope(_cleanupPage == pages - 1)) if (GUILayout.Button("Próxima")) _cleanupPage++;
                    EditorGUILayout.EndHorizontal();
                }
                foreach (var component in _cleanupAnalysis.Components.Skip(_cleanupPage * pageSize).Take(pageSize))
                {
                    EditorGUILayout.BeginHorizontal();
                    bool selected = _cleanupComponents.Contains(component.Id);
                    bool remove = EditorGUILayout.ToggleLeft($"Parte {component.Id + 1}: {component.FaceCount} faces, {component.VertexCount} vértices", selected);
                    if (remove != selected)
                    {
                        if (remove) _cleanupComponents.Add(component.Id); else _cleanupComponents.Remove(component.Id);
                        ClearPreview();
                    }
                    if (GUILayout.Button("Focar", GUILayout.Width(48f))) FrameComponent(component);
                    EditorGUILayout.EndHorizontal();
                }
            }
            DrawOperationButtons(CreateCleanupOperation, "Limpeza");
            if (_cleanupAfter != null)
                EditorGUILayout.HelpBox($"Prévia: {_cleanupAnalysis?.FaceCount} → {_cleanupAfter.FaceCount} faces; {_cleanupAnalysis?.VertexCount} → {_cleanupAfter.VertexCount} vértices. Remoções: {_preview.Result.RemovedLineCount} linha(s), {_preview.Result.RemovedPointCount} ponto(s), {_preview.Result.RemovedComponentCount} fragmento(s).", MessageType.Info);
        }

        private void AnalyzeCleanup()
        {
            try
            {
                RequireDocument();
                ClearPreview();
                _cleanupComponents.Clear();
                _cleanupAnalysis = MeshCleanupAnalysis.Analyze(_document.WorkingMesh, _cleanupEpsilon);
                _cleanupAnalysisHash = MeshFingerprint.Compute(_document.WorkingMesh);
            }
            catch (Exception exception) { _cleanupAnalysis = null; Failure(exception); }
        }

        private IMeshOperation CreateCleanupOperation()
        {
            RequireDocument();
            string currentHash = MeshFingerprint.Compute(_document.WorkingMesh);
            if (_cleanupComponents.Count > 0 && _cleanupAnalysisHash != currentHash)
                throw new InvalidOperationException("A malha mudou. Analise novamente e revise os fragmentos antes de continuar.");
            if (_cleanupAnalysis == null || _cleanupAnalysisHash != currentHash)
            {
                _cleanupAnalysis = MeshCleanupAnalysis.Analyze(_document.WorkingMesh, _cleanupEpsilon);
                _cleanupAnalysisHash = currentHash;
            }
            return new CleanupMeshOperation(_cleanupEpsilon, _cleanupRemoveUnused, _cleanupRemoveLines, _cleanupRemovePoints, _cleanupComponents.OrderBy(id => id).ToArray());
        }

        private void FrameComponent(MeshCleanupComponent component)
        {
            if (_workingRenderer == null) return;
            var matrix = _workingRenderer.localToWorldMatrix;
            var bounds = component.Bounds;
            var world = new Bounds(matrix.MultiplyPoint3x4(bounds.center), Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        world.Encapsulate(matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z))));
            SceneView.lastActiveSceneView?.Frame(world, false);
        }

        private ModelScaleSettings SizeSettings() => new ModelScaleSettings
        {
            Mode = _sizeMode, Axis = _sizeAxis, TargetDimension = _targetDimension,
            OutputFolder = _outputFolder + "/Sized",
        };

        private void DrawSize()
        {
            EditorGUILayout.HelpBox("A raiz escolhida e todos os seus filhos serão copiados. As dimensões seguem os eixos da raiz e não incluem a escala do pai.", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            _sizeMode = (ModelScaleMode)GUILayout.Toolbar((int)_sizeMode, new[] { "Manter tamanho atual", "Definir dimensão" });
            if (_sizeMode == ModelScaleMode.TargetDimension)
            {
                _sizeAxis = (ModelScaleAxis)GUILayout.Toolbar((int)_sizeAxis, new[] { "Largura (X)", "Altura (Y)", "Profundidade (Z)" });
                _targetDimension = EditorGUILayout.FloatField(new GUIContent("Dimensão desejada", "Medida em unidades Unity, nos eixos da raiz. Por exemplo: altura 1,80 para um personagem de 1,80 m na convenção de 1 unidade = 1 metro."), _targetDimension);
            }
            _outputFolder = EditorGUILayout.TextField("Pasta de saída", _outputFolder);
            if (EditorGUI.EndChangeCheck()) _sizePreview = null;
            using (new EditorGUI.DisabledScope(_target == null))
                if (GUILayout.Button("Gerar prévia do tamanho", GUILayout.Height(30f))) PreviewSize();
            if (_sizePreview == null) return;
            EditorGUILayout.LabelField("Dimensões antes", FormatDimensions(_sizePreview.BoundsBefore.size));
            EditorGUILayout.LabelField("Dimensões depois", FormatDimensions(_sizePreview.BoundsAfter.size));
            EditorGUILayout.LabelField("Escala da raiz depois", "1, 1, 1");
            EditorGUILayout.LabelField("Malhas incluídas", _sizePreview.RendererCount.ToString());
            foreach (var warning in _sizePreview.Warnings) EditorGUILayout.HelpBox(SizeWarning(warning), MessageType.Warning);
            if (GUILayout.Button("Aplicar tamanho e criar cópia", GUILayout.Height(30f))) ApplySize();
        }

        private static string FormatDimensions(Vector3 value) => $"X {value.x:0.###}   Y {value.y:0.###}   Z {value.z:0.###}";

        private static string SizeWarning(string warning)
        {
            if (warning.StartsWith("Dimensions", StringComparison.Ordinal)) return "As dimensões usam a geometria na pose base, nos eixos do modelo. A animação pode ultrapassar esses limites.";
            if (warning.StartsWith("Parent scale", StringComparison.Ordinal)) return "A escala do pai é preservada. Escala local (1, 1, 1) na raiz não significa escala global (1, 1, 1).";
            if (warning.StartsWith("Undo", StringComparison.Ordinal)) return "Desfazer remove a cópia da cena. Os arquivos gerados e o prefab permanecem salvos para preservar referências.";
            if (warning.StartsWith("Custom scripts", StringComparison.Ordinal)) return "Scripts e configurações são copiados. Distâncias de gameplay e outros campos personalizados não são redimensionados.";
            return warning;
        }

        internal void PreviewSize()
        {
            try { ClearPreview(); _sizePreview = ModelScaleService.Preview(_target, SizeSettings()); _status = "Prévia pronta. Revise as dimensões antes de criar a cópia."; _statusType = MessageType.Info; }
            catch (Exception exception) { _sizePreview = null; Failure(exception); }
        }

        private void ApplySize()
        {
            Run(() =>
            {
                if (_sizePreview == null) throw new InvalidOperationException("Gere a prévia do tamanho primeiro.");
                var result = ModelScaleService.Apply(_target, SizeSettings(), _sizePreview.SourceHash, _sizePreview.PreviewHash);
                SetTarget(result.Root);
                _editableRoot = result.Root;
                _savedPrefabPath = result.CreatedAssetPaths.FirstOrDefault(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
                _lastExportPath = _savedPrefabPath;
                Selection.activeGameObject = result.Root;
                return result;
            }, "Cópia criada com o tamanho aprovado, escala (1, 1, 1) e prefab salvo. Acesse Salvar cópia para localizar o arquivo.");
        }

        internal bool CanSaveCopy => _editableRoot != null && _editableRoot == _target && !EditorUtility.IsPersistent(_editableRoot);

        private void DrawSave()
        {
            _outputFolder = EditorGUILayout.TextField("Pasta de saída", _outputFolder);
            if (!CanSaveCopy)
            {
                EditorGUILayout.HelpBox("Crie uma cópia em Limpar geometria ou Ajustar tamanho antes de salvar.", MessageType.Info);
                return;
            }
            EditorGUILayout.LabelField("Hierarquia a salvar", _editableRoot.name);
            EditorGUILayout.HelpBox("O prefab inclui os filhos, materiais e referências do rig. Prévia ainda não aplicada não entra no arquivo salvo.", MessageType.Info);
            if (!string.IsNullOrEmpty(_savedPrefabPath) && GUILayout.Button("Selecionar prefab salvo"))
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(_savedPrefabPath));
            if (GUILayout.Button(string.IsNullOrEmpty(_savedPrefabPath) ? "Salvar cópia como prefab" : "Salvar alterações no prefab da cópia", GUILayout.Height(32f)))
                Run(() =>
                {
                    foreach (var document in _rendererDocuments.Values.Where(value => value != null).Distinct())
                    {
                        var report = ModelingCommandService.Commit(document);
                        if (!report.IsValid) throw new InvalidOperationException("A cópia contém uma malha inválida: " + string.Join(" | ", Issues(report)));
                    }
                    var path = string.IsNullOrEmpty(_savedPrefabPath)
                        ? AssetModelerAssetPaths.UniqueAssetPath(_outputFolder + "/Prefabs", _editableRoot.name, ".prefab") : _savedPrefabPath;
                    var prefab = PrefabUtility.SaveAsPrefabAsset(_editableRoot, path, out bool success);
                    if (!success || prefab == null) throw new InvalidOperationException("Não foi possível salvar o prefab.");
                    AssetDatabase.SaveAssets();
                    _lastExportPath = path;
                    _savedPrefabPath = path;
                    EditorGUIUtility.PingObject(prefab);
                    return prefab;
                }, "Cópia salva como prefab.");
            if (!string.IsNullOrEmpty(_lastExportPath)) EditorGUILayout.SelectableLabel(_lastExportPath, EditorStyles.wordWrappedMiniLabel, GUILayout.Height(36f));
        }

        private void DrawSession()
        {
            EditorGUILayout.LabelField("Documento editável", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_selectedRenderer == null))
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
            EditorGUI.BeginChangeCheck();
            var objectTool = AssetModelerWorkspaceCatalog.ObjectTools[GUILayout.SelectionGrid(
                Array.IndexOf(AssetModelerWorkspaceCatalog.ObjectTools, AssetModelerWorkspaceCatalog.VisibleObjectTool(_objectTool)),
                AssetModelerWorkspaceCatalog.ObjectToolLabels,
                2,
                GUILayout.MinHeight(48f))];
            if (EditorGUI.EndChangeCheck())
            {
                _objectTool = objectTool;
                ClearPreview();
                SceneView.RepaintAll();
            }
            EditorGUILayout.Space(6f);
            switch (_objectTool)
            {
                case AssetModelerObjectTool.Mirror:
                    EditorGUILayout.LabelField("Espelhar mesh", EditorStyles.boldLabel);
                    _axis = GUILayout.Toolbar(_axis, new[] { "X", "Y", "Z" });
                    _mirrorPlane = EditorGUILayout.FloatField(new GUIContent("Plano (offset)", "Posição do plano de espelhamento no eixo escolhido, no espaço local da mesh."), _mirrorPlane);
                    _mirrorCopy = EditorGUILayout.ToggleLeft("Manter original e criar lado espelhado", _mirrorCopy);
                    using (new EditorGUI.DisabledScope(!_mirrorCopy))
                        _mirrorWeldSeam = EditorGUILayout.ToggleLeft(new GUIContent("Soldar emenda", "Une vértices próximos após o espelho com cópia, fechando a costura central."), _mirrorWeldSeam);
                    DrawOperationButtons(CreateCurrentMeshOperation, "Espelho");
                    break;
                case AssetModelerObjectTool.Array:
                    DrawArrayTool();
                    break;
                case AssetModelerObjectTool.Merge:
                    EditorGUILayout.LabelField("Combinar meshes", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("Selecione dois ou mais objetos estáticos na Hierarchy. O resultado será um único objeto e um único asset de mesh.", MessageType.Info);
                    _compactMergeMaterials = EditorGUILayout.ToggleLeft("Unir slots que usam o mesmo material", _compactMergeMaterials);
                    _mergeName = EditorGUILayout.TextField("Nome do resultado", _mergeName);
                    _mergeRemoveSources = EditorGUILayout.ToggleLeft(new GUIContent("Remover objetos originais", "Destrói os objetos de origem após combinar (desfazível via Undo)."), _mergeRemoveSources);
                    EditorGUILayout.LabelField($"Selecionados: {Selection.gameObjects.Length}", EditorStyles.miniLabel);
                    using (new EditorGUI.DisabledScope(Selection.gameObjects.Length < 2))
                        if (GUILayout.Button("Combinar objetos selecionados", GUILayout.Height(30f)))
                            Run(() => SceneObjectOperations.MergeStaticMeshes(Selection.gameObjects, _outputFolder, _compactMergeMaterials, _mergeName, _mergeRemoveSources), "Meshes combinadas em um único asset.");
                    break;
                case AssetModelerObjectTool.Export:
                    EditorGUILayout.LabelField("Exportar FBX", EditorStyles.boldLabel);
                    EditorGUILayout.BeginHorizontal();
                    _fbxPath = EditorGUILayout.TextField("Arquivo", _fbxPath);
                    if (GUILayout.Button("Nome único", GUILayout.Width(90f))) _fbxPath = UniqueFbxPath();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.HelpBox("A exportação usa o pacote oficial FBX Exporter da Unity e mantém a hierarquia selecionada.", MessageType.Info);
                    using (new EditorGUI.DisabledScope(_target == null || !FbxExportAdapter.IsAvailable))
                        if (GUILayout.Button(FbxExportAdapter.IsAvailable ? "Exportar objeto" : "Instale com.unity.formats.fbx", GUILayout.Height(30f)))
                            Run(() => { _lastExportPath = FbxExportAdapter.Export(_target, _fbxPath); return _lastExportPath; }, "FBX exportado.");
                    if (FbxExportAdapter.IsAvailable && Selection.gameObjects.Length > 1)
                        if (GUILayout.Button($"Exportar {Selection.gameObjects.Length} selecionados", GUILayout.Height(26f)))
                            Run(() => ExportSelectedObjects(), "Seleção exportada em FBX.");
                    if (!FbxExportAdapter.IsAvailable)
                        using (new EditorGUI.DisabledScope(_target == null))
                            if (GUILayout.Button("Salvar mesh como .asset (fallback)", GUILayout.Height(26f)))
                                Run(() => SaveMeshAssetFallback(), "Mesh salva como asset editável.");
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_lastExportPath)))
                        if (GUILayout.Button("Revelar na pasta"))
                            EditorUtility.RevealInFinder(_lastExportPath);
                    break;
            }
        }

        private void DrawArrayTool()
        {
            EditorGUILayout.LabelField("Array de objetos", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Linha colada")) SetArrayPreset(SceneArrayMode.Linear, 5);
            if (GUILayout.Button("Grade 2×2")) SetArrayPreset(SceneArrayMode.Grid, 4);
            if (GUILayout.Button("Radial 8")) SetArrayPreset(SceneArrayMode.Radial, 8);
            EditorGUILayout.EndHorizontal();
            _array.Mode = (SceneArrayMode)GUILayout.Toolbar((int)_array.Mode, new[] { "Linear", "Radial", "Grade" });
            _array.Count = Mathf.Max(1, EditorGUILayout.IntField("Quantidade", _array.Count));
            _array.AttachedByBounds = EditorGUILayout.ToggleLeft("Encostar automaticamente pelos bounds", _array.AttachedByBounds);
            _array.IncludeSource = EditorGUILayout.ToggleLeft(new GUIContent("Incluir original na contagem", "Quando ativo, a quantidade inclui o objeto de origem; quando inativo, todas as cópias são novas."), _array.IncludeSource);
            if (_array.Mode == SceneArrayMode.Linear)
                _array.Step = EditorGUILayout.Vector3Field(_array.AttachedByBounds ? "Direção" : "Passo", _array.Step);
            else if (_array.Mode == SceneArrayMode.Grid)
            {
                _array.GridCount = EditorGUILayout.Vector3IntField("Contagem XYZ", _array.GridCount);
                _array.GridSpacing = EditorGUILayout.Vector3Field("Espaçamento XYZ", _array.GridSpacing);
            }
            else
            {
                _array.Step = EditorGUILayout.Vector3Field("Raio", _array.Step);
                _array.RadialAxis = EditorGUILayout.Vector3Field("Eixo", _array.RadialAxis);
                _array.RadialAngle = EditorGUILayout.FloatField("Ângulo total", _array.RadialAngle);
            }
            using (new EditorGUI.DisabledScope(_target == null))
                if (GUILayout.Button("Criar Array", GUILayout.Height(32f)))
                    Run(() => SceneObjectOperations.CreateArray(_target, _array), "Array criado.");
        }

        private void DrawTopology()
        {
            EditorGUI.BeginChangeCheck();
            var topologyTool = (AssetModelerTopologyTool)GUILayout.SelectionGrid(
                (int)_topologyTool,
                AssetModelerWorkspaceCatalog.TopologyToolLabels,
                3,
                GUILayout.MinHeight(76f));
            if (EditorGUI.EndChangeCheck())
            {
                _topologyTool = topologyTool;
                _sceneFaceSelection = AssetModelerWorkspaceCatalog.TopologyUsesFaces(_topologyTool) && _sceneFaceSelection;
                ClearPreview();
                SceneView.RepaintAll();
            }
            EditorGUILayout.Space(6f);
            if (AssetModelerWorkspaceCatalog.TopologyUsesFaces(_topologyTool)) DrawFaceSelectionPanel();

            switch (_topologyTool)
            {
                case AssetModelerTopologyTool.Cleanup:
                    DrawCleanup();
                    return;
                case AssetModelerTopologyTool.Weld:
                    EditorGUILayout.LabelField("Soldar vértices", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("Une vértices próximos preservando costuras de UV e materiais quando possível.", MessageType.Info);
                    _weldTolerance = EditorGUILayout.FloatField("Tolerância", _weldTolerance);
                    break;
                case AssetModelerTopologyTool.Subdivide:
                    EditorGUILayout.LabelField("Subdividir faces selecionadas", EditorStyles.boldLabel);
                    break;
                case AssetModelerTopologyTool.ExtrudeFaces:
                    EditorGUILayout.LabelField("Extrudar faces", EditorStyles.boldLabel);
                    _extrudeDistance = EditorGUILayout.FloatField("Distância", _extrudeDistance);
                    break;
                case AssetModelerTopologyTool.Bevel:
                    EditorGUILayout.LabelField("Inset / Bevel", EditorStyles.boldLabel);
                    _bevelInset = EditorGUILayout.Slider("Inset", _bevelInset, 0f, 0.95f);
                    _bevelHeight = EditorGUILayout.FloatField("Altura", _bevelHeight);
                    break;
                case AssetModelerTopologyTool.AdaptiveRefine:
                    EditorGUILayout.LabelField("Refino adaptativo", EditorStyles.boldLabel);
                    _maximumEdgeLength = EditorGUILayout.FloatField("Comprimento máximo", _maximumEdgeLength);
                    break;
                case AssetModelerTopologyTool.ExtrudeEdges:
                    EditorGUILayout.LabelField("Extrudar arestas de borda", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("Selecione arestas no modo Aresta do Scene View (duplo clique ou tecla L = loop, tecla R = ring). Com a seleção vazia, use IDs no modo avançado.", MessageType.Info);
                    _extrudeDistance = EditorGUILayout.FloatField("Distância", _extrudeDistance);
                    DrawAdvancedIds(() => _edgePairs = EditorGUILayout.TextField(new GUIContent("Pares de vértices", "Ex.: 0,1, 1,2."), _edgePairs));
                    break;
                case AssetModelerTopologyTool.BridgeLoops:
                    EditorGUILayout.LabelField("Conectar dois loops", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("Selecione um loop no modo Aresta (duplo clique) e envie para Loop A ou B, ou use IDs no modo avançado.", MessageType.Info);
                    using (new EditorGUI.DisabledScope(_document == null
                        || _document.Selection.Kind != MeshElementKind.Edge
                        || _document.Selection.TopologyRevision != _document.Revision.Topology
                        || _document.Selection.Indices.Count < 4))
                    {
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("Seleção → Loop A")) _loopA = EdgeSelectionAsLoop();
                        if (GUILayout.Button("Seleção → Loop B")) _loopB = EdgeSelectionAsLoop();
                        EditorGUILayout.EndHorizontal();
                    }
                    DrawAdvancedIds(() =>
                    {
                        _loopA = EditorGUILayout.TextField("Loop A", _loopA);
                        _loopB = EditorGUILayout.TextField("Loop B", _loopB);
                    });
                    break;
                case AssetModelerTopologyTool.FillHole:
                    EditorGUILayout.LabelField("Fechar buraco", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("Com o campo avançado vazio, o maior loop de borda da mesh é detectado automaticamente.", MessageType.Info);
                    DrawAdvancedIds(() => _holeLoop = EditorGUILayout.TextField("Loop de contorno", _holeLoop));
                    break;
            }
            DrawOperationButtons(CreateCurrentMeshOperation, AssetModelerWorkspaceCatalog.TopologyToolLabels[(int)_topologyTool]);
        }

        private void DrawAdvancedIds(Action draw)
        {
            _showAdvancedIds = EditorGUILayout.Foldout(_showAdvancedIds, "Avançado: seleção por IDs", true);
            if (_showAdvancedIds) draw();
        }

        private int[] ResolveEdgePairs()
        {
            var pairs = ParseIndicesPreserveOrder(_edgePairs);
            if (pairs.Length > 0) return pairs;
            if (_document != null
                && _document.Selection.Kind == MeshElementKind.Edge
                && _document.Selection.TopologyRevision == _document.Revision.Topology)
            {
                var values = new int[_document.Selection.Indices.Count];
                for (int i = 0; i < values.Length; i++) values[i] = _document.Selection.Indices[i];
                return values;
            }
            return pairs;
        }

        private string EdgeSelectionAsLoop()
        {
            var ordered = OrderPairsToLoop(_document.Selection.Indices);
            return string.Join(",", ordered);
        }

        private static List<int> OrderPairsToLoop(IReadOnlyList<int> pairs)
        {
            var remaining = new List<int[]>();
            for (int i = 0; i + 1 < pairs.Count; i += 2) remaining.Add(new[] { pairs[i], pairs[i + 1] });
            var loop = new List<int>();
            if (remaining.Count == 0) return loop;
            var first = remaining[0];
            remaining.RemoveAt(0);
            loop.Add(first[0]);
            loop.Add(first[1]);
            for (int guard = 0; guard < 100000 && remaining.Count > 0; guard++)
            {
                int tail = loop[loop.Count - 1];
                int found = -1;
                bool flip = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (remaining[i][0] == tail) { found = i; break; }
                    if (remaining[i][1] == tail) { found = i; flip = true; break; }
                }
                if (found < 0) break;
                var edge = remaining[found];
                remaining.RemoveAt(found);
                int nextVertex = flip ? edge[0] : edge[1];
                if (nextVertex == loop[0]) break;
                loop.Add(nextVertex);
            }
            return loop;
        }

        private int[] ResolveHoleLoop()
        {
            var loop = ParseIndices(_holeLoop);
            if (loop.Length > 0) return loop;
            RequireDocument();
            var topology = HalfEdgeTopology.Build(_document.WorkingMesh);
            foreach (var candidate in topology.BoundaryLoops())
                if (candidate.Length > loop.Length) loop = candidate;
            return loop;
        }

        private void DrawFaceSelectionPanel()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            bool enabled = GUILayout.Toggle(_sceneFaceSelection, "Selecionar faces no Scene View", "Button", GUILayout.Height(26f));
            if (enabled != _sceneFaceSelection)
            {
                _sceneFaceSelection = enabled;
                SceneView.RepaintAll();
            }
            using (new EditorGUI.DisabledScope(_document == null))
                if (GUILayout.Button("Limpar", GUILayout.Width(58f), GUILayout.Height(26f))) SetFaceSelection(Array.Empty<int>());
            EditorGUILayout.EndHorizontal();
            int selectedCount = _document != null && _document.Selection.Kind == MeshElementKind.Face
                ? _document.Selection.Indices.Count
                : 0;
            EditorGUILayout.LabelField($"{selectedCount} face(s) selecionada(s)", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(
                "Clique seleciona • duplo clique pega a ilha conectada • arraste → seleciona área • esquerda→direita exige faces inteiras • direita→esquerda inclui faces cruzadas • Shift adiciona • Ctrl/Cmd remove.",
                MessageType.None);
            using (new EditorGUI.DisabledScope(_document == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Tudo", EditorStyles.miniButtonLeft)) SelectAllFaces();
                if (GUILayout.Button("Inverter", EditorStyles.miniButtonMid)) InvertFaceSelection();
                using (new EditorGUI.DisabledScope(selectedCount == 0))
                    if (GUILayout.Button("Conectadas", EditorStyles.miniButtonRight)) SelectConnectedFaces();
                EditorGUILayout.EndHorizontal();
            }
            _showAdvancedSelection = EditorGUILayout.Foldout(_showAdvancedSelection, "Seleção avançada por IDs e regiões", true);
            if (_showAdvancedSelection)
            {
                EditorGUI.BeginChangeCheck();
                string faceSelection = EditorGUILayout.TextField(new GUIContent("IDs de faces", "Ex.: 0,1,4-8."), _faceSelection);
                if (EditorGUI.EndChangeCheck())
                {
                    _faceSelection = faceSelection;
                    if (_document != null)
                    {
                        try { SetFaceSelection(ParseIndices(_faceSelection)); }
                        catch (Exception exception) { Failure(exception); }
                    }
                }
                EditorGUILayout.BeginHorizontal();
                _regionName = EditorGUILayout.TextField("Região", _regionName);
                if (GUILayout.Button("Salvar", GUILayout.Width(60f))) SaveFaceRegion();
                if (GUILayout.Button("Usar", GUILayout.Width(50f))) UseRegion();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawDeform()
        {
            EditorGUILayout.LabelField("Modo", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var deformMode = (MeshDeformMode)GUILayout.SelectionGrid(
                (int)_deformMode,
                new[] { "Puxar", "Suavizar", "Relaxar", "Planificar", "Inflar", "Dobrar", "Torcer", "Afilar", "Lattice", "Curva", "Laplacian", "ARAP" },
                3,
                GUILayout.MinHeight(96f));
            if (EditorGUI.EndChangeCheck())
            {
                _deformMode = deformMode;
                ClearPreview();
                SceneView.RepaintAll();
            }
            EditorGUILayout.Space(5f);
            EditorGUILayout.HelpBox(DeformDescription(_deformMode), MessageType.Info);
            if (_deformMode == MeshDeformMode.Flatten
                || _deformMode == MeshDeformMode.Bend
                || _deformMode == MeshDeformMode.Twist
                || _deformMode == MeshDeformMode.Taper
                || _deformMode == MeshDeformMode.Spline)
                _axis = GUILayout.Toolbar(_axis, new[] { "Eixo X", "Eixo Y", "Eixo Z" });
            _deformStrength = EditorGUILayout.FloatField(
                _deformMode == MeshDeformMode.Bend || _deformMode == MeshDeformMode.Twist ? "Ângulo / força" : "Força",
                _deformStrength);
            _deformRadius = EditorGUILayout.FloatField("Raio de influência", _deformRadius);
            if (_deformMode == MeshDeformMode.Smooth
                || _deformMode == MeshDeformMode.Relax
                || _deformMode == MeshDeformMode.Laplacian
                || _deformMode == MeshDeformMode.Arap)
                _deformIterations = EditorGUILayout.IntSlider("Qualidade", _deformIterations, 1, 100);
            if (_deformMode == MeshDeformMode.Grab)
                _deformDirection = EditorGUILayout.Vector3Field("Direção", _deformDirection);
            if (_deformMode == MeshDeformMode.Spline)
                _splinePoints = EditorGUILayout.TextField(new GUIContent("Pontos da curva", "XYZ separados por vírgula; pontos separados por ponto e vírgula."), _splinePoints);
            _showAdvancedSelection = EditorGUILayout.Foldout(_showAdvancedSelection, "Limitar a vértices específicos", true);
            if (_showAdvancedSelection)
            {
                _vertexSelection = EditorGUILayout.TextField("Vértices afetados", _vertexSelection);
                _pinnedSelection = EditorGUILayout.TextField("Vértices fixos", _pinnedSelection);
            }
            DrawOperationButtons(CreateCurrentMeshOperation, "Deformação");
        }

        private void DrawSurfaceFit()
        {
            EditorGUILayout.LabelField("Smart Surface Fit", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("O objeto editável é a roupa/acessório. Escolha abaixo o corpo que servirá como superfície alvo.", MessageType.Info);
            _fitTarget = (Renderer)EditorGUILayout.ObjectField("Corpo alvo", _fitTarget, typeof(Renderer), true);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Roupa justa")) SetSurfaceFitPreset(0.003f, 0.65f);
            if (GUILayout.Button("Roupa comum")) SetSurfaceFitPreset(0.008f, 0.4f);
            if (GUILayout.Button("Armadura")) SetSurfaceFitPreset(0.02f, 0.8f);
            EditorGUILayout.EndHorizontal();
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
            DrawOperationButtons(CreateCurrentMeshOperation, "Ajustar roupa");
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

        private IMeshOperation CreateCurrentMeshOperation()
        {
            switch (_navigation.Workspace)
            {
                case AssetModelerWorkspace.Objects:
                    if (_objectTool == AssetModelerObjectTool.Mirror)
                    {
                        if (_mirrorCopy && _mirrorWeldSeam)
                            return new MirrorAndWeldMeshOperation(_axis, _mirrorPlane, _weldTolerance, true);
                        return new MirrorMeshOperation(_axis, _mirrorPlane, _mirrorCopy);
                    }
                    break;
                case AssetModelerWorkspace.Topology:
                    switch (_topologyTool)
                    {
                        case AssetModelerTopologyTool.Cleanup: return CreateCleanupOperation();
                        case AssetModelerTopologyTool.Weld: return new WeldVerticesOperation(_weldTolerance, true);
                        case AssetModelerTopologyTool.Subdivide: return new SubdivideTrianglesOperation(CurrentFaceSelection());
                        case AssetModelerTopologyTool.ExtrudeFaces: return new ExtrudeFacesOperation(CurrentFaceSelection(), _extrudeDistance);
                        case AssetModelerTopologyTool.Bevel: return new BevelFacesOperation(CurrentFaceSelection(), _bevelInset, _bevelHeight);
                        case AssetModelerTopologyTool.AdaptiveRefine: return new AdaptiveRefineOperation(_maximumEdgeLength);
                        case AssetModelerTopologyTool.ExtrudeEdges: return new ExtrudeBoundaryEdgesOperation(ResolveEdgePairs(), _extrudeDistance);
                        case AssetModelerTopologyTool.BridgeLoops: return new BridgeLoopsOperation(ParseIndices(_loopA), ParseIndices(_loopB));
                        case AssetModelerTopologyTool.FillHole: return new FillHoleOperation(ResolveHoleLoop());
                    }
                    break;
                case AssetModelerWorkspace.Deform:
                    return new DeformMeshOperation(new MeshDeformParameters
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
                    });
                case AssetModelerWorkspace.SurfaceFit:
                    return CreateSurfaceFitOperation();
                case AssetModelerWorkspace.Cleanup:
                    return CreateCleanupOperation();
            }
            throw new InvalidOperationException("A ferramenta atual executa diretamente na cena e não usa preview de mesh.");
        }

        private void DrawOperationButtons(Func<IMeshOperation> factory, string label = null)
        {
            EditorGUILayout.Space(7f);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_document == null))
            {
                string previewLabel = _preview == null ? "Gerar prévia" : "Atualizar prévia";
                var previewContent = new GUIContent(previewLabel, string.IsNullOrEmpty(label) ? null : $"Pré-visualizar {label} sem alterar a mesh.");
                if (GUILayout.Button(previewContent, GUILayout.Height(30f)))
                {
                    try { PreviewOperation(factory()); }
                    catch (Exception exception) { Failure(exception); }
                }
                using (new EditorGUI.DisabledScope(!SceneCanApplyPreview))
                {
                    var applyContent = new GUIContent("Aplicar prévia", string.IsNullOrEmpty(label) ? null : $"Aplicar {label} à cópia editável.");
                    if (GUILayout.Button(applyContent, GUILayout.Height(30f)))
                    {
                        try { ApplyOperation(factory()); }
                        catch (Exception exception) { Failure(exception); }
                    }
                }
            }
            using (new EditorGUI.DisabledScope(_preview == null))
                if (GUILayout.Button("×", GUILayout.Width(30f), GUILayout.Height(30f))) ClearPreview();
            EditorGUILayout.EndHorizontal();
            if (_document == null)
                EditorGUILayout.LabelField("Crie uma cópia editável para usar esta operação.", EditorStyles.centeredGreyMiniLabel);
        }

        private void OpenWorkspacePage(AssetModelerWorkspace workspace)
        {
            ClearPreview();
            _sceneFaceSelection = false;
            _navigation.Open(workspace);
            _scroll = Vector2.zero;
            Repaint();
            SceneView.RepaintAll();
        }

        private void SetArrayPreset(SceneArrayMode mode, int count)
        {
            _array.Mode = mode;
            _array.Count = count;
            _array.AttachedByBounds = mode == SceneArrayMode.Linear;
            _array.Step = mode == SceneArrayMode.Radial ? new Vector3(1f, 0f, 0f) : Vector3.right;
            _array.GridCount = new Vector3Int(2, 2, 1);
            _array.GridSpacing = Vector3.one;
            _array.RadialAxis = Vector3.up;
            _array.RadialAngle = 360f;
        }

        private void SetSurfaceFitPreset(float clearance, float preservation)
        {
            _fitClearance = clearance;
            _fitShapePreservation = preservation;
            _fitStrength = 1f;
            _fitIterations = preservation > 0.7f ? 12 : 8;
            _fitAdaptiveRefine = true;
        }

        private static string DeformDescription(MeshDeformMode mode)
        {
            switch (mode)
            {
                case MeshDeformMode.Grab: return "Move uma região com influência suave ao redor dos vértices selecionados.";
                case MeshDeformMode.Smooth: return "Suaviza irregularidades locais sem trocar a topologia.";
                case MeshDeformMode.Relax: return "Distribui os vértices com mais uniformidade preservando o volume.";
                case MeshDeformMode.Flatten: return "Aproxima a região de um plano no eixo escolhido.";
                case MeshDeformMode.Inflate: return "Expande ou contrai a superfície seguindo as normais.";
                case MeshDeformMode.Bend: return "Dobra a malha ao longo do eixo escolhido.";
                case MeshDeformMode.Twist: return "Torçe progressivamente a malha ao redor do eixo.";
                case MeshDeformMode.Taper: return "Afila ou alarga a malha progressivamente.";
                case MeshDeformMode.Lattice: return "Deforma o volume usando uma gaiola de controle.";
                case MeshDeformMode.Spline: return "Faz a malha acompanhar uma sequência de pontos de curva.";
                case MeshDeformMode.Laplacian: return "Preserva detalhes enquanto ajusta a forma global.";
                case MeshDeformMode.Arap: return "Deformação rígida por partes para reduzir distorções.";
                default: return string.Empty;
            }
        }

        private void PreviewOperation(IMeshOperation operation)
        {
            RequireDocument();
            ClearPreview();
            _preview = ModelingCommandService.Preview(_document, operation, _document.Revision);
            if (operation is CleanupMeshOperation)
                _cleanupAfter = MeshCleanupAnalysis.Analyze(_preview.PreviewMesh, _cleanupEpsilon);
            ResolveWorkingRenderer();
            MeshOperationPreviewController.Show(_preview, _workingRenderer);
            _status = "Prévia pronta. Revise o resultado antes de aplicar à cópia.";
            _statusType = MessageType.Info;
        }

        private void ApplyOperation(IMeshOperation operation)
        {
            RequireDocument();
            if (!SceneCanApplyPreview) throw new InvalidOperationException("A prévia está ausente ou desatualizada. Gere e revise outra antes de aplicar.");
            ModelingCommandService.Apply(_document, operation, _preview.Result.RevisionBefore, _preview.PreviewHash);
            ClearPreview();
            _cleanupAnalysis = null;
            _cleanupComponents.Clear();
            _status = "Alteração aplicada à cópia editável.";
            _statusType = MessageType.Info;
        }

        private void CreateEditableCopy()
        {
            Run(() =>
            {
                if (_selectedRenderer == null) throw new InvalidOperationException("Escolha a malha para edição primeiro.");
                string folder = _outputFolder + "/Editable/" + AssetModelerAssetPaths.SafeName(_target.name);
                var result = CanSaveCopy
                    ? EditableMeshFactory.CreateForExistingCopy(_editableRoot, _selectedRenderer, folder)
                    : EditableMeshFactory.CreateModelCopy(_target, _selectedRenderer, folder);
                UseEditableCopy(result);
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

        private int[] CurrentFaceSelection()
        {
            if (_document != null
                && _document.Selection.Kind == MeshElementKind.Face
                && _document.Selection.TopologyRevision == _document.Revision.Topology)
            {
                var values = new int[_document.Selection.Indices.Count];
                for (int i = 0; i < values.Length; i++) values[i] = _document.Selection.Indices[i];
                return values;
            }
            return ParseIndices(_faceSelection);
        }

        private void SetFaceSelection(IEnumerable<int> indices)
        {
            RequireDocument();
            Undo.RecordObject(_document, "Select mesh faces");
            _document.Selection.SetSelection(MeshElementKind.Face, indices, _document.Revision.Topology);
            _faceSelection = string.Join(",", _document.Selection.Indices);
            EditorUtility.SetDirty(_document);
            Repaint();
            SceneView.RepaintAll();
        }

        internal void SelectFaceFromScene(int faceIndex, bool additive, bool remove)
            => SelectFacesFromScene(new[] { faceIndex }, additive, remove, $"Face {faceIndex}");

        internal void SelectFacesFromScene(IEnumerable<int> faceIndices, bool additive, bool remove, string source)
        {
            try
            {
                RequireDocument();
                var selected = new SortedSet<int>();
                if (additive || remove)
                    for (int i = 0; i < _document.Selection.Indices.Count; i++) selected.Add(_document.Selection.Indices[i]);
                var incoming = new SortedSet<int>();
                if (faceIndices != null)
                    foreach (int faceIndex in faceIndices)
                        if (faceIndex >= 0) incoming.Add(faceIndex);
                if (remove) selected.ExceptWith(incoming);
                else selected.UnionWith(incoming);
                SetFaceSelection(selected);
                _status = $"{source}: {incoming.Count} face(s); {selected.Count} selecionada(s) no total.";
                _statusType = MessageType.Info;
            }
            catch (Exception exception) { Failure(exception); }
        }

        private void SelectAllFaces()
        {
            try
            {
                RequireDocument();
                var topology = HalfEdgeTopology.Build(_document.WorkingMesh);
                var faces = new int[topology.Faces.Count];
                for (int i = 0; i < faces.Length; i++) faces[i] = i;
                SetFaceSelection(faces);
                _status = $"Todas as {faces.Length} faces foram selecionadas.";
                _statusType = MessageType.Info;
            }
            catch (Exception exception) { Failure(exception); }
        }

        private void InvertFaceSelection()
        {
            try
            {
                RequireDocument();
                var topology = HalfEdgeTopology.Build(_document.WorkingMesh);
                var current = new HashSet<int>(_document.Selection.Kind == MeshElementKind.Face
                    ? _document.Selection.Indices
                    : Array.Empty<int>());
                var inverted = new List<int>();
                for (int i = 0; i < topology.Faces.Count; i++) if (!current.Contains(i)) inverted.Add(i);
                SetFaceSelection(inverted);
                _status = $"Seleção invertida: {inverted.Count} face(s).";
                _statusType = MessageType.Info;
            }
            catch (Exception exception) { Failure(exception); }
        }

        private void SelectConnectedFaces()
        {
            try
            {
                RequireDocument();
                var topology = HalfEdgeTopology.Build(_document.WorkingMesh);
                var connected = AssetModelerSceneController.CollectConnectedFaces(topology, CurrentFaceSelection());
                SetFaceSelection(connected);
                _status = $"Ilhas conectadas expandidas para {connected.Length} face(s).";
                _statusType = MessageType.Info;
            }
            catch (Exception exception) { Failure(exception); }
        }

        internal GameObject SceneTarget => _target;
        internal MeshEditDocument SceneDocument => _document;
        internal Renderer SceneRenderer => _workingRenderer;
        internal AssetModelerWorkspace SceneWorkspace => _navigation?.Workspace ?? AssetModelerWorkspace.Home;
        internal bool SceneHasPreview => _preview != null;
        internal bool HasSizePreview => _sizePreview != null;
        internal bool SceneCanApplyPreview => _preview != null && _document != null
            && _document.Matches(_preview.Result.RevisionBefore)
            && RendererMesh(_workingRenderer) == _document.WorkingMesh;
        internal bool SceneFacePickingActive => (_sceneFaceSelection
                && _navigation != null
                && _navigation.Workspace == AssetModelerWorkspace.Topology
                && AssetModelerWorkspaceCatalog.TopologyUsesFaces(_topologyTool)
                || SceneUx.AssetModelerToolContext.IsActive
                && SceneUx.AssetModelerModelingState.Mode == SceneUx.AssetModelerEditMode.Face)
            && _document?.WorkingMesh != null
            && _workingRenderer != null;
        internal bool SceneSupportsPreview => _navigation != null
            && AssetModelerWorkspaceCatalog.SupportsMeshPreview(_navigation.Workspace, _objectTool, _topologyTool);
        internal IReadOnlyList<int> SceneSelectedFaces
            => _document != null && _document.Selection.Kind == MeshElementKind.Face
                ? _document.Selection.Indices
                : Array.Empty<int>();

        internal void SceneOpenWorkspace(AssetModelerWorkspace workspace) => OpenWorkspacePage(workspace);
        internal void SceneCreateEditableCopy() => CreateEditableCopy();
        internal void SceneFrameTarget() => FrameTarget();
        internal void SceneCancelPreview() { ClearPreview(); Repaint(); }
        internal void ScenePreviewCurrent()
        {
            try { PreviewOperation(CreateCurrentMeshOperation()); }
            catch (Exception exception) { Failure(exception); }
            Repaint();
        }
        internal void SceneApplyCurrent()
        {
            try { ApplyOperation(CreateCurrentMeshOperation()); }
            catch (Exception exception) { Failure(exception); }
            Repaint();
        }

        internal AssetModelerTopologyTool SceneTopologyTool => _topologyTool;
        internal MeshDeformMode SceneDeformMode => _deformMode;
        internal int SceneDeformAxis => _axis;
        internal float SceneExtrudeDistance { get => _extrudeDistance; set { _extrudeDistance = value; Repaint(); } }
        internal float SceneBevelInset { get => _bevelInset; set { _bevelInset = value; Repaint(); } }
        internal float SceneDeformRadius { get => _deformRadius; set { _deformRadius = value; Repaint(); } }
        internal float SceneDeformStrength { get => _deformStrength; set { _deformStrength = value; Repaint(); } }
        internal Vector3 SceneDeformDirection { get => _deformDirection; set { _deformDirection = value; Repaint(); } }
        internal Vector3[] SceneSplinePoints { get => ParseSplinePoints(); set { _splinePoints = FormatSplinePoints(value); Repaint(); } }
        internal Renderer SceneFitTarget => _fitTarget;
        internal float SceneFitClearance { get => _fitClearance; set { _fitClearance = value; Repaint(); } }

        internal void SceneStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }

        internal void SceneSelectRegion(string name)
        {
            _regionName = name;
            UseRegion();
        }

        internal void SceneSelectElements(MeshElementKind kind, IEnumerable<int> indices, bool additive, bool remove, string source)
        {
            try
            {
                RequireDocument();
                bool canCombine = (additive || remove)
                    && _document.Selection.Kind == kind
                    && _document.Selection.TopologyRevision == _document.Revision.Topology;
                var selected = canCombine
                    ? MeshSelectionEncoding.Normalize(kind, _document.Selection.Indices)
                    : new List<int>();
                var incoming = MeshSelectionEncoding.Normalize(kind, indices);

                if (kind == MeshElementKind.Edge)
                {
                    var edgeSet = new HashSet<MeshEdgeKey>(MeshSelectionEncoding.EdgePairs(selected));
                    var incomingEdges = MeshSelectionEncoding.EdgePairs(incoming);
                    if (remove) edgeSet.ExceptWith(incomingEdges);
                    else edgeSet.UnionWith(incomingEdges);
                    selected = MeshSelectionEncoding.FlattenEdges(edgeSet.OrderBy(edge => edge.A).ThenBy(edge => edge.B));
                }
                else
                {
                    var valueSet = new SortedSet<int>(selected);
                    if (remove) valueSet.ExceptWith(incoming);
                    else valueSet.UnionWith(incoming);
                    selected = new List<int>(valueSet);
                }
                Undo.RecordObject(_document, "Select mesh elements");
                _document.Selection.SetSelection(kind, selected, _document.Revision.Topology);
                if (kind == MeshElementKind.Face) _faceSelection = string.Join(",", _document.Selection.Indices);
                EditorUtility.SetDirty(_document);
                _status = $"{source}: {MeshSelectionEncoding.ElementCount(kind, incoming)} elemento(s); "
                    + $"{MeshSelectionEncoding.ElementCount(kind, selected)} selecionado(s) no total.";
                _statusType = MessageType.Info;
                Repaint();
                SceneView.RepaintAll();
            }
            catch (Exception exception) { Failure(exception); }
        }

        private Vector3[] ParseSplinePoints()
        {
            var flat = ParseVectorList(_splinePoints);
            var result = new Vector3[flat.Length / 3];
            for (int i = 0; i < result.Length; i++)
                result[i] = new Vector3(flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2]);
            return result;
        }

        private static string FormatSplinePoints(Vector3[] points)
        {
            if (points == null || points.Length == 0) return string.Empty;
            var parts = new string[points.Length];
            for (int i = 0; i < points.Length; i++)
                parts[i] = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", points[i].x, points[i].y, points[i].z);
            return string.Join("; ", parts);
        }

        private string UniqueFbxPath()
        {
            var folder = ExportFolder();
            var baseName = _target != null ? AssetModelerAssetPaths.SafeName(_target.name, "model") : "model";
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            return AssetModelerAssetPaths.UniqueAssetPath(folder, baseName + "_" + stamp, ".fbx");
        }

        private string ExportFolder()
        {
            var folder = System.IO.Path.GetDirectoryName(_fbxPath ?? string.Empty)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets", StringComparison.Ordinal))
                folder = "Assets/AssetModeler/Exports";
            return folder;
        }

        private object ExportSelectedObjects()
        {
            var folder = ExportFolder();
            var selected = Selection.gameObjects;
            for (int i = 0; i < selected.Length; i++)
            {
                var path = AssetModelerAssetPaths.UniqueAssetPath(folder, AssetModelerAssetPaths.SafeName(selected[i].name, "model"), ".fbx");
                _lastExportPath = FbxExportAdapter.Export(selected[i], path);
            }
            return null;
        }

        private object SaveMeshAssetFallback()
        {
            var filter = _target != null ? _target.GetComponentInChildren<MeshFilter>() : null;
            if (filter == null || filter.sharedMesh == null)
                throw new InvalidOperationException("O objeto não possui MeshFilter com mesh para salvar.");
            var path = AssetModelerAssetPaths.UniqueAssetPath(ExportFolder(), AssetModelerAssetPaths.SafeName(_target.name), ".asset");
            var copy = UnityEngine.Object.Instantiate(filter.sharedMesh);
            copy.name = AssetModelerAssetPaths.SafeName(_target.name);
            AssetDatabase.CreateAsset(copy, path);
            AssetDatabase.SaveAssets();
            _lastExportPath = path;
            return path;
        }

        private void FrameTarget()
        {
            if (_target == null) return;
            Selection.activeGameObject = _target;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        internal void UseEditableCopy(EditableMeshResult result)
        {
            ClearPreview();
            SetTarget(result.EditableRoot != null ? result.EditableRoot : result.EditableObject);
            _editableRoot = _target;
            _selectedRenderer = _workingRenderer = result.EditableRenderer;
            _document = result.Document;
            _cleanupAnalysis = null;
            _cleanupComponents.Clear();
            if (_workingRenderer != null) _rendererDocuments[_workingRenderer] = _document;
        }

        internal void SetTarget(GameObject target)
        {
            if (_target == target) return;
            ClearPreview();
            _target = target;
            _editableRoot = null;
            _savedPrefabPath = null;
            _lastExportPath = null;
            _document = null;
            _rendererDocuments.Clear();
            _cleanupAnalysis = null;
            _cleanupComponents.Clear();
            _workingRenderer = null;
            _selectedRenderer = null;
            if (_target != null && EditableMeshFactory.TryResolveRenderer(_target, out var renderer, out _, out _)) _selectedRenderer = renderer;
            Repaint();
        }

        internal void SetRenderer(Renderer renderer)
        {
            if (renderer != null && (_target == null || renderer.transform != _target.transform && !renderer.transform.IsChildOf(_target.transform)))
                throw new InvalidOperationException("A malha escolhida deve pertencer à raiz do modelo.");
            if (_selectedRenderer == renderer) return;
            ClearPreview();
            if (_selectedRenderer != null && _document != null) _rendererDocuments[_selectedRenderer] = _document;
            _selectedRenderer = renderer;
            _document = null;
            _workingRenderer = null;
            if (renderer != null && _rendererDocuments.TryGetValue(renderer, out var document)
                && document != null && RendererMesh(renderer) == document.WorkingMesh)
            {
                _document = document;
                _workingRenderer = renderer;
            }
            _cleanupAnalysis = null;
            _cleanupComponents.Clear();
        }

        private static Mesh RendererMesh(Renderer renderer)
        {
            return RendererMeshUtility.GetSharedMesh(renderer);
        }

        private void ResolveWorkingRenderer()
        {
            if (_document != null && RendererMesh(_selectedRenderer) == _document.WorkingMesh)
                _workingRenderer = _selectedRenderer;
        }

        private void ClearPreview()
        {
            _sizePreview = null;
            _cleanupAfter = null;
            if (_preview != null)
            {
                MeshOperationPreviewController.Detach();
                _preview.Dispose();
                _preview = null;
            }
        }

        private void RequireDocument()
        {
            if (_document == null || _document.WorkingMesh == null || RendererMesh(_selectedRenderer) != _document.WorkingMesh)
                throw new InvalidOperationException("Crie uma cópia editável da malha escolhida primeiro.");
        }

        private void Run(Func<object> action, string success)
        {
            try { action(); _status = success; _statusType = MessageType.Info; }
            catch (Exception exception) { Failure(exception); }
        }

        private void Failure(Exception exception)
        {
            var message = exception?.Message ?? "Erro desconhecido.";
            string code = message.Split(':')[0];
            switch (code)
            {
                case "MODEL_ROOT_SCALE_UNSUPPORTED": message = "A raiz precisa ter escala positiva e uniforme nos três eixos antes do ajuste de tamanho."; break;
                case "MODEL_EXTERNAL_BONE": message = "Escolha uma raiz que contenha todas as malhas e todos os ossos do modelo."; break;
                case "MODEL_MESH_UNREADABLE": case "MESH_NOT_READABLE": message = "Ative Read/Write nas configurações de importação de todas as malhas envolvidas."; break;
                case "MODEL_DIMENSION_INVALID": message = "Informe uma dimensão maior que zero."; break;
                case "STALE_MODEL_SOURCE": case "STALE_MODEL_PREVIEW": message = "O modelo ou as opções mudaram. Gere e revise uma nova prévia de tamanho."; break;
                case "MODEL_OUTPUT_INVALID": message = "Escolha uma pasta de saída dentro de Assets/."; break;
                case "MODEL_EDIT_MODE_REQUIRED": message = "Saia do modo de jogo e aguarde o Editor ficar pronto antes de ajustar o tamanho."; break;
            }
            _status = message;
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
