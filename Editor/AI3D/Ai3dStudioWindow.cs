using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    public sealed class Ai3dStudioWindow : EditorWindow
    {
        private static readonly string[] PageLabels = { "Gerar", "Jobs", "Candidato", "Configuração" };
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
        private Ai3dGatewayHealth _health;
        private JObject _capabilities;
        private Ai3dCredentialStatus _credentialStatus;
        private string _tripoApiKeyInput;
        private bool _showTripoApiKey;
        private JObject _balance;

        private int _generationMode;
        private Ai3dGenerationPreset _preset = Ai3dGenerationPreset.GamePc;
        private string _model;
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
        private string _pendingGenerationKey;
        private Ai3dUploadSnapshot _preparedUpload;
        private string _preparedUploadPath;
        private string _pendingConvertKey;

        private readonly List<Ai3dJobSummary> _jobs = new List<Ai3dJobSummary>();
        private Ai3dJobSnapshot _selectedJob;
        private Ai3dArtifactValidation _artifactValidation;
        private Ai3dStagedCandidate _staged;
        private string _reviewedPreviewHash;
        private string _currentPreviewHash;
        private string _acceptedAssetPath;

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
            if (_staged != null && Ai3dScenePreviewController.ActiveAsset == _staged.Asset)
                Ai3dScenePreviewController.Clear();
        }

        private async Task InitializeAsync()
        {
            await TestConnectionAsync(false);
            if (!string.IsNullOrEmpty(Ai3dGatewaySettings.LastJobId))
            {
                try { await SelectJobAsync(Ai3dGatewaySettings.LastJobId); }
                catch { /* Last job may belong to a removed gateway data directory. */ }
            }
            await RefreshJobsAsync(false);
        }

        private void OnEditorUpdate()
        {
            if (!_autoPoll || _busy || _polling || _selectedJob == null || _selectedJob.IsTerminal) return;
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1.5d;
            _ = RefreshSelectedJobAsync(false);
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
                case 1: DrawJobsPage(); break;
                case 2: DrawCandidatePage(); break;
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
                var state = _health == null ? "não testado"
                    : !_health.Ready ? "offline"
                    : _health.Configured ? "Tripo pronta" : "gateway sem API key";
                var previous = GUI.color;
                GUI.color = _health != null && _health.Ready && _health.Configured
                    ? new Color(0.55f, 1f, 0.65f)
                    : new Color(1f, 0.75f, 0.35f);
                GUILayout.Label("● " + state, EditorStyles.miniBoldLabel);
                GUI.color = previous;
            }
        }

        private void DrawGeneratePage()
        {
            EditorGUILayout.LabelField("Novo candidato", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Gera um candidato externo. O resultado permanece no vault até você converter, validar e colocar em quarentena.",
                MessageType.Info);
            _generationMode = GUILayout.Toolbar(_generationMode, new[] { "Texto → 3D", "Imagem → 3D" });
            EditorGUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            _preset = (Ai3dGenerationPreset)EditorGUILayout.Popup("Preset", (int)_preset, PresetLabels);
            if (EditorGUI.EndChangeCheck()) ApplyPreset();
            _model = EditorGUILayout.TextField(new GUIContent("Modelo Tripo", "Versão explícita; não é atualizada silenciosamente."), _model);

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
                        var selected = EditorUtility.OpenFilePanel("Imagem para Tripo", string.Empty, "png,jpg,jpeg");
                        if (!string.IsNullOrEmpty(selected) && !string.Equals(selected, _imagePath, StringComparison.Ordinal))
                        {
                            _imagePath = selected;
                            _preparedUpload = null;
                            _preparedUploadPath = null;
                            _pendingGenerationKey = null;
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
                _faceLimit = EditorGUILayout.IntSlider("Limite de faces", Mathf.Clamp(_faceLimit, 500, 20000), 500, 20000);
                EditorGUILayout.HelpBox("A API Smart Low Poly aceita no máximo 20.000 faces.", MessageType.None);
            }
            else _faceLimit = EditorGUILayout.IntField("Limite de faces", Mathf.Max(500, _faceLimit));
            _geometryQuality = EditorGUILayout.Popup("Qualidade", _geometryQuality == "detailed" ? 1 : 0,
                new[] { "standard", "detailed" }) == 1 ? "detailed" : "standard";
            _texture = EditorGUILayout.ToggleLeft("Gerar textura", _texture);
            using (new EditorGUI.DisabledScope(!_texture))
                _pbr = EditorGUILayout.ToggleLeft("Materiais PBR", _texture && _pbr);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Orçamento", EditorStyles.boldLabel);
            _estimatedCredits = Mathf.Max(0, EditorGUILayout.IntField("Estimativa", _estimatedCredits));
            _capCredits = Mathf.Max(_estimatedCredits, EditorGUILayout.IntField("Limite máximo", _capCredits));
            EditorGUILayout.HelpBox(
                "O limite local evita submissões acima do valor aprovado, mas não é uma trava transacional da Tripo.",
                MessageType.Warning);
            _externalSendApproved = EditorGUILayout.ToggleLeft(
                _generationMode == 0
                    ? "Autorizo enviar este texto para a Tripo e consumir créditos"
                    : "Autorizo enviar esta imagem para a Tripo e consumir créditos",
                _externalSendApproved);

            var canGenerate = !_busy && _externalSendApproved && _health != null && _health.Ready && _health.Configured;
            if (_generationMode == 0) canGenerate &= !string.IsNullOrWhiteSpace(_prompt);
            else canGenerate &= !string.IsNullOrWhiteSpace(_imagePath) && File.Exists(_imagePath);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Gerar candidato", GUILayout.Height(36f))) _ = SubmitGenerationAsync();
            }
            if (_health == null || !_health.Ready || !_health.Configured)
                EditorGUILayout.HelpBox("Abra Configuração e conecte um gateway com uma credencial Tripo.", MessageType.Warning);
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
            EditorGUILayout.LabelField("Operação", _selectedJob.Operation ?? string.Empty);
            EditorGUILayout.LabelField("Estado", _selectedJob.Status ?? string.Empty);
            var progressRect = EditorGUILayout.GetControlRect(false, 20f);
            EditorGUI.ProgressBar(progressRect, _selectedJob.Progress, _selectedJob.Message ?? _selectedJob.Status);
            EditorGUILayout.LabelField("Estimativa", _selectedJob.EstimatedCredits + " créditos");
            if (_selectedJob.CreditsConsumed.HasValue)
                EditorGUILayout.LabelField("Consumido", _selectedJob.CreditsConsumed.Value + " créditos");
            if (_selectedJob.Error != null)
                EditorGUILayout.HelpBox(_selectedJob.Error.Code + ": " + _selectedJob.Error.Message, MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Atualizar")) _ = RefreshSelectedJobAsync(true);
                using (new EditorGUI.DisabledScope(_busy || _selectedJob.IsTerminal))
                    if (GUILayout.Button("Cancelar localmente")) _ = CancelSelectedJobAsync();
            }

            if (string.Equals(_selectedJob.Status, "completed", StringComparison.Ordinal))
            {
                var fbx = _selectedJob.FindFbxArtifact();
                if (fbx == null && !string.IsNullOrEmpty(_selectedJob.ProviderTaskId))
                {
                    EditorGUILayout.HelpBox("O resultado ainda não é FBX. A conversão é um novo job faturável.", MessageType.Info);
                    using (new EditorGUI.DisabledScope(_busy || !_externalSendApproved))
                        if (GUILayout.Button("Converter resultado para FBX")) _ = ConvertSelectedToFbxAsync();
                    _externalSendApproved = EditorGUILayout.ToggleLeft(
                        "Autorizo a conversão e até 15 créditos", _externalSendApproved);
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
            if (_artifactValidation == null)
            {
                EditorGUILayout.HelpBox("Selecione um job concluído com FBX e clique em Validar FBX.", MessageType.Info);
                return;
            }
            DrawArtifactValidation();
            if (_artifactValidation.IsValid && _staged == null)
            {
                if (GUILayout.Button("Colocar na quarentena Unity", GUILayout.Height(32f))) StageCandidate();
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
                    Ai3dScenePreviewController.Show(_staged.Asset, Short(_staged.JobId, 10));
                    _currentPreviewHash = Ai3dCandidateService.ComputePreviewHash(
                        _staged.Asset, _staged.ValidationHash);
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
            using (new EditorGUI.DisabledScope(!_staged.IsValid || !previewIsCurrent || _busy))
            {
                if (GUILayout.Button("Aceitar como asset do projeto", GUILayout.Height(34f))) AcceptCandidate();
            }
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
            EditorGUILayout.Space(8f);
            DrawTripoCredentialSettings();
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
                using (new EditorGUI.DisabledScope(_health == null || !_health.Ready || !_health.Configured))
                    if (GUILayout.Button("Consultar saldo")) _ = RefreshBalanceAsync();
            }
            if (_health != null)
            {
                EditorGUILayout.LabelField("Serviço", _health.Service ?? string.Empty);
                EditorGUILayout.LabelField("Versão", _health.Version ?? string.Empty);
                EditorGUILayout.LabelField("Gateway", _health.Ready ? "pronto" : "offline");
                EditorGUILayout.LabelField("Tripo API", _health.Configured ? "configurada" : "não configurada");
            }
            if (_balance != null)
            {
                EditorGUILayout.LabelField("Saldo disponível", ((int?)_balance["balance"] ?? 0).ToString("N0"));
                EditorGUILayout.LabelField("Créditos congelados", ((int?)_balance["frozen"] ?? 0).ToString("N0"));
            }
        }

        private void DrawTripoCredentialSettings()
        {
            EditorGUILayout.LabelField("Credencial Tripo", EditorStyles.boldLabel);
            if (_credentialStatus != null)
            {
                EditorGUILayout.LabelField("Estado", _credentialStatus.Configured ? "configurada" : "não configurada");
                EditorGUILayout.LabelField("Origem", CredentialSourceLabel(_credentialStatus.Source));
                EditorGUILayout.LabelField("Armazenamento", _credentialStatus.Store ?? "indisponível");
                if (!_credentialStatus.StoreAvailable)
                    EditorGUILayout.HelpBox(
                        "O armazenamento seguro não está disponível neste sistema. Ainda é possível iniciar o gateway com TRIPO_API_KEY.",
                        MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Conecte o gateway para consultar o armazenamento seguro da credencial.", MessageType.None);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _tripoApiKeyInput = _showTripoApiKey
                    ? EditorGUILayout.TextField("Nova API key", _tripoApiKeyInput ?? string.Empty)
                    : EditorGUILayout.PasswordField("Nova API key", _tripoApiKeyInput ?? string.Empty);
                _showTripoApiKey = GUILayout.Toggle(_showTripoApiKey, "Ver", GUILayout.Width(44f));
            }
            var canStore = !_busy
                           && _credentialStatus != null
                           && _credentialStatus.StoreAvailable
                           && !string.IsNullOrWhiteSpace(_tripoApiKeyInput)
                           && _tripoApiKeyInput.Length >= 8;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!canStore))
                    if (GUILayout.Button("Salvar com segurança")) _ = SaveTripoApiKeyAsync();
                using (new EditorGUI.DisabledScope(
                           _busy || _credentialStatus == null || !_credentialStatus.Configured))
                    if (GUILayout.Button("Testar credencial")) _ = TestTripoCredentialAsync();
                using (new EditorGUI.DisabledScope(
                           _busy || _credentialStatus == null
                           || !string.Equals(_credentialStatus.Source, "windows_credential_manager", StringComparison.Ordinal)))
                    if (GUILayout.Button("Remover")) _ = DeleteTripoApiKeyAsync();
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
            _pendingGenerationKey = null;
        }

        private async Task SubmitGenerationAsync()
        {
            var subject = _generationMode == 0 ? "o prompt" : "a imagem";
            if (!EditorUtility.DisplayDialog(
                    "Enviar para Tripo",
                    $"Enviar {subject} para um provedor externo e autorizar até {_capCredits} créditos?",
                    "Enviar e gerar", "Cancelar")) return;
            SetBusy(true);
            try
            {
                if (string.IsNullOrEmpty(_pendingGenerationKey)) _pendingGenerationKey = Guid.NewGuid().ToString("N");
                Ai3dJobSnapshot job;
                if (_generationMode == 0)
                {
                    job = await Ai3dGatewayClient.CreateTextJobAsync(
                        _prompt, _model, _faceLimit, _texture, _pbr, _smartLowPoly,
                        _geometryQuality, _estimatedCredits, _capCredits,
                        _pendingGenerationKey, Token);
                }
                else
                {
                    var absolute = Path.GetFullPath(_imagePath);
                    var currentHash = Ai3dGatewayClient.ComputeFileSha256(absolute);
                    if (_preparedUpload == null
                        || !string.Equals(_preparedUploadPath, absolute, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(_preparedUpload.Sha256, currentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _preparedUpload = await Ai3dGatewayClient.UploadAsync(absolute, Token);
                        _preparedUploadPath = absolute;
                    }
                    job = await Ai3dGatewayClient.CreateImageJobAsync(
                        _preparedUpload.FileToken, _preparedUpload.Sha256, _model, _faceLimit,
                        _texture, _pbr, _smartLowPoly, _geometryQuality,
                        _estimatedCredits, _capCredits, _pendingGenerationKey, Token);
                }
                _pendingGenerationKey = null;
                _selectedJob = job;
                _externalSendApproved = false;
                _artifactValidation = null;
                _staged = null;
                _reviewedPreviewHash = null;
                _currentPreviewHash = null;
                _acceptedAssetPath = null;
                _page = 1;
                SetStatus("Job criado. O gateway fará polling e download para o vault.", MessageType.Info);
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
                    "Criar um novo job Tripo de conversão, autorizado a consumir até 15 créditos?",
                    "Converter", "Cancelar")) return;
            SetBusy(true);
            try
            {
                if (string.IsNullOrEmpty(_pendingConvertKey)) _pendingConvertKey = Guid.NewGuid().ToString("N");
                _selectedJob = await Ai3dGatewayClient.CreateConvertToFbxJobAsync(
                    _selectedJob.ProviderTaskId, 10, 15, _pendingConvertKey, Token);
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
                if (_health.Ready && !string.IsNullOrEmpty(Ai3dGatewaySettings.GatewayToken))
                {
                    _capabilities = await Ai3dGatewayClient.GetCapabilitiesAsync(Token);
                    _credentialStatus = await Ai3dGatewayClient.GetCredentialStatusAsync(Token);
                }
                if (showSuccess)
                    SetStatus(_health.Configured
                        ? "Gateway conectado e Tripo API configurada."
                        : "Gateway conectado, mas a credencial Tripo não está configurada.",
                        _health.Configured ? MessageType.Info : MessageType.Warning);
            }
            catch (Exception exception)
            {
                _health = null;
                _capabilities = null;
                _credentialStatus = null;
                if (showSuccess) SetException(exception);
            }
            finally { SetBusy(false); }
        }

        private async Task RefreshBalanceAsync()
        {
            SetBusy(true);
            try
            {
                _balance = await Ai3dGatewayClient.GetBalanceAsync(Token);
                SetStatus("Saldo atualizado sem consumir créditos.", MessageType.Info);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private async Task SaveTripoApiKeyAsync()
        {
            if (string.IsNullOrWhiteSpace(_tripoApiKeyInput)) return;
            if (!EditorUtility.DisplayDialog(
                    "Salvar credencial Tripo",
                    "Enviar esta key uma única vez ao gateway local e armazená-la no Windows Credential Manager? Ela não será salva em Assets ou EditorPrefs.",
                    "Salvar com segurança", "Cancelar")) return;
            var value = _tripoApiKeyInput;
            var stored = false;
            SetBusy(true);
            try
            {
                _credentialStatus = await Ai3dGatewayClient.SetTripoApiKeyAsync(value, Token);
                stored = true;
                _health = await Ai3dGatewayClient.HealthAsync(Token);
                _balance = await Ai3dGatewayClient.GetBalanceAsync(Token);
                SetStatus("Credencial salva no Windows Credential Manager e validada pela Tripo.", MessageType.Info);
            }
            catch (Exception exception)
            {
                if (!stored) SetException(exception);
                else
                {
                    var root = exception.GetBaseException();
                    var code = root is Ai3dGatewayException gateway ? gateway.Code + ": " : string.Empty;
                    SetStatus(
                        "Credencial armazenada, mas o teste com a Tripo falhou: " + code + root.Message,
                        MessageType.Warning);
                }
            }
            finally
            {
                _tripoApiKeyInput = null;
                value = null;
                SetBusy(false);
            }
        }

        private async Task TestTripoCredentialAsync()
        {
            SetBusy(true);
            try
            {
                _credentialStatus = await Ai3dGatewayClient.GetCredentialStatusAsync(Token);
                _balance = await Ai3dGatewayClient.GetBalanceAsync(Token);
                _health = await Ai3dGatewayClient.HealthAsync(Token);
                SetStatus("Credencial válida; saldo consultado sem consumir créditos.", MessageType.Info);
            }
            catch (Exception exception) { SetException(exception); }
            finally { SetBusy(false); }
        }

        private async Task DeleteTripoApiKeyAsync()
        {
            if (!EditorUtility.DisplayDialog(
                    "Remover credencial Tripo",
                    "Remover a key persistida do Windows Credential Manager? Jobs remotos em andamento podem falhar no próximo polling.",
                    "Remover", "Cancelar")) return;
            SetBusy(true);
            try
            {
                _credentialStatus = await Ai3dGatewayClient.DeleteTripoApiKeyAsync(Token);
                _health = await Ai3dGatewayClient.HealthAsync(Token);
                _balance = null;
                SetStatus(_credentialStatus.Configured
                    ? "Credencial persistida removida; TRIPO_API_KEY do ambiente continua ativa."
                    : "Credencial Tripo removida.",
                    _credentialStatus.Configured ? MessageType.Warning : MessageType.Info);
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
            _selectedJob = await Ai3dGatewayClient.GetJobAsync(jobId, Token);
            Ai3dGatewaySettings.LastJobId = jobId;
            _artifactValidation = null;
            _staged = null;
            _reviewedPreviewHash = null;
            _currentPreviewHash = null;
            _acceptedAssetPath = null;
            _page = 1;
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
                _selectedJob = await Ai3dGatewayClient.CancelJobAsync(_selectedJob.JobId, Token);
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
                _page = 2;
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

        private void AcceptCandidate()
        {
            if (_staged == null) return;
            if (!EditorUtility.DisplayDialog(
                    "Aceitar candidato",
                    "Promover a cópia validada da quarentena para a pasta de assets aceitos? A cena não será alterada.",
                    "Aceitar asset", "Cancelar")) return;
            try
            {
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
            var code = root is Ai3dGatewayException gateway ? gateway.Code + ": " : string.Empty;
            SetStatus(code + root.Message, MessageType.Error);
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

        private static string CredentialSourceLabel(string source)
        {
            switch (source)
            {
                case "windows_credential_manager": return "Windows Credential Manager";
                case "environment": return "variável TRIPO_API_KEY";
                case "memory": return "memória temporária";
                default: return "nenhuma";
            }
        }
    }
}
