using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    public sealed class Ai3dStudioWindow : EditorWindow
    {
        private static readonly string[] PageLabels = { "Gerar", "Processar", "Jobs", "Candidato", "Configuração" };
        private static readonly string[] PresetLabels = { "Game Mobile", "Game PC", "Hero Prop", "Humanoid" };

        private int _page;
        private Vector2 _scroll;
        private bool _busy;
        private bool _polling;
        private bool _autoPoll = true;
        private double _nextPoll;
        private string _status;
        private MessageType _statusType = MessageType.None;
        private CancellationTokenSource _lifetime;

        private string _gatewayUrl;
        private string _gatewayToken;
        private bool _showGatewayToken;
        private string _quarantineRoot;
        private string _acceptedRoot;
        private int _timeoutSeconds;
        private int _maxWorkflowCredits;
        private Ai3dGatewayHealth _health;
        private JObject _capabilities;
        private Ai3dCredentialStatus _credentialStatus;
        private Ai3dCredentialStatus _meshyCredentialStatus;
        private string _tripoApiKeyInput;
        private string _meshyApiKeyInput;
        private bool _showTripoApiKey;
        private bool _showMeshyApiKey;
        private Ai3dGatewayProcessSnapshot _gatewayProcess;
        private JObject _balance;
        private string _provider = "tripo";

        private int _generationMode;
        private Ai3dGenerationPreset _preset = Ai3dGenerationPreset.GamePc;
        private string _model;
        private string _meshyModel = "latest";
        private string _prompt = "Um prop 3D game-ready, topologia limpa, materiais PBR e escala realista";
        private string _imagePath;
        private int _faceLimit = 50000;
        private bool _texture = true;
        private bool _pbr = true;
        private bool _smartLowPoly;
        private string _geometryQuality = "standard";
        private int _estimatedCredits = 120;
        private int _capCredits = 180;
        private bool _externalSendApproved;
        private string _pendingConvertKey;

        private readonly List<Ai3dJobSummary> _jobs = new List<Ai3dJobSummary>();
        private Ai3dJobSnapshot _selectedJob;
        private Ai3dArtifactValidation _artifactValidation;
        private Ai3dStagedCandidate _staged;
        private string _reviewedPreviewHash;
        private string _currentPreviewHash;
        private string _acceptedAssetPath;
        private string _activeWorkflowId;
        private string _activeWorkflowPlanHash;

        private int _processMode;
        private string _meshPath;
        private bool _quadRetopology;
        private bool _bakeRetopology = true;
        private string _segmentationGranularity = "balanced";
        private string _completionMode = "quick_cap";
        private bool _splitByConnectivity = true;
        private string _rigType = "auto";
        private string _rigSpec = "mixamo";
        private string _animationPresets = "preset:idle, preset:walk, preset:run";
        private string _meshyAnimationActionIds = string.Empty;
        private float _heightMeters = 1.7f;
        private int _animationFps = 30;
        private bool _animateInPlace = true;
        private int _processBudgetCap = 120;

        [MenuItem("Window/AI 3D Studio (Unity MCP)")]
        public static Ai3dStudioWindow Open()
        {
            var window = GetWindow<Ai3dStudioWindow>();
            window.titleContent = new GUIContent("AI 3D Studio");
            window.minSize = new Vector2(460f, 600f);
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            _lifetime = new CancellationTokenSource();
            _gatewayUrl = Ai3dGatewaySettings.GatewayUrl;
            _gatewayToken = Ai3dGatewaySettings.GatewayToken;
            _quarantineRoot = Ai3dGatewaySettings.QuarantineRoot;
            _acceptedRoot = Ai3dGatewaySettings.AcceptedRoot;
            _timeoutSeconds = Ai3dGatewaySettings.TimeoutSeconds;
            _maxWorkflowCredits = Ai3dGatewaySettings.MaxCreditsPerWorkflow;
            _model = Ai3dGatewaySettings.ModelVersion;
            EditorApplication.update += OnEditorUpdate;
            _ = InitializeAsync();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
            _tripoApiKeyInput = null;
            _meshyApiKeyInput = null;
            if (_staged != null && Ai3dScenePreviewController.ActiveAsset == _staged.Asset)
                Ai3dScenePreviewController.Clear();
        }

        private async Task InitializeAsync()
        {
            await TestConnectionAsync(false);
            if (!string.IsNullOrEmpty(Ai3dGatewaySettings.LastWorkflowId))
            {
                try
                {
                    var workflow = Ai3dWorkflowService.Get(Ai3dGatewaySettings.LastWorkflowId);
                    _activeWorkflowId = workflow.WorkflowId;
                    _activeWorkflowPlanHash = workflow.PlanHash;
                    if (!string.IsNullOrEmpty(workflow.CurrentJobId))
                        _selectedJob = await Ai3dGatewayClient.GetJobAsync(workflow.CurrentJobId, Token);
                }
                catch { Ai3dGatewaySettings.LastWorkflowId = string.Empty; }
            }
            else if (!string.IsNullOrEmpty(Ai3dGatewaySettings.LastJobId))
            {
                try { await SelectJobAsync(Ai3dGatewaySettings.LastJobId); }
                catch { /* Last job may belong to a removed gateway data directory. */ }
            }
            await RefreshJobsAsync(false);
        }

        private void OnEditorUpdate()
        {
            if (!_autoPoll || _busy || _polling || _selectedJob == null
                || (_selectedJob.IsTerminal && string.IsNullOrEmpty(_activeWorkflowId))) return;
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1.5d;
            if (!string.IsNullOrEmpty(_activeWorkflowId)) _ = RefreshActiveWorkflowAsync(false);
            else _ = RefreshSelectedJobAsync(false);
        }

        private void OnGUI()
        {
            DrawHeader();
            _page = GUILayout.Toolbar(_page, PageLabels, GUILayout.Height(26f));
            EditorGUILayout.Space(6f);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_page)
            {
                case 0: DrawGeneratePage(); break;
                case 1: DrawProcessPage(); break;
                case 2: DrawJobsPage(); break;
                case 3: DrawCandidatePage(); break;
                default: DrawSettingsPage(); break;
            }
            EditorGUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _statusType);
            if (_busy) EditorGUILayout.HelpBox("Operação em andamento…", MessageType.Info);
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("AI 3D Studio", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                var configured = SelectedProviderConfigured;
                var state = _health == null ? "não testado"
                    : !_health.Ready ? "offline"
                    : configured ? SelectedProviderLabel + " pronta" : "gateway sem API key " + SelectedProviderLabel;
                var previous = GUI.color;
                GUI.color = _health != null && _health.Ready && configured
                    ? new Color(0.55f, 1f, 0.65f)
                    : new Color(1f, 0.75f, 0.35f);
                GUILayout.Label("● " + state, EditorStyles.miniBoldLabel);
                GUI.color = previous;
            }
        }

        private void DrawProviderSelector()
        {
            var values = new[] { "tripo", "meshy", "auto" };
            var labels = new[] { "Tripo", "Meshy", "Automático" };
            var selected = Math.Max(0, Array.IndexOf(values, _provider));
            var next = EditorGUILayout.Popup(
                new GUIContent("Provedor", "Automático escolhe Meshy, exceto na separação semântica exclusiva da Tripo."),
                selected, labels);
            if (next != selected)
            {
                _provider = values[next];
                if (_provider != "tripo")
                {
                    _capCredits = _generationMode == 0 ? (_texture ? 45 : 30) : (_texture ? 40 : 30);
                    _processBudgetCap = _processMode == 0 ? 8 : 25;
                }
            }
        }

        private string EffectiveProvider
            => _provider == "auto" ? (_page == 1 && _processMode == 1 ? "tripo" : "meshy") : _provider;

        private string SelectedProviderLabel => EffectiveProvider == "meshy" ? "Meshy" : "Tripo";

        private bool SelectedProviderConfigured
            => EffectiveProvider == "meshy"
                ? _meshyCredentialStatus != null && _meshyCredentialStatus.Configured
                : _credentialStatus != null ? _credentialStatus.Configured : _health != null && _health.Configured;

        private void DrawGeneratePage()
        {
            EditorGUILayout.LabelField("Novo candidato", EditorStyles.boldLabel);
            DrawProviderSelector();
            EditorGUILayout.HelpBox(
                EffectiveProvider == "meshy"
                    ? "Planeja geração Meshy (preview/refine quando necessário) e solicita FBX no próprio resultado. O candidato permanece no vault até validação e quarentena."
                    : "Planeja e executa geração → conversão FBX com um único planHash e limite aprovado. O resultado permanece no vault até você validar e colocá-lo em quarentena.",
                MessageType.Info);
            _generationMode = GUILayout.Toolbar(_generationMode, new[] { "Texto → 3D", "Imagem → 3D" });
            EditorGUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            _preset = (Ai3dGenerationPreset)EditorGUILayout.Popup("Preset", (int)_preset, PresetLabels);
            if (EditorGUI.EndChangeCheck()) ApplyPreset();
            if (EffectiveProvider == "meshy")
                _meshyModel = EditorGUILayout.TextField(
                    new GUIContent("Modelo Meshy", "Use latest, meshy-6 ou meshy-5."), _meshyModel ?? "latest");
            else
                _model = EditorGUILayout.TextField(
                    new GUIContent("Modelo Tripo", "Versão explícita; não é atualizada silenciosamente."), _model);

            if (_generationMode == 0)
            {
                EditorGUILayout.LabelField("Prompt");
                _prompt = EditorGUILayout.TextArea(_prompt ?? string.Empty, GUILayout.MinHeight(76f));
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _imagePath = EditorGUILayout.TextField("Imagem", _imagePath ?? string.Empty);
                    if (GUILayout.Button("…", GUILayout.Width(32f)))
                    {
                        var selected = EditorUtility.OpenFilePanel("Imagem para " + SelectedProviderLabel, string.Empty, "png,jpg,jpeg");
                        if (!string.IsNullOrEmpty(selected) && !string.Equals(selected, _imagePath, StringComparison.Ordinal))
                        {
                            _imagePath = selected;
                        }
                    }
                }
                EditorGUILayout.HelpBox(
                    "A imagem precisa estar sob uma raiz permitida pelo TRIPO_GATEWAY_UPLOAD_ROOTS. O hash é verificado antes do envio.",
                    MessageType.None);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Geometria e materiais", EditorStyles.boldLabel);
            _smartLowPoly = EditorGUILayout.ToggleLeft("Smart Low Poly", _smartLowPoly);
            if (_smartLowPoly)
            {
                var smartMaximum = EffectiveProvider == "meshy" && _generationMode == 1 ? 15000 : 20000;
                _faceLimit = EditorGUILayout.IntSlider("Limite de faces", Mathf.Clamp(_faceLimit, 500, smartMaximum), 500, smartMaximum);
                EditorGUILayout.HelpBox(
                    EffectiveProvider == "meshy"
                        ? "Meshy Smart Topology T2 cria partes separadas e aceita até 15.000 faces em Imagem → 3D. Em Texto → 3D, Low Poly escolhe a topologia automaticamente."
                        : "A API Smart Low Poly aceita no máximo 20.000 faces.",
                    MessageType.None);
            }
            else _faceLimit = EditorGUILayout.IntField("Limite de faces", Mathf.Max(500, _faceLimit));
            _geometryQuality = EditorGUILayout.Popup("Qualidade", _geometryQuality == "detailed" ? 1 : 0,
                new[] { "standard", "detailed" }) == 1 ? "detailed" : "standard";
            _texture = EditorGUILayout.ToggleLeft("Gerar textura", _texture);
            using (new EditorGUI.DisabledScope(!_texture))
                _pbr = EditorGUILayout.ToggleLeft("Materiais PBR", _texture && _pbr);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Orçamento", EditorStyles.boldLabel);
            if (EffectiveProvider == "tripo")
                _estimatedCredits = Mathf.Max(1, EditorGUILayout.IntField("Estimativa da geração", _estimatedCredits));
            _capCredits = Mathf.Max(1, EditorGUILayout.IntField("Limite do workflow", _capCredits));
            EditorGUILayout.HelpBox(
                EffectiveProvider == "meshy"
                    ? "O planner calcula os créditos por etapa Meshy (geração, refine, rig ou animação). FBX já é solicitado na geração; o limite local não é uma trava transacional do provedor."
                    : "O planner reserva 10 créditos estimados e até 15 do limite para converter automaticamente o resultado em FBX. O limite local não é uma trava transacional da Tripo.",
                MessageType.Warning);
            _externalSendApproved = EditorGUILayout.ToggleLeft(
                _generationMode == 0
                    ? "Autorizo enviar este texto para " + SelectedProviderLabel + " e consumir créditos"
                    : "Autorizo enviar esta imagem para " + SelectedProviderLabel + " e consumir créditos",
                _externalSendApproved);

            var canGenerate = !_busy && _externalSendApproved && _health != null && _health.Ready && SelectedProviderConfigured;
            if (_generationMode == 0) canGenerate &= !string.IsNullOrWhiteSpace(_prompt);
            else canGenerate &= !string.IsNullOrWhiteSpace(_imagePath) && File.Exists(_imagePath);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Gerar candidato", GUILayout.Height(36f))) _ = SubmitGenerationAsync();
            }
            if (_health == null || !_health.Ready || !SelectedProviderConfigured)
                EditorGUILayout.HelpBox("Abra Configuração e conecte um gateway com uma credencial " + SelectedProviderLabel + ".", MessageType.Warning);
        }

        private void DrawProcessPage()
        {
            EditorGUILayout.LabelField("Processar modelo existente", EditorStyles.boldLabel);
            DrawProviderSelector();
            EditorGUILayout.HelpBox(
                "O mesmo planner seguro executa apenas operações tipadas. O arquivo é hashado antes do envio; o resultado volta como candidato em quarentena, nunca direto na cena.",
                MessageType.Info);
            _processMode = GUILayout.Toolbar(_processMode, new[] { "Retopologia", "Separar + fechar", "Rig + animação" });
            using (new EditorGUILayout.HorizontalScope())
            {
                _meshPath = EditorGUILayout.TextField("Modelo", _meshPath ?? string.Empty);
                if (GUILayout.Button("…", GUILayout.Width(32f)))
                {
                    var selected = EditorUtility.OpenFilePanel(
                        "Modelo para processamento " + SelectedProviderLabel, string.Empty, "fbx,glb,gltf,obj,stl");
                    if (!string.IsNullOrEmpty(selected)) _meshPath = selected;
                }
            }
            EditorGUILayout.HelpBox(
                "O arquivo precisa estar sob uma raiz permitida por TRIPO_GATEWAY_UPLOAD_ROOTS. Retopologia e separação só podem ser aceitas quando a fonte também está importada sob Assets/; para auditar rig, habilite Read/Write no importer.",
                MessageType.None);

            if (_processMode == 0)
            {
                EditorGUILayout.LabelField("Smart Retopology v2", EditorStyles.boldLabel);
                _quadRetopology = EditorGUILayout.ToggleLeft("Saída em quads", _quadRetopology);
                var maximum = _quadRetopology ? 10000 : 20000;
                _faceLimit = EditorGUILayout.IntSlider("Meta geométrica", Mathf.Clamp(_faceLimit, 500, maximum), 500, maximum);
                _bakeRetopology = EditorGUILayout.ToggleLeft("Bake de textura", _bakeRetopology);
                EditorGUILayout.HelpBox(
                    "Após importar, a ferramenta compara fonte/candidato e abre os dois lado a lado no Scene View. Excesso de triângulos bloqueia a aceitação.",
                    MessageType.Info);
            }
            else if (_processMode == 1)
            {
                EditorGUILayout.LabelField("Segmentação semântica v2", EditorStyles.boldLabel);
                var granularity = Array.IndexOf(new[] { "simple", "balanced", "detailed" }, _segmentationGranularity);
                _segmentationGranularity = new[] { "simple", "balanced", "detailed" }[
                    EditorGUILayout.Popup("Detalhamento", Mathf.Max(0, granularity), new[] { "Simples", "Balanceado", "Detalhado" })];
                _splitByConnectivity = EditorGUILayout.ToggleLeft("Separar componentes desconectados", _splitByConnectivity);
                var completion = _completionMode == "none" ? 0 : _completionMode == "ai_completion" ? 2 : 1;
                completion = EditorGUILayout.Popup("Fechamento", completion,
                    new[] { "Sem fechamento", "Quick cap", "AI completion" });
                _completionMode = completion == 0 ? "none" : completion == 2 ? "ai_completion" : "quick_cap";
                EditorGUILayout.HelpBox(
                    "Se a fonte tiver rig, pesos, bind poses e blend shapes são auditados. Qualquer perda bloqueia a aceitação e direciona para Smart Body Split local.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField("Rig e pacote de animações", EditorStyles.boldLabel);
                if (EffectiveProvider == "meshy")
                {
                    _heightMeters = Mathf.Max(0.1f, EditorGUILayout.FloatField("Altura aproximada (m)", _heightMeters));
                    _meshyAnimationActionIds = EditorGUILayout.TextField(
                        new GUIContent("IDs de animação", "Até 5 action IDs da biblioteca Meshy, separados por vírgula. Vazio gera somente rig e animações básicas."),
                        _meshyAnimationActionIds ?? string.Empty);
                    var fpsValues = new[] { 24, 25, 30, 60 };
                    var fpsLabels = new[] { "24", "25", "30", "60" };
                    _animationFps = fpsValues[EditorGUILayout.Popup(
                        "FPS", Math.Max(0, Array.IndexOf(fpsValues, _animationFps)), fpsLabels)];
                    EditorGUILayout.HelpBox(
                        "Meshy Auto-Rig suporta humanoides bípedes texturizados. GLB externo deve olhar para +Z e ter no máximo 300.000 faces.",
                        MessageType.Info);
                }
                else
                {
                    _rigType = new[] { "auto", "biped", "quadruped", "hexapod", "octopod", "avian", "serpentine", "aquatic" }[
                        EditorGUILayout.Popup("Tipo", Math.Max(0, Array.IndexOf(
                            new[] { "auto", "biped", "quadruped", "hexapod", "octopod", "avian", "serpentine", "aquatic" }, _rigType)),
                            new[] { "Automático (rig-check)", "Bípede", "Quadrúpede", "Hexápode", "Octópode", "Ave", "Serpentino", "Aquático" })];
                    _rigSpec = EditorGUILayout.Popup("Esqueleto", _rigSpec == "tripo" ? 1 : 0,
                        new[] { "Mixamo / Unity", "Tripo" }) == 1 ? "tripo" : "mixamo";
                    _animationPresets = EditorGUILayout.TextField("Animações", _animationPresets ?? string.Empty);
                    _animateInPlace = EditorGUILayout.ToggleLeft("Animações in-place", _animateInPlace);
                    EditorGUILayout.HelpBox(
                        "Sempre executa rig-check antes de gastar com rig. Informe até 5 presets separados por vírgula, por exemplo: preset:idle, preset:walk.",
                        MessageType.Info);
                }
            }

            _processBudgetCap = Mathf.Clamp(
                EditorGUILayout.IntField("Limite do workflow", _processBudgetCap), 1,
                Ai3dGatewaySettings.MaxCreditsPerWorkflow);
            _externalSendApproved = EditorGUILayout.ToggleLeft(
                "Autorizo enviar esta mesh à " + SelectedProviderLabel + " e consumir até o limite revisado",
                _externalSendApproved);
            var operationSupported = !(EffectiveProvider == "meshy" && _processMode == 1);
            if (!operationSupported)
                EditorGUILayout.HelpBox("Separar + fechar é exclusivo da Tripo. Escolha Tripo ou Automático.", MessageType.Warning);
            var canRun = !_busy && _externalSendApproved && File.Exists(_meshPath ?? string.Empty) &&
                         _health != null && _health.Ready && SelectedProviderConfigured && operationSupported;
            using (new EditorGUI.DisabledScope(!canRun))
                if (GUILayout.Button("Planejar e processar candidato", GUILayout.Height(36f)))
                    _ = SubmitProcessingAsync();
        }

        private void DrawJobsPage()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Tarefas", EditorStyles.boldLabel);
                _autoPoll = GUILayout.Toggle(_autoPoll, "Atualizar automaticamente", GUILayout.Width(170f));
                if (GUILayout.Button("Atualizar", GUILayout.Width(80f))) _ = RefreshJobsAsync(true);
            }
            for (int i = 0; i < _jobs.Count; i++)
            {
                var job = _jobs[i];
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var id = Short(job.JobId, 10);
                    if (GUILayout.Button(id, EditorStyles.miniButton, GUILayout.Width(84f))) _ = SelectJobAsync(job.JobId);
                    EditorGUILayout.LabelField(job.Provider ?? "tripo", GUILayout.Width(52f));
                    EditorGUILayout.LabelField(job.Operation ?? "?", GUILayout.Width(120f));
                    EditorGUILayout.LabelField(job.Status ?? "?", GUILayout.Width(125f));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField((job.Progress * 100f).ToString("F0") + "%", GUILayout.Width(44f));
                }
            }
            if (_jobs.Count == 0) EditorGUILayout.HelpBox("Nenhum job encontrado neste gateway.", MessageType.None);
            EditorGUILayout.Space(8f);
            DrawSelectedJob();
        }

        private void DrawSelectedJob()
        {
            if (_selectedJob == null) return;
            EditorGUILayout.LabelField("Job selecionado", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(_selectedJob.JobId, EditorStyles.miniLabel, GUILayout.Height(18f));
            EditorGUILayout.LabelField("Provedor", _selectedJob.Provider ?? "tripo");
            EditorGUILayout.LabelField("Operação", _selectedJob.Operation ?? string.Empty);
            EditorGUILayout.LabelField("Estado", _selectedJob.Status ?? string.Empty);
            if (!string.IsNullOrEmpty(_activeWorkflowId))
            {
                var workflow = Ai3dWorkflowService.Get(_activeWorkflowId);
                EditorGUILayout.LabelField("Workflow", Short(workflow.WorkflowId, 12) + " · " + workflow.Status);
                EditorGUILayout.LabelField("Orçamento", workflow.CreditsConsumed + " / " + workflow.BudgetCapCredits + " créditos");
            }
            var progressRect = EditorGUILayout.GetControlRect(false, 20f);
            EditorGUI.ProgressBar(progressRect, _selectedJob.Progress, _selectedJob.Message ?? _selectedJob.Status);
            EditorGUILayout.LabelField("Estimativa", _selectedJob.EstimatedCredits + " créditos");
            if (_selectedJob.CreditsConsumed.HasValue)
                EditorGUILayout.LabelField("Consumido", _selectedJob.CreditsConsumed.Value + " créditos");
            if (_selectedJob.Error != null)
                EditorGUILayout.HelpBox(_selectedJob.Error.Code + ": " + _selectedJob.Error.Message, MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Atualizar"))
                {
                    if (!string.IsNullOrEmpty(_activeWorkflowId)) _ = RefreshActiveWorkflowAsync(true);
                    else _ = RefreshSelectedJobAsync(true);
                }
                using (new EditorGUI.DisabledScope(_busy || _selectedJob.IsTerminal))
                    if (GUILayout.Button("Cancelar localmente")) _ = CancelSelectedJobAsync();
            }

            if (string.Equals(_selectedJob.Status, "completed", StringComparison.Ordinal))
            {
                var fbx = _selectedJob.FindFbxArtifact();
                if (fbx == null && !string.IsNullOrEmpty(_selectedJob.ProviderTaskId))
                {
                    if (!string.IsNullOrEmpty(_activeWorkflowId))
                        EditorGUILayout.HelpBox(
                            "O workflow iniciará a conversão FBX automaticamente dentro do limite já aprovado.",
                            MessageType.Info);
                    else
                    {
                        EditorGUILayout.HelpBox("O resultado ainda não é FBX. A conversão é um novo job faturável.", MessageType.Info);
                        using (new EditorGUI.DisabledScope(_busy || !_externalSendApproved))
                            if (GUILayout.Button("Converter resultado para FBX")) _ = ConvertSelectedToFbxAsync();
                        _externalSendApproved = EditorGUILayout.ToggleLeft(
                            "Autorizo a conversão e até 15 créditos", _externalSendApproved);
                    }
                }
                else if (fbx != null)
                {
                    EditorGUILayout.HelpBox("FBX baixado e hashado no vault. Valide antes da quarentena Unity.", MessageType.Info);
                    if (GUILayout.Button("Validar FBX")) ValidateSelectedArtifact();
                }
            }
        }

        private void DrawCandidatePage()
        {
            EditorGUILayout.LabelField("Revisão do candidato", EditorStyles.boldLabel);
            var workflow = TryGetActiveWorkflow();
            if (workflow?.QualityReport != null) DrawWorkflowQuality(workflow);
            if (_artifactValidation == null)
            {
                EditorGUILayout.HelpBox("Selecione um job concluído com FBX e clique em Validar FBX.", MessageType.Info);
                return;
            }
            DrawArtifactValidation();
            if (_artifactValidation.IsValid && _staged == null)
            {
                if (GUILayout.Button("Colocar na quarentena Unity", GUILayout.Height(32f)))
                {
                    if (!string.IsNullOrEmpty(_activeWorkflowId)) _ = StageWorkflowCandidateAsync();
                    else StageCandidate();
                }
            }
            if (_staged != null) DrawStagedCandidate();
        }

        private void DrawArtifactValidation()
        {
            EditorGUILayout.LabelField("Artifact vault", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Arquivo", _artifactValidation.Artifact?.LogicalName ?? string.Empty);
            EditorGUILayout.SelectableLabel(_artifactValidation.AbsolutePath ?? string.Empty, EditorStyles.miniLabel, GUILayout.Height(18f));
            EditorGUILayout.LabelField("SHA-256", Short(_artifactValidation.CandidateHash, 20));
            EditorGUILayout.LabelField("Gate", _artifactValidation.IsValid ? "Aprovado" : "Bloqueado");
            DrawIssues(_artifactValidation.Issues);
        }

        private void DrawStagedCandidate()
        {
            var workflow = TryGetActiveWorkflow();
            var qualityAllowed = workflow?.QualityReport == null
                                 || (bool?)workflow.QualityReport["acceptAllowed"] != false;
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Quarentena Unity", EditorStyles.boldLabel);
            EditorGUILayout.ObjectField("Asset", _staged.Asset, typeof(GameObject), false);
            EditorGUILayout.LabelField("Meshes", _staged.MeshCount.ToString("N0"));
            EditorGUILayout.LabelField("Vértices", _staged.VertexCount.ToString("N0"));
            EditorGUILayout.LabelField("Triângulos", _staged.TriangleCount.ToString("N0"));
            EditorGUILayout.LabelField("Validation hash", Short(_staged.ValidationHash, 20));
            DrawIssues(_staged.Issues);

            var preview = AssetPreview.GetAssetPreview(_staged.Asset) ?? AssetPreview.GetMiniThumbnail(_staged.Asset);
            if (preview != null)
            {
                var rect = GUILayoutUtility.GetRect(200f, 200f, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit, true);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview no Scene View"))
                {
                    _reviewedPreviewHash = null;
                    if (!string.IsNullOrEmpty(_activeWorkflowId))
                    {
                        var previewWorkflow = Ai3dWorkflowService.PreviewCandidate(
                            _activeWorkflowId, _activeWorkflowPlanHash);
                        _currentPreviewHash = previewWorkflow.PreviewHash;
                    }
                    else
                    {
                        Ai3dScenePreviewController.Show(_staged.Asset, Short(_staged.JobId, 10));
                        _currentPreviewHash = Ai3dCandidateService.ComputePreviewHash(
                            _staged.Asset, _staged.ValidationHash);
                    }
                }
                if (GUILayout.Button("Localizar asset")) EditorGUIUtility.PingObject(_staged.Asset);
            }
            var previewIsCurrent = !string.IsNullOrEmpty(_reviewedPreviewHash)
                                   && string.Equals(_reviewedPreviewHash, _currentPreviewHash, StringComparison.Ordinal);
            using (new EditorGUI.DisabledScope(
                       Ai3dScenePreviewController.ActiveAsset != _staged.Asset || string.IsNullOrEmpty(_currentPreviewHash)))
            {
                if (GUILayout.Button(previewIsCurrent ? "Preview revisado ✓" : "Marcar preview como revisado"))
                {
                    _reviewedPreviewHash = _currentPreviewHash;
                    SetStatus("Preview atual vinculado ao validation hash.", MessageType.Info);
                }
            }
            if (!previewIsCurrent)
                EditorGUILayout.HelpBox("Abra e revise o candidato no Scene View antes de aceitá-lo.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!_staged.IsValid || !qualityAllowed || !previewIsCurrent || _busy))
            {
                if (GUILayout.Button("Aceitar como asset do projeto", GUILayout.Height(34f))) AcceptCandidate();
            }
            if (!qualityAllowed)
                EditorGUILayout.HelpBox(
                    "A aceitação permanece bloqueada pelo relatório de qualidade. Use o preview para diagnosticar ou rejeite o candidato.",
                    MessageType.Error);
            if (!string.IsNullOrEmpty(_activeWorkflowId)
                && GUILayout.Button("Rejeitar candidato (preservar quarentena)")) RejectWorkflowCandidate();
            if (!string.IsNullOrEmpty(_acceptedAssetPath))
            {
                EditorGUILayout.HelpBox("Aceito em " + _acceptedAssetPath, MessageType.Info);
                if (GUILayout.Button("Adicionar explicitamente à cena")) InstantiateAccepted();
            }
        }

        private void DrawSettingsPage()
        {
            EditorGUILayout.LabelField("Gateway local", EditorStyles.boldLabel);
            _gatewayUrl = EditorGUILayout.TextField("URL", _gatewayUrl ?? string.Empty);
            _gatewayProcess = Ai3dGatewayProcess.Inspect(_gatewayUrl);
            DrawGatewayProcessControls();
            using (new EditorGUILayout.HorizontalScope())
            {
                _gatewayToken = _showGatewayToken
                    ? EditorGUILayout.TextField("Token local", _gatewayToken ?? string.Empty)
                    : EditorGUILayout.PasswordField("Token local", _gatewayToken ?? string.Empty);
                _showGatewayToken = GUILayout.Toggle(_showGatewayToken, "Ver", GUILayout.Width(44f));
            }
            if (GUILayout.Button("Gerar token local forte"))
            {
                _gatewayToken = Ai3dGatewaySettings.GenerateGatewayToken();
                _status = "Token gerado. Configure o mesmo valor em TRIPO_GATEWAY_TOKEN antes de iniciar o gateway.";
                _statusType = MessageType.Warning;
            }
            EditorGUILayout.HelpBox(
                "A API key nunca é persistida pela Unity. Ela é enviada uma vez ao gateway autenticado e armazenada pelo sistema operacional.",
                MessageType.Info);
            _timeoutSeconds = EditorGUILayout.IntSlider("Timeout HTTP", _timeoutSeconds, 1, 120);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Automação MCP", EditorStyles.boldLabel);
            Settings.AllowExternalServiceTools = EditorGUILayout.ToggleLeft(
                "Permitir workflows pagos via MCP", Settings.AllowExternalServiceTools);
            _maxWorkflowCredits = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("Limite por workflow", "Teto local absoluto que o planner MCP não pode ultrapassar."),
                _maxWorkflowCredits));
            EditorGUILayout.HelpBox(
                "Desligado por padrão. Quando ligado, cada início/cancelamento remoto ainda exige confirmação MCP de uso único, consentimento dos dados enviados e um planHash atual.",
                MessageType.Warning);
            EditorGUILayout.Space(8f);
            DrawProviderCredentialSettings("tripo", _credentialStatus);
            EditorGUILayout.Space(6f);
            DrawProviderCredentialSettings("meshy", _meshyCredentialStatus);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Assets", EditorStyles.boldLabel);
            _quarantineRoot = EditorGUILayout.TextField("Quarentena", _quarantineRoot ?? string.Empty);
            _acceptedRoot = EditorGUILayout.TextField("Aceitos", _acceptedRoot ?? string.Empty);
            _model = EditorGUILayout.TextField("Modelo padrão", _model ?? string.Empty);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Salvar"))
                {
                    try
                    {
                        SaveSettings();
                        SetStatus("Configuração salva.", MessageType.Info);
                    }
                    catch (Exception exception) { SetException(exception); }
                }
                if (GUILayout.Button("Testar conexão")) _ = TestConnectionAsync(true);
                using (new EditorGUI.DisabledScope(_health == null || !_health.Ready || !SelectedProviderConfigured))
                    if (GUILayout.Button("Consultar saldo")) _ = RefreshBalanceAsync();
            }
            if (_health != null)
            {
                EditorGUILayout.LabelField("Serviço", _health.Service ?? string.Empty);
                EditorGUILayout.LabelField("Versão", _health.Version ?? string.Empty);
                EditorGUILayout.LabelField("Gateway", _health.Ready ? "pronto" : "offline");
                EditorGUILayout.LabelField("Tripo API", _credentialStatus?.Configured == true ? "configurada" : "não configurada");
                EditorGUILayout.LabelField("Meshy API", _meshyCredentialStatus?.Configured == true ? "configurada" : "não configurada");
            }
            if (_balance != null)
            {
                EditorGUILayout.LabelField("Saldo disponível", ((int?)_balance["balance"] ?? 0).ToString("N0"));
                EditorGUILayout.LabelField("Créditos congelados", ((int?)_balance["frozen"] ?? 0).ToString("N0"));
            }
        }

        private void DrawGatewayProcessControls()
        {
            var process = _gatewayProcess ?? Ai3dGatewayProcess.Inspect(_gatewayUrl);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_busy || !process.CanStart))
                    if (GUILayout.Button("▶ Iniciar", GUILayout.Height(26f))) _ = StartGatewayAsync(false);
                using (new EditorGUI.DisabledScope(_busy || !process.CanStop))
                    if (GUILayout.Button("■ Parar", GUILayout.Height(26f))) _ = StopGatewayAsync();
                using (new EditorGUI.DisabledScope(_busy || process.IsExternal))
                    if (GUILayout.Button("↻ Reiniciar", GUILayout.Height(26f))) _ = StartGatewayAsync(true);
                if (GUILayout.Button("Logs", GUILayout.Width(52f), GUILayout.Height(26f)))
                    Ai3dGatewayProcess.RevealLogs();
            }

            var stateLabel = process.State == Ai3dGatewayProcessState.Running ? "rodando"
                : process.State == Ai3dGatewayProcessState.Starting ? "iniciando"
                : process.State == Ai3dGatewayProcessState.External ? "externo"
                : process.State == Ai3dGatewayProcessState.Failed ? "falhou"
                : "parado";
            EditorGUILayout.LabelField(
                "Processo: " + stateLabel + (process.ProcessId > 0 ? " · PID " + process.ProcessId : string.Empty),
                EditorStyles.miniLabel);
            if (!string.IsNullOrWhiteSpace(process.Version))
                EditorGUILayout.LabelField("Versão do gateway: " + process.Version, EditorStyles.miniLabel);
            if (!string.IsNullOrWhiteSpace(process.Message))
                EditorGUILayout.LabelField(process.Message, EditorStyles.miniLabel);
            if (process.IsExternal)
                EditorGUILayout.HelpBox(
                    "Já existe um gateway saudável nessa URL. Ele pode ser usado, mas só deve ser parado pelo programa que o iniciou.",
                    MessageType.Info);
            if (!string.IsNullOrWhiteSpace(process.Error))
                EditorGUILayout.HelpBox(process.Error, MessageType.Error);
        }

        private async Task StartGatewayAsync(bool restart)
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                if (string.IsNullOrWhiteSpace(_gatewayToken))
                    _gatewayToken = Ai3dGatewaySettings.GenerateGatewayToken();
                SaveSettings();
                var options = Ai3dGatewayProcess.CreateLaunchOptions(
                    _gatewayUrl, _gatewayToken, _maxWorkflowCredits);
                await Task.Run(() =>
                {
                    if (restart) Ai3dGatewayProcess.Restart(options);
                    else Ai3dGatewayProcess.Start(options);
                });
                _gatewayProcess = Ai3dGatewayProcess.Inspect(_gatewayUrl, true);
                _health = await Ai3dGatewayClient.HealthAsync(Token);
                _capabilities = await Ai3dGatewayClient.GetCapabilitiesAsync(Token);
                _credentialStatus = await Ai3dGatewayClient.GetCredentialStatusAsync("tripo", Token);
                _meshyCredentialStatus = await Ai3dGatewayClient.GetCredentialStatusAsync("meshy", Token);
                SetStatus(restart ? "Gateway reiniciado e conectado." : "Gateway iniciado e conectado.", MessageType.Info);
            }
            catch (Exception exception) { SetException(exception); }
            finally
            {
                _gatewayProcess = Ai3dGatewayProcess.Inspect(_gatewayUrl, true);
                SetBusy(false);
            }
        }

        private async Task StopGatewayAsync()
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                await Task.Run(Ai3dGatewayProcess.Stop);
                _health = null;
                _capabilities = null;
                _credentialStatus = null;
                _meshyCredentialStatus = null;
                _balance = null;
                _gatewayProcess = Ai3dGatewayProcess.Inspect(_gatewayUrl, true);
                SetStatus("Gateway parado. Jobs remotos já enviados não são cancelados automaticamente.", MessageType.Warning);
            }
            catch (Exception exception) { SetException(exception); }
            finally
            {
                _gatewayProcess = Ai3dGatewayProcess.Inspect(_gatewayUrl, true);
                SetBusy(false);
            }
        }

        private void DrawProviderCredentialSettings(string provider, Ai3dCredentialStatus status)
        {
            var isMeshy = provider == "meshy";
            var label = isMeshy ? "Meshy" : "Tripo";
            EditorGUILayout.LabelField("Credencial " + label, EditorStyles.boldLabel);
            if (status != null)
            {
                EditorGUILayout.LabelField("Estado", status.Configured ? "configurada" : "não configurada");
                EditorGUILayout.LabelField("Origem", CredentialSourceLabel(status.Source, provider));
                EditorGUILayout.LabelField("Armazenamento", status.Store ?? "indisponível");
                if (!status.StoreAvailable)
                    EditorGUILayout.HelpBox(
                        "O armazenamento seguro não está disponível neste sistema. Ainda é possível iniciar o gateway com " +
                        (isMeshy ? "MESHY_API_KEY." : "TRIPO_API_KEY."),
                        MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Conecte o gateway para consultar o armazenamento seguro da credencial.", MessageType.None);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var input = isMeshy ? _meshyApiKeyInput : _tripoApiKeyInput;
                var show = isMeshy ? _showMeshyApiKey : _showTripoApiKey;
                input = show
                    ? EditorGUILayout.TextField("Nova API key", input ?? string.Empty)
                    : EditorGUILayout.PasswordField("Nova API key", input ?? string.Empty);
                show = GUILayout.Toggle(show, "Ver", GUILayout.Width(44f));
                if (isMeshy) { _meshyApiKeyInput = input; _showMeshyApiKey = show; }
                else { _tripoApiKeyInput = input; _showTripoApiKey = show; }
            }
            var providerInput = isMeshy ? _meshyApiKeyInput : _tripoApiKeyInput;
            var canStore = !_busy
                           && status != null
                           && status.StoreAvailable
                           && !string.IsNullOrWhiteSpace(providerInput)
                           && providerInput.StartsWith(isMeshy ? "msy_" : "tsk_", StringComparison.Ordinal);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!canStore))
                    if (GUILayout.Button("Salvar com segurança")) _ = SaveProviderApiKeyAsync(provider);
                using (new EditorGUI.DisabledScope(
                           _busy || status == null || !status.Configured))
                    if (GUILayout.Button("Testar credencial")) _ = TestProviderCredentialAsync(provider);
                using (new EditorGUI.DisabledScope(
                           _busy || status == null
                           || !string.Equals(status.Source, "windows_credential_manager", StringComparison.Ordinal)))
                    if (GUILayout.Button("Remover")) _ = DeleteProviderApiKeyAsync(provider);
            }
        }

        private void ApplyPreset()
        {
            switch (_preset)
            {
                case Ai3dGenerationPreset.GameMobile:
                    _faceLimit = 15000; _smartLowPoly = true; _geometryQuality = "standard"; _estimatedCredits = 100; _capCredits = 150;
                    break;
                case Ai3dGenerationPreset.GamePc:
                    _faceLimit = 50000; _smartLowPoly = false; _geometryQuality = "standard"; _estimatedCredits = 120; _capCredits = 180;
                    break;
                case Ai3dGenerationPreset.HeroProp:
                    _faceLimit = 80000; _smartLowPoly = false; _geometryQuality = "detailed"; _estimatedCredits = 160; _capCredits = 240;
                    break;
                default:
                    _faceLimit = 50000; _smartLowPoly = false; _geometryQuality = "detailed"; _estimatedCredits = 160; _capCredits = 240;
                    break;
            }
            _texture = true;
            _pbr = true;
        }

        private static string PresetId(Ai3dGenerationPreset preset)
        {
            switch (preset)
            {
                case Ai3dGenerationPreset.GameMobile: return "game-mobile";
                case Ai3dGenerationPreset.HeroProp: return "hero-prop";
                case Ai3dGenerationPreset.CharacterHumanoid: return "character-humanoid";
                default: return "game-pc";
            }
        }

        private async Task SubmitGenerationAsync()
        {
            SetBusy(true);
            try
            {
                var sourceKind = _generationMode == 0 ? "text" : "image";
                var planArguments = new JObject
                {
                    ["provider"] = _provider,
                    ["sourceKind"] = sourceKind,
                    ["preset"] = PresetId(_preset),
                    ["modelVersion"] = EffectiveProvider == "meshy" ? _meshyModel : _model,
                    ["autoConvertToFbx"] = true,
                    ["faceLimit"] = _faceLimit,
                    ["texture"] = _texture,
                    ["pbr"] = _pbr,
                    ["smartLowPoly"] = _smartLowPoly,
                    ["geometryQuality"] = _geometryQuality,
                    ["estimatedCredits"] = _estimatedCredits,
                    ["budgetCapCredits"] = _capCredits,
                };
                if (_generationMode == 0) planArguments["prompt"] = _prompt;
                else planArguments["imagePath"] = Path.GetFullPath(_imagePath);
                var plan = Ai3dWorkflowService.Plan(planArguments);
                var subject = _generationMode == 0 ? "o prompt" : "a imagem";
                if (!EditorUtility.DisplayDialog(
                        "Executar workflow " + (plan.Provider == "meshy" ? "Meshy" : "Tripo"),
                        $"Enviar {subject}, gerar o modelo e converter automaticamente para FBX?\n\n" +
                        $"Estimativa: {plan.EstimatedCredits} créditos\nLimite aprovado: {plan.BudgetCapCredits} créditos\n\n" +
                        "O resultado ficará no vault até a validação e quarentena.",
                        "Enviar e executar", "Cancelar")) return;
                var workflow = await Ai3dWorkflowService.StartAsync(
                    plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true,
                    new[] { sourceKind }, Token);
                _activeWorkflowId = workflow.WorkflowId;
                _activeWorkflowPlanHash = workflow.PlanHash;
                Ai3dGatewaySettings.LastWorkflowId = workflow.WorkflowId;
                _selectedJob = await Ai3dGatewayClient.GetJobAsync(workflow.CurrentJobId, Token);
                _externalSendApproved = false;
                _artifactValidation = null;
                _staged = null;
                _reviewedPreviewHash = null;
                _currentPreviewHash = null;
                _acceptedAssetPath = null;
                _page = 2;
                SetStatus(
                    "Workflow " + (plan.Provider == "meshy" ? "Meshy" : "Tripo") +
                    " criado com planHash e limite aprovado; o resultado permanece fora da cena.",
                    MessageType.Info);
                await RefreshJobsAsync(false);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private async Task SubmitProcessingAsync()
        {
            SetBusy(true);
            try
            {
                var workflowKind = _processMode == 0 ? "retopology"
                    : _processMode == 1 ? "segment_complete" : "rig_animation";
                var preset = _processMode == 0 ? "hero-prop"
                    : _processMode == 1 ? "modular-character" : "animation-pack";
                var arguments = new JObject
                {
                    ["provider"] = _provider,
                    ["workflowKind"] = workflowKind,
                    ["sourceKind"] = "mesh",
                    ["meshPath"] = Path.GetFullPath(_meshPath),
                    ["preset"] = preset,
                    ["autoConvertToFbx"] = true,
                    ["budgetCapCredits"] = _processBudgetCap,
                };
                if (_processMode == 0)
                {
                    arguments["retopologyModel"] = "v2.0";
                    arguments["faceLimit"] = _faceLimit;
                    arguments["quad"] = _quadRetopology;
                    arguments["bake"] = _bakeRetopology;
                }
                else if (_processMode == 1)
                {
                    arguments["segmentationModel"] = "v2.0-20260430";
                    arguments["segmentationGranularity"] = _segmentationGranularity;
                    arguments["splitByConnectivity"] = _splitByConnectivity;
                    arguments["completionMode"] = _completionMode;
                }
                else
                {
                    if (EffectiveProvider == "meshy")
                    {
                        arguments["heightMeters"] = _heightMeters;
                        arguments["animationFps"] = _animationFps;
                        var actionIds = new JArray();
                        foreach (var value in (_meshyAnimationActionIds ?? string.Empty).Split(','))
                        {
                            var trimmed = value.Trim();
                            if (trimmed.Length == 0) continue;
                            if (!int.TryParse(trimmed, out var actionId) || actionId <= 0)
                                throw new Ai3dWorkflowException(
                                    "AI3D_ANIMATION_ACTION_INVALID", "IDs de animação Meshy devem ser inteiros positivos.");
                            if (!actionIds.Any(token => (int)token == actionId)) actionIds.Add(actionId);
                        }
                        arguments["animationActionIds"] = actionIds;
                    }
                    else
                    {
                        arguments["rigType"] = _rigType;
                        arguments["rigSpec"] = _rigSpec;
                        arguments["animateInPlace"] = _animateInPlace;
                        var animations = new JArray();
                        foreach (var value in (_animationPresets ?? string.Empty).Split(','))
                        {
                            var trimmed = value.Trim();
                            if (trimmed.Length > 0 && !animations.Any(token =>
                                    string.Equals((string)token, trimmed, StringComparison.Ordinal)))
                                animations.Add(trimmed);
                        }
                        arguments["animations"] = animations;
                    }
                }
                var plan = Ai3dWorkflowService.Plan(arguments);
                if (!EditorUtility.DisplayDialog(
                        "Executar processamento " + (plan.Provider == "meshy" ? "Meshy" : "Tripo"),
                        $"Workflow: {workflowKind}\nNós: {string.Join(" → ", plan.Nodes.Select(value => value.Id))}\n\n" +
                        $"Estimativa: {plan.EstimatedCredits} créditos\nLimite aprovado: {plan.BudgetCapCredits} créditos\n\n" +
                        "O resultado será validado localmente e ficará fora da cena.",
                        "Enviar e executar", "Cancelar")) return;
                var workflow = await Ai3dWorkflowService.StartAsync(
                    plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true,
                    new[] { "mesh" }, Token);
                _activeWorkflowId = workflow.WorkflowId;
                _activeWorkflowPlanHash = workflow.PlanHash;
                Ai3dGatewaySettings.LastWorkflowId = workflow.WorkflowId;
                _selectedJob = await Ai3dGatewayClient.GetJobAsync(workflow.CurrentJobId, Token);
                _externalSendApproved = false;
                _artifactValidation = null;
                _staged = null;
                _reviewedPreviewHash = null;
                _currentPreviewHash = null;
                _acceptedAssetPath = null;
                _page = 2;
                SetStatus(
                    "Workflow tipado iniciado. Cada nó usa o mesmo planHash, idempotência própria e o limite aprovado.",
                    MessageType.Info);
                await RefreshJobsAsync(false);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private async Task ConvertSelectedToFbxAsync()
        {
            if (_selectedJob == null || string.IsNullOrEmpty(_selectedJob.ProviderTaskId)) return;
            if (!EditorUtility.DisplayDialog(
                    "Converter para FBX",
                    "Criar um novo job " + (_selectedJob.Provider == "meshy" ? "Meshy" : "Tripo") +
                    " de conversão, autorizado a consumir até " + (_selectedJob.Provider == "meshy" ? "2" : "15") + " créditos?",
                    "Converter", "Cancelar")) return;
            SetBusy(true);
            try
            {
                if (string.IsNullOrEmpty(_pendingConvertKey)) _pendingConvertKey = Guid.NewGuid().ToString("N");
                var meshy = _selectedJob.Provider == "meshy";
                _selectedJob = await Ai3dGatewayClient.CreateConvertToFbxJobAsync(
                    _selectedJob.ProviderTaskId, meshy ? 1 : 10, meshy ? 2 : 15,
                    _pendingConvertKey, meshy ? "meshy" : "tripo", Token);
                _pendingConvertKey = null;
                _externalSendApproved = false;
                _artifactValidation = null;
                _staged = null;
                _reviewedPreviewHash = null;
                _currentPreviewHash = null;
                SetStatus("Conversão FBX criada.", MessageType.Info);
                await RefreshJobsAsync(false);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private async Task TestConnectionAsync(bool showSuccess)
        {
            SetBusy(true);
            try
            {
                SaveSettings();
                _health = await Ai3dGatewayClient.HealthAsync(Token);
                _capabilities = null;
                _credentialStatus = null;
                _meshyCredentialStatus = null;
                if (_health.Ready && !string.IsNullOrEmpty(Ai3dGatewaySettings.GatewayToken))
                {
                    _capabilities = await Ai3dGatewayClient.GetCapabilitiesAsync(Token);
                    _credentialStatus = await Ai3dGatewayClient.GetCredentialStatusAsync("tripo", Token);
                    _meshyCredentialStatus = await Ai3dGatewayClient.GetCredentialStatusAsync("meshy", Token);
                }
                if (showSuccess)
                    SetStatus(SelectedProviderConfigured
                        ? "Gateway conectado e credencial " + SelectedProviderLabel + " configurada."
                        : "Gateway conectado, mas a credencial " + SelectedProviderLabel + " não está configurada.",
                        SelectedProviderConfigured ? MessageType.Info : MessageType.Warning);
            }
            catch (Exception exception)
            {
                _health = null;
                _capabilities = null;
                _credentialStatus = null;
                _meshyCredentialStatus = null;
                if (showSuccess) SetException(exception);
            }
            finally { SetBusy(false); }
        }

        private async Task RefreshBalanceAsync()
        {
            SetBusy(true);
            try
            {
                _balance = await Ai3dGatewayClient.GetBalanceAsync(EffectiveProvider, Token);
                SetStatus("Saldo atualizado sem consumir créditos.", MessageType.Info);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private async Task SaveProviderApiKeyAsync(string provider)
        {
            var isMeshy = provider == "meshy";
            var input = isMeshy ? _meshyApiKeyInput : _tripoApiKeyInput;
            var label = isMeshy ? "Meshy" : "Tripo";
            if (string.IsNullOrWhiteSpace(input)) return;
            if (!EditorUtility.DisplayDialog(
                    "Salvar credencial " + label,
                    "Enviar esta key uma única vez ao gateway local e armazená-la no Windows Credential Manager? Ela não será salva em Assets ou EditorPrefs.",
                    "Salvar com segurança", "Cancelar")) return;
            var value = input;
            var stored = false;
            SetBusy(true);
            try
            {
                var status = await Ai3dGatewayClient.SetProviderApiKeyAsync(provider, value, Token);
                if (isMeshy) _meshyCredentialStatus = status;
                else _credentialStatus = status;
                stored = true;
                _health = await Ai3dGatewayClient.HealthAsync(Token);
                _balance = await Ai3dGatewayClient.GetBalanceAsync(provider, Token);
                SetStatus("Credencial salva no Windows Credential Manager e validada pela " + label + ".", MessageType.Info);
            }
            catch (Exception exception)
            {
                if (!stored) SetException(exception);
                else
                {
                    var root = exception.GetBaseException();
                    var code = root is Ai3dGatewayException gateway ? gateway.Code + ": " : string.Empty;
                    SetStatus(
                        "Credencial armazenada, mas o teste com a " + label + " falhou: " + code + root.Message,
                        MessageType.Warning);
                }
            }
            finally
            {
                if (isMeshy) _meshyApiKeyInput = null;
                else _tripoApiKeyInput = null;
                value = null;
                SetBusy(false);
            }
        }

        private async Task TestProviderCredentialAsync(string provider)
        {
            SetBusy(true);
            try
            {
                var status = await Ai3dGatewayClient.GetCredentialStatusAsync(provider, Token);
                if (provider == "meshy") _meshyCredentialStatus = status;
                else _credentialStatus = status;
                _balance = await Ai3dGatewayClient.GetBalanceAsync(provider, Token);
                _health = await Ai3dGatewayClient.HealthAsync(Token);
                SetStatus("Credencial válida; saldo consultado sem consumir créditos.", MessageType.Info);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private async Task DeleteProviderApiKeyAsync(string provider)
        {
            var label = provider == "meshy" ? "Meshy" : "Tripo";
            if (!EditorUtility.DisplayDialog(
                    "Remover credencial " + label,
                    "Remover a key persistida do Windows Credential Manager? Jobs remotos em andamento podem falhar no próximo polling.",
                    "Remover", "Cancelar")) return;
            SetBusy(true);
            try
            {
                var status = await Ai3dGatewayClient.DeleteProviderApiKeyAsync(provider, Token);
                if (provider == "meshy") _meshyCredentialStatus = status;
                else _credentialStatus = status;
                _health = await Ai3dGatewayClient.HealthAsync(Token);
                _balance = null;
                SetStatus(status.Configured
                    ? "Credencial persistida removida; a variável de ambiente do provedor continua ativa."
                    : "Credencial " + label + " removida.",
                    status.Configured ? MessageType.Warning : MessageType.Info);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private async Task RefreshJobsAsync(bool showErrors)
        {
            if (string.IsNullOrWhiteSpace(Ai3dGatewaySettings.GatewayToken)) return;
            try
            {
                var jobs = await Ai3dGatewayClient.ListJobsAsync(30, Token);
                _jobs.Clear();
                _jobs.AddRange(jobs);
                Repaint();
            }
            catch (Exception exception) { if (showErrors) SetException(exception); }
        }

        private async Task SelectJobAsync(string jobId)
        {
            _activeWorkflowId = null;
            _activeWorkflowPlanHash = null;
            Ai3dGatewaySettings.LastWorkflowId = string.Empty;
            _selectedJob = await Ai3dGatewayClient.GetJobAsync(jobId, Token);
            Ai3dGatewaySettings.LastJobId = jobId;
            _artifactValidation = null;
            _staged = null;
            _reviewedPreviewHash = null;
            _currentPreviewHash = null;
            _acceptedAssetPath = null;
            _page = 2;
            Repaint();
        }

        private async Task RefreshSelectedJobAsync(bool showErrors)
        {
            if (_selectedJob == null || _polling) return;
            _polling = true;
            try
            {
                _selectedJob = await Ai3dGatewayClient.GetJobAsync(_selectedJob.JobId, Token);
                if (_selectedJob.IsTerminal) await RefreshJobsAsync(false);
                Repaint();
            }
            catch (Exception exception) { if (showErrors) SetException(exception); }
            finally { _polling = false; }
        }

        private async Task RefreshActiveWorkflowAsync(bool showErrors)
        {
            if (string.IsNullOrEmpty(_activeWorkflowId) || _polling) return;
            _polling = true;
            try
            {
                var workflow = Ai3dWorkflowService.Get(_activeWorkflowId);
                if (!string.IsNullOrEmpty(workflow.CurrentJobId)
                    && (_selectedJob == null
                        || !string.Equals(_selectedJob.JobId, workflow.CurrentJobId, StringComparison.Ordinal)
                        || !_selectedJob.IsTerminal))
                    _selectedJob = await Ai3dGatewayClient.GetJobAsync(workflow.CurrentJobId, Token);
                if (!string.Equals(workflow.Status, "starting", StringComparison.Ordinal)
                    && !(workflow.Status ?? string.Empty).StartsWith("running_", StringComparison.Ordinal)
                    && !string.Equals(workflow.Status, "cancel_requested", StringComparison.Ordinal))
                    await RefreshJobsAsync(false);
                Repaint();
            }
            catch (Exception exception) { if (showErrors) SetException(exception); }
            finally { _polling = false; }
        }

        private async Task CancelSelectedJobAsync()
        {
            if (_selectedJob == null) return;
            if (!EditorUtility.DisplayDialog(
                    "Cancelar localmente",
                    "Se a tarefa já foi enviada, a Tripo pode continuar e consumir créditos. O gateway apenas deixa de baixar os artifacts.",
                    "Solicitar cancelamento", "Voltar")) return;
            SetBusy(true);
            try
            {
                if (!string.IsNullOrEmpty(_activeWorkflowId))
                {
                    var workflow = await Ai3dWorkflowService.CancelAsync(
                        _activeWorkflowId, _activeWorkflowPlanHash, Token);
                    _selectedJob = await Ai3dGatewayClient.GetJobAsync(workflow.CurrentJobId, Token);
                }
                else _selectedJob = await Ai3dGatewayClient.CancelJobAsync(_selectedJob.JobId, Token);
                SetStatus("Cancelamento local solicitado; a reconciliação remota continua.", MessageType.Warning);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private void ValidateSelectedArtifact()
        {
            try
            {
                _artifactValidation = Ai3dCandidateService.ValidateArtifact(
                    _selectedJob,
                    _selectedJob?.FindFbxArtifact(),
                    (string)_capabilities?["artifactVaultRoot"]);
                _staged = null;
                _reviewedPreviewHash = null;
                _currentPreviewHash = null;
                _acceptedAssetPath = null;
                _page = 3;
                SetStatus(_artifactValidation.IsValid
                    ? "Artifact aprovado para a quarentena Unity."
                    : "Artifact bloqueado; veja os erros de validação.",
                    _artifactValidation.IsValid ? MessageType.Info : MessageType.Error);
            }
            catch (Exception exception) { SetException(exception); }
        }

        private void StageCandidate()
        {
            if (!EditorUtility.DisplayDialog(
                    "Quarentena Unity",
                    "Copiar o FBX validado para Assets/ em uma área isolada e aplicar configurações de importação seguras?",
                    "Colocar em quarentena", "Cancelar")) return;
            try
            {
                _staged = Ai3dCandidateService.Stage(_artifactValidation, _quarantineRoot);
                _reviewedPreviewHash = null;
                _currentPreviewHash = Ai3dCandidateService.ComputePreviewHash(
                    _staged.Asset, _staged.ValidationHash);
                SetStatus(_staged.IsValid
                    ? "Candidato importado e validado na quarentena."
                    : "Candidato importado, mas bloqueado pelos validadores.",
                    _staged.IsValid ? MessageType.Info : MessageType.Error);
                if (_staged.Asset != null) Ai3dScenePreviewController.Show(_staged.Asset, Short(_staged.JobId, 10));
            }
            catch (Exception exception)
            {
                _reviewedPreviewHash = null;
                _currentPreviewHash = Ai3dCandidateService.ComputePreviewHash(
                    _staged?.Asset, _staged?.ValidationHash);
                SetException(exception);
            }
        }

        private async Task StageWorkflowCandidateAsync()
        {
            if (!EditorUtility.DisplayDialog(
                    "Quarentena Unity",
                    "Revalidar o FBX pelo workflow, copiar somente o artifact autenticado para Assets/ e aplicar configurações de importação seguras?",
                    "Validar e colocar em quarentena", "Cancelar")) return;
            SetBusy(true);
            try
            {
                var workflow = await Ai3dWorkflowService.ValidateCandidateAsync(
                    _activeWorkflowId, _activeWorkflowPlanHash, Token, _quarantineRoot);
                _staged = Ai3dCandidateService.RevalidateStaged(
                    workflow.StagedAssetPath, workflow.CurrentJobId, workflow.ProviderTaskId,
                    workflow.PlanHash, workflow.CandidateHash);
                _reviewedPreviewHash = null;
                _currentPreviewHash = Ai3dCandidateService.ComputePreviewHash(
                    _staged.Asset, _staged.ValidationHash);
                SetStatus("Candidato validado pelo workflow e importado na quarentena; a cena não foi alterada.", MessageType.Info);
                if (_staged.Asset != null) Ai3dScenePreviewController.Show(_staged.Asset, Short(_staged.JobId, 10));
            }
            catch (Exception exception)
            {
                TryRestoreBlockedWorkflowCandidate();
                _reviewedPreviewHash = null;
                _currentPreviewHash = Ai3dCandidateService.ComputePreviewHash(
                    _staged?.Asset, _staged?.ValidationHash);
                SetException(exception);
            }
            finally { SetBusy(false); }
        }

        private void AcceptCandidate()
        {
            if (_staged == null) return;
            if (!EditorUtility.DisplayDialog(
                    "Aceitar candidato",
                    "Promover a cópia validada da quarentena para a pasta de assets aceitos? A cena não será alterada.",
                    "Aceitar asset", "Cancelar")) return;
            try
            {
                if (!string.IsNullOrEmpty(_activeWorkflowId))
                {
                    var workflow = Ai3dWorkflowService.AcceptCandidate(
                        _activeWorkflowId, _activeWorkflowPlanHash,
                        _staged.SourceArtifactHash, _staged.ValidationHash,
                        _reviewedPreviewHash, _acceptedRoot);
                    _acceptedAssetPath = workflow.AcceptedAssetPath;
                }
                else
                    _acceptedAssetPath = Ai3dCandidateService.Accept(
                        _staged, _reviewedPreviewHash, _acceptedRoot);
                var accepted = AssetDatabase.LoadAssetAtPath<GameObject>(_acceptedAssetPath);
                if (accepted != null) EditorGUIUtility.PingObject(accepted);
                SetStatus("Asset aceito. Ele ainda não foi adicionado à cena.", MessageType.Info);
            }
            catch (Exception exception)
            {
                _reviewedPreviewHash = null;
                _currentPreviewHash = Ai3dCandidateService.ComputePreviewHash(
                    _staged?.Asset, _staged?.ValidationHash);
                SetException(exception);
            }
        }

        private void RejectWorkflowCandidate()
        {
            if (_staged == null || string.IsNullOrEmpty(_activeWorkflowId)) return;
            if (!EditorUtility.DisplayDialog(
                    "Rejeitar candidato",
                    "Marcar este candidato como rejeitado e limpar o preview? Os arquivos de quarentena serão preservados para recuperação.",
                    "Rejeitar", "Cancelar")) return;
            try
            {
                Ai3dWorkflowService.RejectCandidate(
                    _activeWorkflowId, _activeWorkflowPlanHash, _staged.SourceArtifactHash);
                _reviewedPreviewHash = null;
                _currentPreviewHash = null;
                SetStatus("Candidato rejeitado. A quarentena foi preservada e a cena não foi alterada.", MessageType.Warning);
            }
            catch (Exception exception) { SetException(exception); }
        }

        private void InstantiateAccepted()
        {
            if (string.IsNullOrEmpty(_acceptedAssetPath)) return;
            if (!EditorUtility.DisplayDialog(
                    "Adicionar à cena",
                    "Criar uma instância Undo-aware do asset aceito na Scene View atual?",
                    "Adicionar", "Cancelar")) return;
            try
            {
                var position = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
                Ai3dCandidateService.InstantiateAccepted(_acceptedAssetPath, position);
                SetStatus("Instância adicionada à cena. Salvar a cena continua sendo explícito.", MessageType.Info);
            }
            catch (Exception exception) { SetException(exception); }
        }

        private void SaveSettings()
        {
            if (!Ai3dGatewaySettings.TryNormalizeGatewayUrl(_gatewayUrl, out var normalized, out var error))
                throw new InvalidOperationException(error);
            Ai3dGatewaySettings.GatewayUrl = normalized;
            Ai3dGatewaySettings.GatewayToken = (_gatewayToken ?? string.Empty).Trim();
            Ai3dGatewaySettings.QuarantineRoot = _quarantineRoot;
            Ai3dGatewaySettings.AcceptedRoot = _acceptedRoot;
            Ai3dGatewaySettings.TimeoutSeconds = _timeoutSeconds;
            Ai3dGatewaySettings.MaxCreditsPerWorkflow = _maxWorkflowCredits;
            Ai3dGatewaySettings.ModelVersion = string.IsNullOrWhiteSpace(_model)
                ? Ai3dGatewaySettings.DefaultModel : _model.Trim();
            _gatewayUrl = Ai3dGatewaySettings.GatewayUrl;
            _quarantineRoot = Ai3dGatewaySettings.QuarantineRoot;
            _acceptedRoot = Ai3dGatewaySettings.AcceptedRoot;
        }

        private CancellationToken Token => _lifetime?.Token ?? CancellationToken.None;

        private void SetBusy(bool value)
        {
            _busy = value;
            Repaint();
        }

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }

        private void SetException(Exception exception)
        {
            var root = exception.GetBaseException();
            if (root is OperationCanceledException) return;
            var code = root is Ai3dGatewayException gateway ? gateway.Code + ": "
                : root is Ai3dWorkflowException workflow ? workflow.Code + ": "
                : string.Empty;
            SetStatus(code + root.Message, MessageType.Error);
        }

        private Ai3dWorkflowRecord TryGetActiveWorkflow()
        {
            if (string.IsNullOrEmpty(_activeWorkflowId)) return null;
            try { return Ai3dWorkflowService.Get(_activeWorkflowId); }
            catch { return null; }
        }

        private void TryRestoreBlockedWorkflowCandidate()
        {
            var workflow = TryGetActiveWorkflow();
            if (workflow == null || string.IsNullOrEmpty(workflow.StagedAssetPath)) return;
            try
            {
                _staged = Ai3dCandidateService.RevalidateStaged(
                    workflow.StagedAssetPath, workflow.CurrentJobId, workflow.ProviderTaskId,
                    workflow.PlanHash, workflow.CandidateHash);
            }
            catch { _staged = null; }
        }

        private static void DrawWorkflowQuality(Ai3dWorkflowRecord workflow)
        {
            var report = workflow.QualityReport;
            if (report == null) return;
            var allowed = (bool?)report["acceptAllowed"] != false;
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Gate específico do workflow", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Fluxo", workflow.WorkflowKind ?? "-");
            EditorGUILayout.LabelField("Resultado", allowed ? "Aprovado" : "Bloqueado");
            var sourceTriangles = (long?)report["source"]?["triangleCount"];
            var candidateTriangles = (long?)report["candidate"]?["triangleCount"];
            if (sourceTriangles.HasValue || candidateTriangles.HasValue)
                EditorGUILayout.LabelField(
                    "Triângulos (origem → candidato)",
                    $"{sourceTriangles.GetValueOrDefault():N0} → {candidateTriangles.GetValueOrDefault():N0}");
            var tools = report["recommendedLocalTools"] as JArray;
            if (tools != null && tools.Count > 0)
                EditorGUILayout.LabelField("Ferramentas sugeridas", string.Join(", ", tools.Values<string>()));
            var issues = workflow.ValidationIssues ?? report["issues"] as JArray;
            if (issues == null) return;
            foreach (var token in issues.OfType<JObject>())
            {
                var severity = ((string)token["severity"] ?? string.Empty).ToLowerInvariant();
                var type = severity == "error" ? MessageType.Error
                    : severity == "warning" ? MessageType.Warning
                    : MessageType.Info;
                EditorGUILayout.HelpBox(
                    ((string)token["code"] ?? "AI3D_QUALITY") + ": " + ((string)token["message"] ?? string.Empty), type);
            }
        }

        private static void DrawIssues(IReadOnlyList<Ai3dCandidateIssue> issues)
        {
            for (int i = 0; i < issues.Count; i++)
            {
                var type = issues[i].Severity == Ai3dIssueSeverity.Error ? MessageType.Error
                    : issues[i].Severity == Ai3dIssueSeverity.Warning ? MessageType.Warning
                    : MessageType.Info;
                EditorGUILayout.HelpBox(issues[i].Code + ": " + issues[i].Message, type);
            }
        }

        private static string Short(string value, int length)
            => string.IsNullOrEmpty(value) || value.Length <= length ? value : value.Substring(0, length);

        private static string CredentialSourceLabel(string source, string provider)
        {
            switch (source)
            {
                case "windows_credential_manager": return "Windows Credential Manager";
                case "environment": return "variável " + (provider == "meshy" ? "MESHY_API_KEY" : "TRIPO_API_KEY");
                case "memory": return "memória temporária";
                default: return "nenhuma";
            }
        }
    }
}
