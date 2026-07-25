using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Chat;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    public sealed partial class CoderChatWindow : EditorWindow
    {
        private const string WindowTitle = "Coder Chat";
        private const string UxmlRelativePath = "Editor/Window/CoderChatWindow.uxml";

        private readonly List<ChatMessage> _visibleMessages = new();
        private readonly Dictionary<ChatMessage, int> _renderedRevisions = new();
        private readonly List<ChatReference> _draftReferences = new();
        private readonly Dictionary<string, CoderAgentInfo> _agentByLabel = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CoderSessionInfo> _sessionByLabel = new(StringComparer.Ordinal);
        private readonly CoderChatScrollState _scrollState = new();
        private readonly CoderChatInvalidationState _invalidationState = new();

        private ListView _transcriptList;
        private VisualElement _interactionsContainer;
        private VisualElement _referenceChips;
        private VisualElement _connectionActions;
        private VisualElement _composerPanel;
        private VisualElement _composerToolbar;
        private VisualElement _composerLeft;
        private VisualElement _composerRight;
        private Label _composerPlaceholder;
        private Label _statusLabel;
        private Label _sessionMetaLabel;
        private Label _permissionBadge;
        private Label _composerFeedback;
        private VisualElement _statusDot;
        private Button _newMessagesButton;
        private Button _sendButton;
        private Button _modelButton;
        private TextField _composerInput;
        private DropdownField _agentField;
        private DropdownField _effortField;
        private DropdownField _sessionField;
        private ScrollView _transcriptScroll;
        private IVisualElementScheduledItem _scheduledUiFlush;

        private bool _sessionDirty = true;
        private bool _catalogDirty = true;
        private bool _composerFocused;
        private bool _showSystemMessages;
        private bool _eventsSubscribed;
        private string _composerDraft = "";
        private string _interactionSignature = "";
        private int _pendingAttachSessionId;
        private int _attachedSessionId;

        [MenuItem("Window/Coder Chat (Scene Analyzer)")]
        public static void Open()
        {
            var window = GetWindow<CoderChatWindow>(WindowTitle);
            window.minSize = new Vector2(420, 560);
            window.Show();
        }

        private void OnEnable()
        {
            _showSystemMessages = Settings.CoderShowSystemMessages;
            _attachedSessionId = 0;
            _pendingAttachSessionId = Settings.CoderSessionId;
            SubscribeEvents();
        }

        private void OnDisable()
        {
            if (_composerInput != null) _composerDraft = _composerInput.value ?? "";
            CancelScheduledUiFlush();
            CloseReferencePicker();
            Bootstrap.ChatClient?.StopSessionSync();
            _attachedSessionId = 0;
            UnsubscribeEvents();
        }

        public void CreateGUI()
        {
            CancelScheduledUiFlush();
            CloseReferencePicker();
            rootVisualElement.Clear();
            var tree = LoadVisualTree();
            if (tree == null)
            {
                BuildAssetErrorFallback();
                return;
            }

            tree.CloneTree(rootVisualElement);
            if (!CacheVisualElements())
            {
                BuildAssetErrorFallback("O UXML foi carregado, mas não contém todos os elementos esperados.");
                return;
            }

            BuildConnectionBar();
            BuildSettingsPanel(rootVisualElement.Q<VisualElement>("settings-host"));
            BuildTranscript();
            BuildComposer();
            RegisterComposerDragAndDrop();
            WireNewMessagesButton();

            _sessionDirty = true;
            _catalogDirty = true;
            rootVisualElement.schedule.Execute(InitializeTranscriptScroll);
            RefreshRemoteCatalogs();
            FlushUi();
        }

        private void SubscribeEvents()
        {
            if (_eventsSubscribed) return;
            _eventsSubscribed = true;
            if (Bootstrap.ChatSession != null)
                Bootstrap.ChatSession.OnChanged += OnSessionChanged;
            Bootstrap.OnCoderProvidersChanged += OnCatalogChanged;
            Bootstrap.OnCoderAgentsChanged += OnCatalogChanged;
            Bootstrap.OnCoderSessionsChanged += OnCatalogChanged;
            Bootstrap.OnServerStateChanged += OnServerStateChanged;
        }

        private void UnsubscribeEvents()
        {
            if (!_eventsSubscribed) return;
            _eventsSubscribed = false;
            if (Bootstrap.ChatSession != null)
                Bootstrap.ChatSession.OnChanged -= OnSessionChanged;
            Bootstrap.OnCoderProvidersChanged -= OnCatalogChanged;
            Bootstrap.OnCoderAgentsChanged -= OnCatalogChanged;
            Bootstrap.OnCoderSessionsChanged -= OnCatalogChanged;
            Bootstrap.OnServerStateChanged -= OnServerStateChanged;
        }

        private void OnSessionChanged()
        {
            _sessionDirty = true;
            _pendingAttachSessionId = Settings.CoderSessionId;
            ScheduleUiFlush();
        }

        private void OnCatalogChanged()
        {
            _catalogDirty = true;
            _sessionDirty = true;
            ScheduleUiFlush();
        }

        private static void OnServerStateChanged(string state)
        {
            Bootstrap.ChatSession?.AddSystemNote($"Servidor MCP: {state}");
        }

        private bool CacheVisualElements()
        {
            _statusDot = rootVisualElement.Q<VisualElement>("status-dot");
            _statusLabel = rootVisualElement.Q<Label>("status-label");
            _sessionMetaLabel = rootVisualElement.Q<Label>("session-meta-label");
            _permissionBadge = rootVisualElement.Q<Label>("permission-badge");
            _connectionActions = rootVisualElement.Q<VisualElement>("connection-actions");
            _transcriptList = rootVisualElement.Q<ListView>("transcript-list");
            _newMessagesButton = rootVisualElement.Q<Button>("new-messages-button");
            _interactionsContainer = rootVisualElement.Q<VisualElement>("interactions-container");
            _referenceChips = rootVisualElement.Q<VisualElement>("reference-chips");
            _composerPanel = rootVisualElement.Q<VisualElement>("composer-panel");
            _composerToolbar = rootVisualElement.Q<VisualElement>("composer-toolbar");
            _composerInput = rootVisualElement.Q<TextField>("composer-input");
            _composerPlaceholder = rootVisualElement.Q<Label>("composer-placeholder");
            _composerFeedback = rootVisualElement.Q<Label>("composer-feedback");
            _composerLeft = rootVisualElement.Q<VisualElement>("composer-left");
            _composerRight = rootVisualElement.Q<VisualElement>("composer-right");

            return _statusDot != null
                && _statusLabel != null
                && _sessionMetaLabel != null
                && _permissionBadge != null
                && _connectionActions != null
                && _transcriptList != null
                && _newMessagesButton != null
                && _interactionsContainer != null
                && _referenceChips != null
                && _composerPanel != null
                && _composerToolbar != null
                && _composerInput != null
                && _composerPlaceholder != null
                && _composerFeedback != null
                && _composerLeft != null
                && _composerRight != null;
        }

        private void BuildConnectionBar()
        {
            _connectionActions.Clear();

            _sessionField = new DropdownField
            {
                name = "session-field",
                tooltip = "Sessões recentes deste projeto",
            };
            _sessionField.AddToClassList("agent-field");
            _sessionField.RegisterValueChangedCallback(evt =>
            {
                if (string.IsNullOrEmpty(evt.newValue)
                    || !_sessionByLabel.TryGetValue(evt.newValue, out var selected)) return;
                AttachSession(selected.Id, true);
            });
            _connectionActions.Add(_sessionField);

            _connectionActions.Add(new Button(() =>
            {
                var sessionId = Settings.CoderSessionId;
                if (sessionId > 0) AttachSession(sessionId, false);
                RefreshRemoteCatalogs();
            }) { text = "Sincronizar", tooltip = "Atualizar sessão, agentes, modelos e lista de sessões" });

            _connectionActions.Add(new Button(CreateNewSession)
            {
                text = "Nova",
                tooltip = "Criar uma sessão para este projeto Unity",
            });

            _connectionActions.Add(new Button(() =>
            {
                if (_settingsFoldout != null) _settingsFoldout.value = !_settingsFoldout.value;
            }) { text = "Configurações" });
        }

        private void BuildTranscript()
        {
            _transcriptList.itemsSource = _visibleMessages;
            _transcriptList.makeItem = () => new CoderChatMessageElement();
            _transcriptList.bindItem = (element, index) =>
            {
                var messageElement = (CoderChatMessageElement)element;
                messageElement.Bind(index >= 0 && index < _visibleMessages.Count ? _visibleMessages[index] : null);
            };
            _transcriptList.unbindItem = (element, _) => ((CoderChatMessageElement)element).Bind(null);
            _transcriptList.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            _transcriptList.selectionType = SelectionType.None;
            _transcriptList.horizontalScrollingEnabled = false;
            _transcriptList.showBorder = false;
        }

        private void BuildComposer()
        {
            _composerLeft.Clear();
            _composerRight.Clear();

            _composerInput.value = _composerDraft;
            _composerInput.multiline = true;
            _composerInput.RegisterValueChangedCallback(evt =>
            {
                _composerDraft = evt.newValue ?? "";
                UpdateComposerPresentation();
            });
            _composerInput.RegisterCallback<KeyDownEvent>(OnComposerKeyDown, TrickleDown.TrickleDown);
            _composerInput.RegisterCallback<FocusInEvent>(_ =>
            {
                _composerFocused = true;
                _composerPanel.AddToClassList("focused");
                UpdateComposerPresentation();
            });
            _composerInput.RegisterCallback<FocusOutEvent>(_ =>
            {
                _composerFocused = false;
                _composerPanel.RemoveFromClassList("focused");
                UpdateComposerPresentation();
            });
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnChatGeometryChanged);
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(OnChatGeometryChanged);

            var attachButton = new Button(OpenReferencePicker)
            {
                text = "Anexar",
                tooltip = "Adicionar GameObjects, Components, assets ou pastas",
            };
            _composerLeft.Add(attachButton);

            var selectionButton = new Button(AttachCurrentSelection)
            {
                text = "Seleção",
                tooltip = "Anexar os objetos selecionados no Unity",
            };
            _composerLeft.Add(selectionButton);

            _agentField = new DropdownField { tooltip = "Agente definido pelo runtime" };
            _agentField.AddToClassList("agent-field");
            _agentField.RegisterValueChangedCallback(evt =>
            {
                if (!_agentByLabel.TryGetValue(evt.newValue, out var selected)) return;
                Settings.CoderAgent = selected.Id;
                _agentField.tooltip = selected.Description ?? selected.Label;
            });
            _composerLeft.Add(_agentField);

            _effortField = new DropdownField(new List<string> { "Padrão", "Pensar" }, 0);
            _effortField.AddToClassList("effort-field");
            _effortField.RegisterValueChangedCallback(evt =>
            {
                if (!_effortField.enabledSelf) return;
                Settings.CoderThinking = evt.newValue == "Pensar";
            });
            _composerLeft.Add(_effortField);

            _modelButton = new Button(() =>
            {
                if (_settingsFoldout != null) _settingsFoldout.value = true;
                _modelField?.Focus();
            });
            _modelButton.tooltip = "Provider/modelo da nova sessão";
            _composerLeft.Add(_modelButton);

            _sendButton = new Button(DoSend);
            _sendButton.AddToClassList("send-button");
            _composerRight.Add(_sendButton);

            RenderReferenceChips();
            UpdateComposerPresentation();
            UpdateResponsiveLayout(rootVisualElement.resolvedStyle.width);
        }

        private void OnChatGeometryChanged(GeometryChangedEvent evt)
        {
            if (Mathf.Abs(evt.newRect.width - evt.oldRect.width) < 1f) return;
            UpdateResponsiveLayout(evt.newRect.width);
            UpdateComposerPresentation();
        }

        private void UpdateResponsiveLayout(float width)
        {
            rootVisualElement.EnableInClassList("compact-layout", width > 0f && width < 620f);
        }

        private void UpdateComposerPresentation()
        {
            if (_composerInput == null || _composerPlaceholder == null) return;
            var text = _composerInput.value ?? "";
            SetDisplayed(_composerPlaceholder, string.IsNullOrEmpty(text) && !_composerFocused);
            _composerInput.style.height = EstimateComposerHeight(text, _composerInput.resolvedStyle.width);
        }

        internal static float EstimateComposerHeight(string text, float width)
        {
            var estimatedColumns = Mathf.Max(24, Mathf.FloorToInt(Mathf.Max(260f, width) / 7f));
            var visualLines = 0;
            foreach (var line in (text ?? "").Split('\n'))
                visualLines += Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, line.Length) / (float)estimatedColumns));
            return Mathf.Clamp(42f + (visualLines - 1) * 17f, 42f, 160f);
        }

        private void OnComposerKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            if (evt.shiftKey) return;
            evt.PreventDefault();
            evt.StopImmediatePropagation();
            DoSend();
        }

        private void DoSend()
        {
            var client = Bootstrap.ChatClient;
            var session = Bootstrap.ChatSession;
            if (client == null || session == null)
            {
                ShowComposerFeedback("O cliente de chat ainda não foi inicializado.");
                return;
            }

            if (client.IsRunning)
            {
                client.Cancel();
                return;
            }

            var text = (_composerInput.value ?? "").Trim();
            var validReferences = _draftReferences.Where(reference => reference != null && reference.Validate()).ToList();
            if (string.IsNullOrEmpty(text) && validReferences.Count == 0)
            {
                ShowComposerFeedback("Escreva uma mensagem ou anexe uma referência.");
                return;
            }

            var invalidCount = _draftReferences.Count - validReferences.Count;
            var content = ChatReference.BuildMessageContent(text, validReferences);
            session.AddUser(text, validReferences);
            client.Send(
                Settings.CoderUrl,
                Settings.CoderToken,
                Settings.CoderSessionId,
                content,
                Settings.CoderProfile,
                Settings.CoderAgent);

            _composerDraft = "";
            _composerInput.SetValueWithoutNotify("");
            _draftReferences.Clear();
            RenderReferenceChips();
            if (invalidCount > 0)
                ShowComposerFeedback($"{invalidCount} referência(s) indisponível(is) não foram enviada(s).", false);
            else
                HideComposerFeedback();
            _scrollState.ResumeFollowing();
            RequestScrollTranscriptToEnd();
            _composerInput.Focus();
        }

        private void CreateNewSession()
        {
            if (Bootstrap.ChatClient == null || Bootstrap.ChatSession == null) return;
            Bootstrap.ChatClient.StopSessionSync();
            _attachedSessionId = 0;
            _pendingAttachSessionId = 0;
            Settings.CoderSessionId = 0;
            Bootstrap.ChatSession.Reset();
            Bootstrap.ChatClient.CreateSession(
                Settings.CoderUrl,
                Settings.CoderToken,
                Settings.CoderProjectId,
                $"Unity chat — {DateTime.Now:yyyy-MM-dd HH:mm}",
                Settings.CoderProvider,
                Settings.CoderModel,
                Settings.CoderAgent);
        }

        private void AttachSession(int sessionId, bool replaceConversation)
        {
            if (sessionId <= 0 || Bootstrap.ChatClient == null || Bootstrap.ChatSession == null) return;
            if (replaceConversation) Bootstrap.ChatSession.Reset();
            Settings.CoderSessionId = sessionId;
            _attachedSessionId = sessionId;
            _pendingAttachSessionId = sessionId;
            Bootstrap.ChatClient.AttachSession(
                Settings.CoderUrl, Settings.CoderToken, sessionId, replaceConversation);
        }

        private void ScheduleUiFlush()
        {
            if (rootVisualElement.panel == null || !_invalidationState.TrySchedule()) return;
            _scheduledUiFlush = rootVisualElement.schedule.Execute(FlushUi);
        }

        private void CancelScheduledUiFlush()
        {
            _scheduledUiFlush?.Pause();
            _scheduledUiFlush = null;
            _invalidationState.Cancel();
        }

        private void FlushUi()
        {
            _scheduledUiFlush = null;
            _invalidationState.Consume();
            if (rootVisualElement.panel == null) return;
            TryAttachPendingSession();

            if (_catalogDirty)
            {
                _catalogDirty = false;
                RefreshCatalogControls();
                RefreshSettingsControls();
            }

            if (!_sessionDirty) return;
            _sessionDirty = false;

            UpdateConnectionHeader();
            UpdateSendButton();
            RefreshTranscript();
            RefreshInteractions();
            RefreshEffortAvailability();
        }

        private void TryAttachPendingSession()
        {
            if (_pendingAttachSessionId <= 0
                || _pendingAttachSessionId == _attachedSessionId
                || Bootstrap.ChatClient == null
                || Bootstrap.ChatClient.IsRunning) return;

            _attachedSessionId = _pendingAttachSessionId;
            Bootstrap.ChatClient.AttachSession(
                Settings.CoderUrl, Settings.CoderToken, _attachedSessionId, _visibleMessages.Count == 0);
        }

        private void RefreshTranscript()
        {
            var session = Bootstrap.ChatSession;
            if (session == null) return;

            var oldCount = _visibleMessages.Count;
            var structureChanged = VisibleSequenceChanged(session.Messages);
            var lastItemChanged = false;
            if (structureChanged)
            {
                var nextVisibleMessages = new List<ChatMessage>();
                foreach (var message in session.Messages)
                    if (IsVisible(message)) nextVisibleMessages.Add(message);

                if (IsStrictReferenceAppend(_visibleMessages, nextVisibleMessages))
                {
                    for (var index = oldCount; index < nextVisibleMessages.Count; index++)
                    {
                        var message = nextVisibleMessages[index];
                        _visibleMessages.Add(message);
                        _renderedRevisions[message] = message.Revision;
                    }
                    _transcriptList.RefreshItems();
                }
                else
                {
                    _visibleMessages.Clear();
                    _visibleMessages.AddRange(nextVisibleMessages);
                    _renderedRevisions.Clear();
                    foreach (var message in _visibleMessages) _renderedRevisions[message] = message.Revision;
                    _transcriptList.Rebuild();
                }
            }
            else
            {
                for (var index = 0; index < _visibleMessages.Count; index++)
                {
                    var message = _visibleMessages[index];
                    if (_renderedRevisions.TryGetValue(message, out var revision) && revision == message.Revision)
                        continue;
                    _renderedRevisions[message] = message.Revision;
                    _transcriptList.RefreshItem(index);
                    if (index == _visibleMessages.Count - 1) lastItemChanged = true;
                }
            }

            if (_visibleMessages.Count > oldCount || lastItemChanged)
            {
                if (_scrollState.IsFollowingEnd) RequestScrollTranscriptToEnd();
                else SetDisplayed(_newMessagesButton, true);
            }
        }

        internal static bool IsStrictReferenceAppend<T>(IReadOnlyList<T> current, IReadOnlyList<T> next)
            where T : class
        {
            if (current == null || next == null || next.Count <= current.Count) return false;
            for (var index = 0; index < current.Count; index++)
                if (!ReferenceEquals(current[index], next[index])) return false;
            return true;
        }

        private bool VisibleSequenceChanged(IReadOnlyList<ChatMessage> messages)
        {
            var visibleIndex = 0;
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                if (!IsVisible(message)) continue;
                if (visibleIndex >= _visibleMessages.Count || !ReferenceEquals(_visibleMessages[visibleIndex], message))
                    return true;
                visibleIndex++;
            }
            return visibleIndex != _visibleMessages.Count;
        }

        private bool IsVisible(ChatMessage message)
        {
            return message != null && (_showSystemMessages || message.Role != ChatRole.System);
        }

        private void RefreshInteractions()
        {
            var interactions = Bootstrap.ChatSession?.Interactions;
            if (interactions == null) return;
            var signature = string.Join("|", interactions.Select(interaction =>
                $"{interaction.Key}:{interaction.State}:{interaction.Error}:{InteractionContentLength(interaction)}"));
            if (signature == _interactionSignature) return;

            _interactionSignature = signature;
            _interactionsContainer.Clear();
            foreach (var interaction in interactions)
                _interactionsContainer.Add(new CoderChatInteractionElement(interaction));
            SetDisplayed(_interactionsContainer, interactions.Count > 0);
        }

        private static int InteractionContentLength(PendingInteraction interaction)
        {
            return interaction switch
            {
                PendingQuestion question => question.Question?.Length ?? 0,
                PendingPlan plan => plan.Plan?.Length ?? 0,
                PendingTasks tasks => tasks.Summary?.Length ?? 0,
                _ => 0,
            };
        }

        private void UpdateConnectionHeader()
        {
            var session = Bootstrap.ChatSession;
            if (session == null) return;

            _statusLabel.text = string.IsNullOrEmpty(session.ConnectionDetail)
                ? StateLabel(session.ConnectionState)
                : $"{StateLabel(session.ConnectionState)} · {session.ConnectionDetail}";
            SetStatusClass(session.ConnectionState);

            var remote = session.RemoteSession;
            var sessionId = remote?.Id ?? Settings.CoderSessionId;
            var title = remote?.Title;
            var model = remote?.Model ?? Settings.CoderModel;
            _sessionMetaLabel.text = sessionId > 0
                ? $"#{sessionId} · {(string.IsNullOrEmpty(title) ? "sessão Unity" : title)}"
                    + (string.IsNullOrEmpty(model) ? "" : $" · {model}")
                : "Nenhuma sessão · o envio criará uma automaticamente";

            var permission = remote?.ToolPermissionMode;
            _permissionBadge.text = string.IsNullOrEmpty(permission) ? "PERMISSÃO —" : permission.ToUpperInvariant();
            _permissionBadge.tooltip = string.IsNullOrEmpty(permission)
                ? "O runtime ainda não informou o modo de permissão desta sessão"
                : $"Modo de permissão do runtime: {permission}";
        }

        private void SetStatusClass(ChatConnectionState state)
        {
            foreach (var className in new[] { "state-idle", "state-running", "state-syncing", "state-connecting", "state-waiting", "state-error" })
                _statusDot.RemoveFromClassList(className);

            switch (state)
            {
                case ChatConnectionState.Idle: _statusDot.AddToClassList("state-idle"); break;
                case ChatConnectionState.Running: _statusDot.AddToClassList("state-running"); break;
                case ChatConnectionState.Syncing: _statusDot.AddToClassList("state-syncing"); break;
                case ChatConnectionState.Connecting: _statusDot.AddToClassList("state-connecting"); break;
                case ChatConnectionState.WaitingForApproval: _statusDot.AddToClassList("state-waiting"); break;
                case ChatConnectionState.Error: _statusDot.AddToClassList("state-error"); break;
            }
        }

        private void UpdateSendButton()
        {
            if (_sendButton == null) return;
            var running = Bootstrap.ChatClient?.IsRunning == true;
            _sendButton.text = running ? "Parar" : "Enviar";
            _sendButton.EnableInClassList("stop", running);
            _sendButton.tooltip = running ? "Cancelar a resposta atual" : "Enviar (Enter) · nova linha (Shift+Enter)";
        }

        private void RefreshCatalogControls()
        {
            RefreshAgentChoices();
            RefreshSessionChoices();
            UpdateModelButton();
        }

        private void RefreshAgentChoices()
        {
            if (_agentField == null) return;
            var agents = Bootstrap.CoderAgents != null && Bootstrap.CoderAgents.Count > 0
                ? Bootstrap.CoderAgents
                : FallbackAgents();

            _agentByLabel.Clear();
            var labels = new List<string>();
            foreach (var agent in agents)
            {
                if (agent == null || string.IsNullOrEmpty(agent.Id)) continue;
                var label = string.IsNullOrEmpty(agent.Label) ? agent.Id : agent.Label;
                if (_agentByLabel.ContainsKey(label)) label = $"{label} ({agent.Id})";
                _agentByLabel[label] = agent;
                labels.Add(label);
            }

            _agentField.choices = labels;
            var selected = _agentByLabel.FirstOrDefault(pair => pair.Value.Id == Settings.CoderAgent);
            if (string.IsNullOrEmpty(selected.Key) && labels.Count > 0) selected = new KeyValuePair<string, CoderAgentInfo>(labels[0], _agentByLabel[labels[0]]);
            if (!string.IsNullOrEmpty(selected.Key))
            {
                _agentField.SetValueWithoutNotify(selected.Key);
                _agentField.tooltip = selected.Value.Description ?? selected.Value.Label;
                if (!string.Equals(Settings.CoderAgent, selected.Value.Id, StringComparison.Ordinal))
                    Settings.CoderAgent = selected.Value.Id;
            }
        }

        private void RefreshSessionChoices()
        {
            if (_sessionField == null) return;
            _sessionByLabel.Clear();
            var labels = new List<string>();
            foreach (var session in Bootstrap.CoderSessions ?? new List<CoderSessionInfo>())
            {
                if (session == null || session.Id <= 0) continue;
                var label = $"{(string.IsNullOrEmpty(session.Title) ? "Sessão" : session.Title)} · #{session.Id}";
                if (_sessionByLabel.ContainsKey(label)) label += $" · {session.UpdatedAt:HH:mm}";
                _sessionByLabel[label] = session;
                labels.Add(label);
            }
            if (labels.Count == 0) labels.Add("Nenhuma sessão recente");
            _sessionField.choices = labels;
            var selected = _sessionByLabel.FirstOrDefault(pair => pair.Value.Id == Settings.CoderSessionId);
            _sessionField.SetValueWithoutNotify(string.IsNullOrEmpty(selected.Key) ? labels[0] : selected.Key);
        }

        private void RefreshEffortAvailability()
        {
            if (_effortField == null) return;
            var model = SelectedModelInfo();
            var canReason = model == null || model.CanReason;
            _effortField.SetEnabled(canReason);
            _effortField.SetValueWithoutNotify(canReason && Settings.CoderThinking ? "Pensar" : "Padrão");
            _effortField.tooltip = canReason
                ? "Padrão omite thinking; Pensar envia thinking=true"
                : "Este modelo informou can_reason=false";
        }

        private CoderModelInfo SelectedModelInfo()
        {
            var provider = Bootstrap.CoderProviders?.FirstOrDefault(item => item.Id == Settings.CoderProvider);
            return provider?.Models?.FirstOrDefault(item => item.Id == Settings.CoderModel);
        }

        private void UpdateModelButton()
        {
            if (_modelButton == null) return;
            var provider = string.IsNullOrEmpty(Settings.CoderProvider) ? "provider" : Settings.CoderProvider;
            var model = string.IsNullOrEmpty(Settings.CoderModel) ? "modelo padrão" : Settings.CoderModel;
            _modelButton.text = $"{provider} · {model}";
        }

        private void InitializeTranscriptScroll()
        {
            if (_transcriptList == null) return;
            _transcriptScroll = _transcriptList.Q<ScrollView>();
            if (_transcriptScroll == null) return;

            _transcriptScroll.RegisterCallback<WheelEvent>(_ =>
            {
                _scrollState.SuspendFollowing();
                _transcriptScroll.schedule.Execute(UpdateScrollStateFromUser);
            });
            _transcriptScroll.verticalScroller.RegisterCallback<PointerDownEvent>(_ => _scrollState.BeginUserDrag());
            _transcriptScroll.verticalScroller.RegisterCallback<PointerUpEvent>(_ =>
            {
                _scrollState.EndUserDrag();
                _transcriptScroll.schedule.Execute(UpdateScrollStateFromUser);
            });
            _transcriptScroll.verticalScroller.valueChanged += _ =>
            {
                if (_scrollState.CorrectionPending || _scrollState.IsUserDragging) return;
                UpdateScrollStateFromUser();
            };
            _transcriptScroll.contentContainer.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_scrollState.IsFollowingEnd) RequestScrollTranscriptToEnd();
            });
        }

        private void UpdateScrollStateFromUser()
        {
            if (_transcriptScroll == null) return;
            var scroller = _transcriptScroll.verticalScroller;
            _scrollState.ObserveUserPosition(scroller.value, scroller.highValue);
            if (_scrollState.IsFollowingEnd) SetDisplayed(_newMessagesButton, false);
        }

        private void WireNewMessagesButton()
        {
            _newMessagesButton.clicked += () =>
            {
                _scrollState.ResumeFollowing();
                RequestScrollTranscriptToEnd();
                SetDisplayed(_newMessagesButton, false);
            };
        }

        private void RequestScrollTranscriptToEnd()
        {
            if (_transcriptList == null || _visibleMessages.Count == 0 || !_scrollState.TryBeginCorrection()) return;
            _transcriptList.schedule.Execute(() =>
            {
                if (_transcriptList == null
                    || _visibleMessages.Count == 0
                    || !_scrollState.IsFollowingEnd
                    || !_scrollState.CorrectionPending)
                {
                    _scrollState.CancelCorrection();
                    return;
                }

                _transcriptList.ScrollToItem(_visibleMessages.Count - 1);
                _transcriptList.schedule.Execute(CompleteScrollCorrection);
            });
            SetDisplayed(_newMessagesButton, false);
        }

        private void CompleteScrollCorrection()
        {
            if (_transcriptScroll == null
                || !_scrollState.IsFollowingEnd
                || !_scrollState.CorrectionPending)
            {
                _scrollState.CancelCorrection();
                return;
            }
            _scrollState.CompleteCorrection();
        }

        private void RefreshRemoteCatalogs()
        {
            var client = Bootstrap.ChatClient;
            if (client == null) return;
            client.RefreshAgents(Settings.CoderUrl, Settings.CoderToken);
            if (Bootstrap.CoderProviders == null || Bootstrap.CoderProviders.Count == 0)
                client.RefreshProviders(Settings.CoderUrl, Settings.CoderToken);
            if (Settings.CoderProjectId > 0)
                client.RefreshSessions(Settings.CoderUrl, Settings.CoderToken, Settings.CoderProjectId);
        }

        private void ShowComposerFeedback(string message, bool error = true)
        {
            if (_composerFeedback == null) return;
            _composerFeedback.text = message ?? "";
            _composerFeedback.style.color = error
                ? new StyleColor(new Color(0.86f, 0.42f, 0.45f))
                : new StyleColor(StyleKeyword.Null);
            SetDisplayed(_composerFeedback, !string.IsNullOrEmpty(message));
        }

        private void HideComposerFeedback()
        {
            if (_composerFeedback != null) SetDisplayed(_composerFeedback, false);
        }

        private static string StateLabel(ChatConnectionState state)
        {
            switch (state)
            {
                case ChatConnectionState.Offline: return "Offline";
                case ChatConnectionState.Connecting: return "Conectando";
                case ChatConnectionState.Syncing: return "Sincronizando";
                case ChatConnectionState.Idle: return "Pronto";
                case ChatConnectionState.Running: return "Executando";
                case ChatConnectionState.WaitingForApproval: return "Aguardando aprovação";
                case ChatConnectionState.Error: return "Erro";
                default: return state.ToString();
            }
        }

        private static List<CoderAgentInfo> FallbackAgents()
        {
            return new List<CoderAgentInfo>
            {
                new() { Id = "plan", Label = "Plan", Description = "Planejar antes de alterar o projeto", Profile = "navigate", PlanMode = true },
                new() { Id = "build", Label = "Code", Description = "Investigar, implementar e validar", Profile = "auto" },
                new() { Id = "explore", Label = "Explore", Description = "Investigação somente leitura", Profile = "bootstrap" },
            };
        }

        private static VisualTreeAsset LoadVisualTree()
        {
            var package = PackageInfo.FindForAssembly(typeof(CoderChatWindow).Assembly);
            var packageName = string.IsNullOrEmpty(package?.name) ? "com.maicon.scene-analyzer" : package.name;
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"Packages/{packageName}/{UxmlRelativePath}");
            if (asset != null) return asset;
            return AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlRelativePath);
        }

        private void BuildAssetErrorFallback(string detail = null)
        {
            rootVisualElement.Clear();
            var label = new Label(
                "Não foi possível carregar a interface do Coder Chat.\n"
                + (detail ?? $"Asset esperado: {UxmlRelativePath}")
                + "\nReimporte o pacote e reabra esta janela.");
            label.AddToClassList("fatal-error");
            rootVisualElement.Add(label);
            rootVisualElement.Add(new Button(() => CreateGUI()) { text = "Tentar novamente" });
        }

        private static void SetDisplayed(VisualElement element, bool displayed)
        {
            if (element != null) element.style.display = displayed ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    internal sealed class CoderChatInvalidationState
    {
        public bool IsScheduled { get; private set; }

        public bool TrySchedule()
        {
            if (IsScheduled) return false;
            IsScheduled = true;
            return true;
        }

        public void Consume()
        {
            IsScheduled = false;
        }

        public void Cancel()
        {
            IsScheduled = false;
        }
    }

    internal sealed class CoderChatScrollState
    {
        internal const float EndThreshold = 32f;

        public bool IsFollowingEnd { get; private set; } = true;
        public bool CorrectionPending { get; private set; }
        public bool IsUserDragging { get; private set; }

        public void ObserveUserPosition(float value, float highValue)
        {
            if (IsUserDragging) return;
            CorrectionPending = false;
            IsFollowingEnd = DistanceFromEnd(value, highValue) <= EndThreshold;
        }

        public void BeginUserDrag()
        {
            IsUserDragging = true;
            SuspendFollowing();
        }

        public void EndUserDrag()
        {
            IsUserDragging = false;
        }

        public void SuspendFollowing()
        {
            CorrectionPending = false;
            IsFollowingEnd = false;
        }

        public void ResumeFollowing()
        {
            IsUserDragging = false;
            IsFollowingEnd = true;
        }

        public bool TryBeginCorrection()
        {
            if (!IsFollowingEnd || CorrectionPending) return false;
            CorrectionPending = true;
            return true;
        }

        public void CompleteCorrection()
        {
            CorrectionPending = false;
            IsFollowingEnd = true;
        }

        public void CancelCorrection()
        {
            CorrectionPending = false;
        }

        internal static float DistanceFromEnd(float value, float highValue)
        {
            return Mathf.Max(0f, highValue - value);
        }
    }
}
