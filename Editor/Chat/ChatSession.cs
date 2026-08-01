using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    public enum PendingInteractionState
    {
        Pending,
        Submitting,
        Failed,
    }

    public enum PendingInteractionKind
    {
        Question,
        Plan,
        Spec,
        Decision,
        Tasks,
        ToolApproval,
    }

    public sealed class PendingOption
    {
        public string Label;
        public string Value;

        public override string ToString() => string.IsNullOrEmpty(Label) ? Value ?? "Opção" : Label;
    }

    public abstract class PendingInteraction
    {
        public string Key;
        public PendingInteractionKind Kind;
        public PendingInteractionState State;
        public string Error;
        public string ResponseDraft;
        public DateTime CreatedAt = DateTime.Now;
        public string SourceToolUseId;
        public bool RecoveredFromToolCall;
    }

    public sealed class PendingQuestion : PendingInteraction
    {
        public string Id;
        public string Question;
        public readonly List<PendingOption> Choices = new();
        public List<string> Options = new();
        public bool AllowFreeform = true;
        public bool MultiSelect;
        public readonly HashSet<string> SelectedValues = new(StringComparer.Ordinal);

        public PendingQuestion() { Kind = PendingInteractionKind.Question; }
    }

    public sealed class PendingPlan : PendingInteraction
    {
        public string Id;
        public string Plan;

        public PendingPlan() { Kind = PendingInteractionKind.Plan; }
    }

    public sealed class PendingSpec : PendingInteraction
    {
        public string Id;
        public string Name;
        public string Body;
        public string SpecPath;

        public PendingSpec() { Kind = PendingInteractionKind.Spec; }
    }

    public sealed class PendingDecisionQuestion
    {
        public string Id;
        public string Header;
        public string Question;
        public bool MultiSelect;
        public readonly List<PendingOption> Choices = new();
        public readonly HashSet<string> SelectedValues = new(StringComparer.Ordinal);
        public string ResponseDraft;
    }

    public sealed class PendingDecision : PendingInteraction
    {
        public string Id;
        public string Header;
        public bool AllowFreeform;
        public readonly List<PendingDecisionQuestion> Questions = new();

        public PendingDecision() { Kind = PendingInteractionKind.Decision; }
    }

    public sealed class PendingTasks : PendingInteraction
    {
        public string ToolUseId;
        public string Summary;

        public PendingTasks() { Kind = PendingInteractionKind.Tasks; }
    }

    public sealed class PendingToolApproval : PendingInteraction
    {
        public string ToolUseId;
        public string ParentToolUseId;
        public string ToolName;
        public string ArgsSummary;
        public string InputJson;
        public string RiskClass;
        public string ApprovalKind;
        public bool Nested;
        public bool AlwaysAllow;

        public PendingToolApproval() { Kind = PendingInteractionKind.ToolApproval; }
    }

    public enum ChatConnectionState
    {
        Offline,
        Connecting,
        Syncing,
        Idle,
        Running,
        WaitingForApproval,
        Error,
    }

    [Flags]
    public enum ChatSessionInvalidation
    {
        None = 0,
        Header = 1 << 0,
        Transcript = 1 << 1,
        Interactions = 1 << 2,
        SessionControls = 1 << 3,
        All = Header | Transcript | Interactions | SessionControls,
    }

    public sealed class ChatSession
    {
        public readonly List<ChatMessage> Messages = new();
        public readonly List<PendingInteraction> Interactions = new();
        public ChatMessage StreamingAssistant;
        public string LastEventId;
        public bool IsRunning;
        public string LastError;
        public ChatConnectionState ConnectionState = ChatConnectionState.Offline;
        public string ConnectionDetail;
        public CoderSessionInfo RemoteSession;
        public long LastRemoteMessageId;
        public long EarliestRemoteMessageId;
        public long? NextHistoryBeforeSequence;
        public int TotalRemoteMessages;
        public int ArchivedRemoteMessages;
        public bool HasEarlierMessages;
        public int Revision { get; private set; }

        public PendingQuestion CurrentQuestion => Interactions.OfType<PendingQuestion>().FirstOrDefault();
        public PendingPlan CurrentPlan => Interactions.OfType<PendingPlan>().FirstOrDefault();
        public PendingSpec CurrentSpec => Interactions.OfType<PendingSpec>().FirstOrDefault();
        public PendingDecision CurrentDecision => Interactions.OfType<PendingDecision>().FirstOrDefault();
        public PendingTasks CurrentTasks => Interactions.OfType<PendingTasks>().FirstOrDefault();
        public IEnumerable<PendingToolApproval> PendingToolApprovals => Interactions.OfType<PendingToolApproval>();

        public event Action OnChanged;
        public event Action<ChatSessionInvalidation> OnInvalidated;

        private int _batchDepth;
        private ChatSessionInvalidation _invalidatedInBatch;
        private bool _historyExpanded;

        public IDisposable BeginBatch()
        {
            _batchDepth++;
            return new BatchScope(this);
        }

        public void ApplyBatch(Action action)
        {
            using (BeginBatch()) action?.Invoke();
        }

        public void AddUser(string text, IEnumerable<ChatReference> references = null)
        {
            var message = new ChatMessage { Role = ChatRole.User, Text = text ?? "" };
            if (references != null) message.References.AddRange(references.Where(reference => reference != null));
            Messages.Add(message);
            MarkChanged(ChatSessionInvalidation.Transcript);
        }

        public void StartAssistant()
        {
            if (StreamingAssistant != null)
            {
                IsRunning = true;
                SetConnectionState(ChatConnectionState.Running, "Recebendo resposta");
                return;
            }
            StreamingAssistant = new ChatMessage { Role = ChatRole.Assistant, Streaming = true };
            Messages.Add(StreamingAssistant);
            IsRunning = true;
            LastError = null;
            ConnectionState = ChatConnectionState.Running;
            ConnectionDetail = "Recebendo resposta";
            MarkChanged(ChatSessionInvalidation.Transcript | ChatSessionInvalidation.Header);
        }

        public void AppendAssistantText(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            if (StreamingAssistant == null) StartAssistant();
            StreamingAssistant.Text += delta;
            StreamingAssistant.Touch();
            MarkChanged(ChatSessionInvalidation.Transcript);
        }

        public void AppendAssistantThinking(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            if (StreamingAssistant == null) StartAssistant();
            StreamingAssistant.ThinkingText += delta;
            StreamingAssistant.Touch();
            MarkChanged(ChatSessionInvalidation.Transcript);
        }

        public void AddToolCall(string id, string name, string inputJson)
        {
            if (StreamingAssistant == null) StartAssistant();
            var existing = StreamingAssistant.ToolCalls.Find(call => call.Id == id);
            if (existing == null)
            {
                StreamingAssistant.ToolCalls.Add(new ChatToolCall
                {
                    Id = id,
                    Name = name,
                    InputJson = inputJson,
                });
            }
            else
            {
                existing.Name = name;
                existing.InputJson = inputJson;
            }
            RecoverInteractionFromToolCall(new ChatToolCall
            {
                Id = id,
                Name = name,
                InputJson = inputJson,
            });
            StreamingAssistant.Touch();
            MarkChanged(ChatSessionInvalidation.Transcript
                | ChatSessionInvalidation.Header
                | ChatSessionInvalidation.Interactions);
        }

        public void CompleteToolCall(string id, string resultText, double durationMs)
        {
            if (StreamingAssistant == null) return;
            var call = StreamingAssistant.ToolCalls.Find(candidate => candidate.Id == id);
            if (call == null) return;
            call.ResultText = resultText;
            call.DurationMs = durationMs;
            call.Completed = true;
            ClearInteractionForCompletedTool(call);
            StreamingAssistant.Touch();
            MarkChanged(ChatSessionInvalidation.Transcript
                | ChatSessionInvalidation.Header
                | ChatSessionInvalidation.Interactions);
        }

        public bool CompletePersistedToolCall(string id, string resultText, double durationMs)
        {
            if (string.IsNullOrEmpty(id)) return false;
            for (var messageIndex = Messages.Count - 1; messageIndex >= 0; messageIndex--)
            {
                var message = Messages[messageIndex];
                if (!message.IsRemote) continue;
                var call = message.ToolCalls.Find(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (call == null) continue;
                if (call.Completed) return true;
                call.ResultText = resultText;
                call.DurationMs = durationMs;
                call.Completed = true;
                ClearInteractionForCompletedTool(call);
                message.Touch();
                MarkChanged(ChatSessionInvalidation.Transcript
                    | ChatSessionInvalidation.Header
                    | ChatSessionInvalidation.Interactions);
                return true;
            }
            return false;
        }

        public void AddSystemNote(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Messages.Add(new ChatMessage { Role = ChatRole.System, Text = text });
            MarkChanged(ChatSessionInvalidation.Transcript);
        }

        public void AddError(string text)
        {
            LastError = text;
            Messages.Add(new ChatMessage { Role = ChatRole.Error, Text = text ?? "Erro desconhecido" });
            IsRunning = false;
            ConnectionState = ChatConnectionState.Error;
            ConnectionDetail = text;
            MarkChanged(ChatSessionInvalidation.Transcript | ChatSessionInvalidation.Header);
        }

        public void FinishAssistant()
        {
            if (StreamingAssistant != null)
            {
                if (IsEmptyAssistant(StreamingAssistant))
                    Messages.Remove(StreamingAssistant);
                else
                {
                    StreamingAssistant.Streaming = false;
                    StreamingAssistant.Touch();
                }
            }
            StreamingAssistant = null;
            IsRunning = false;
            ConnectionState = Interactions.Count > 0 ? ChatConnectionState.WaitingForApproval : ChatConnectionState.Idle;
            ConnectionDetail = Interactions.Count > 0 ? "Aguardando sua resposta" : "Pronto";
            MarkChanged(ChatSessionInvalidation.Transcript
                | ChatSessionInvalidation.Header
                | ChatSessionInvalidation.Interactions);
        }

        public void AbortAssistant()
        {
            using (BeginBatch())
            {
                if (StreamingAssistant != null)
                {
                    foreach (var call in StreamingAssistant.ToolCalls.Where(call => !call.Completed))
                    {
                        call.Completed = true;
                        call.Failed = true;
                        call.ResultText = "Interrompida pelo usuário.";
                    }

                    if (IsEmptyAssistant(StreamingAssistant))
                        Messages.Remove(StreamingAssistant);
                    else
                    {
                        StreamingAssistant.Streaming = false;
                        StreamingAssistant.Touch();
                    }
                }

                StreamingAssistant = null;
                Interactions.Clear();
                IsRunning = false;
                LastError = null;
                ConnectionState = ChatConnectionState.Idle;
                ConnectionDetail = "Execução interrompida";
                MarkChanged(ChatSessionInvalidation.Transcript
                    | ChatSessionInvalidation.Header
                    | ChatSessionInvalidation.Interactions);
            }
        }

        public void SetConnectionState(ChatConnectionState state, string detail = null)
        {
            if (ConnectionState == state && ConnectionDetail == detail) return;
            ConnectionState = state;
            ConnectionDetail = detail;
            MarkChanged(ChatSessionInvalidation.Header);
        }

        public void UpdateRemoteSession(CoderSessionInfo remote)
        {
            if (remote == null || !CoderSessionIdentity.IsValid(remote.Id)) return;
            if (RemoteSession != null && RemoteSession.SemanticallyEquals(remote)) return;
            RemoteSession = remote;
            MarkChanged(ChatSessionInvalidation.Header | ChatSessionInvalidation.SessionControls);
        }

        public void Reset(bool keepSystemNotes = false)
        {
            if (keepSystemNotes) Messages.RemoveAll(message => message.Role != ChatRole.System && message.Role != ChatRole.Error);
            else Messages.Clear();
            StreamingAssistant = null;
            LastEventId = null;
            IsRunning = false;
            LastError = null;
            Interactions.Clear();
            RemoteSession = null;
            LastRemoteMessageId = 0;
            EarliestRemoteMessageId = 0;
            NextHistoryBeforeSequence = null;
            TotalRemoteMessages = 0;
            ArchivedRemoteMessages = 0;
            HasEarlierMessages = false;
            _historyExpanded = false;
            ConnectionState = ChatConnectionState.Offline;
            ConnectionDetail = null;
            MarkChanged(ChatSessionInvalidation.All);
        }

        /// A cursorless GET /attach replays a bounded semantic window, not the
        /// complete persisted turn. Keep the authoritative snapshot intact and
        /// let CoderApiClient consume replayed message/tool identities while it
        /// builds only the still-live suffix into StreamingAssistant.
        public void PrepareForLiveReplay()
        {
            Messages.RemoveAll(message => !message.IsRemote && IsEmptyAssistant(message));
            StreamingAssistant = null;
            Interactions.Clear();
            RecoverPendingInteractionsFromMessages();
            IsRunning = false;
            MarkChanged(ChatSessionInvalidation.Transcript
                | ChatSessionInvalidation.Header
                | ChatSessionInvalidation.Interactions);
        }

        public void MergeRemoteSnapshot(CoderSessionSnapshot snapshot, bool replaceConversation = false)
        {
            if (snapshot == null) return;
            using (BeginBatch())
            {
                var removedEmptyAssistants = Messages.RemoveAll(message =>
                    !message.IsRemote && IsEmptyAssistant(message));
                var changed = RemoteSession == null
                    ? snapshot.Session != null
                    : !RemoteSession.SemanticallyEquals(snapshot.Session);
                changed |= removedEmptyAssistants > 0;
                if (changed) RemoteSession = snapshot.Session;

                var reconciled = replaceConversation
                    ? new List<ChatMessage>(snapshot.Messages.Count)
                    : null;
                var reconciledSet = reconciled == null ? null : new HashSet<ChatMessage>();
                var remoteMessagesById = Messages
                    .Where(message => !string.IsNullOrEmpty(message.RemoteId))
                    .GroupBy(RemoteMessageKey, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (var remote in snapshot.Messages)
                {
                    foreach (var reference in remote.References) reference?.Validate();
                    var existing = !string.IsNullOrEmpty(remote.RemoteId)
                        && remoteMessagesById.TryGetValue(RemoteMessageKey(remote), out var indexed)
                        ? indexed
                        : null;
                    if (existing == null) existing = FindEquivalentLocal(remote);
                    if (existing != null && reconciledSet != null && !reconciledSet.Add(existing)) existing = null;
                    if (existing != null)
                    {
                        changed |= existing.CopyRemoteContentFrom(remote);
                        reconciled?.Add(existing);
                    }
                    else if (reconciled != null)
                    {
                        reconciled.Add(remote);
                        changed = true;
                    }
                    else
                    {
                        Messages.Add(remote);
                        changed = true;
                    }
                }

                if (replaceConversation) _historyExpanded = false;
                var nextEarliest = snapshot.EarliestMessageId;
                if (!replaceConversation && EarliestRemoteMessageId > 0 && nextEarliest > 0)
                    nextEarliest = Math.Min(EarliestRemoteMessageId, nextEarliest);
                if (EarliestRemoteMessageId != nextEarliest)
                {
                    EarliestRemoteMessageId = nextEarliest;
                    changed = true;
                }
                var nextTotal = Math.Max(TotalRemoteMessages, snapshot.TotalMessages);
                if (TotalRemoteMessages != nextTotal)
                {
                    TotalRemoteMessages = nextTotal;
                    changed = true;
                }
                if (ArchivedRemoteMessages != snapshot.ArchivedCount)
                {
                    ArchivedRemoteMessages = snapshot.ArchivedCount;
                    changed = true;
                }
                if (!_historyExpanded)
                {
                    if (HasEarlierMessages != snapshot.HasEarlierMessages)
                    {
                        HasEarlierMessages = snapshot.HasEarlierMessages;
                        changed = true;
                    }
                    var nextCursor = snapshot.HasEarlierMessages && snapshot.EarliestMessageId > 0
                        ? snapshot.EarliestMessageId
                        : (long?)null;
                    if (NextHistoryBeforeSequence != nextCursor)
                    {
                        NextHistoryBeforeSequence = nextCursor;
                        changed = true;
                    }
                }

                if (reconciled != null)
                {
                    var targetMessages = Messages
                        .Where(message => (message.Role == ChatRole.System || message.Role == ChatRole.Error)
                            && !reconciled.Contains(message))
                        .Concat(reconciled)
                        .ToList();
                    if (!Messages.SequenceEqual(targetMessages))
                    {
                        Messages.Clear();
                        Messages.AddRange(targetMessages);
                        changed = true;
                    }
                }

                var nextLastRemoteMessageId = Math.Max(LastRemoteMessageId, snapshot.LastMessageId);
                if (LastRemoteMessageId != nextLastRemoteMessageId)
                {
                    LastRemoteMessageId = nextLastRemoteMessageId;
                    changed = true;
                }

                changed |= RecoverPendingInteractionsFromMessages();

                if (!IsRunning)
                {
                    var nextState = Interactions.Count > 0
                        ? ChatConnectionState.WaitingForApproval
                        : ChatConnectionState.Idle;
                    var nextDetail = Interactions.Count > 0 ? "Aguardando sua resposta" : "Sincronizado";
                    if (ConnectionState != nextState || !string.Equals(ConnectionDetail, nextDetail, StringComparison.Ordinal))
                    {
                        ConnectionState = nextState;
                        ConnectionDetail = nextDetail;
                        changed = true;
                    }
                }

                if (changed) MarkChanged(ChatSessionInvalidation.All);
            }
        }

        public void MergeEarlierMessages(CoderMessagePage page)
        {
            if (page == null) return;
            using (BeginBatch())
            {
                _historyExpanded = true;
                var existingById = Messages
                    .Where(message => !string.IsNullOrEmpty(message.RemoteId))
                    .GroupBy(RemoteMessageKey, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                var earlier = new List<ChatMessage>();
                foreach (var remote in page.Messages)
                {
                    foreach (var reference in remote.References) reference?.Validate();
                    if (!string.IsNullOrEmpty(remote.RemoteId)
                        && existingById.TryGetValue(RemoteMessageKey(remote), out var existing))
                    {
                        existing.CopyRemoteContentFrom(remote);
                        continue;
                    }
                    earlier.Add(remote);
                    if (!string.IsNullOrEmpty(remote.RemoteId))
                        existingById[RemoteMessageKey(remote)] = remote;
                }

                if (earlier.Count > 0)
                {
                    var insertionIndex = Messages.FindIndex(message => message.IsRemote);
                    if (insertionIndex < 0) insertionIndex = Messages.Count;
                    Messages.InsertRange(insertionIndex, earlier);
                    var earliest = earlier
                        .Select(message => ParseRemoteSequence(message.RemoteId))
                        .Where(sequence => sequence > 0)
                        .DefaultIfEmpty(EarliestRemoteMessageId)
                        .Min();
                    if (earliest > 0) EarliestRemoteMessageId = earliest;
                }

                NextHistoryBeforeSequence = page.NextBeforeSequence;
                HasEarlierMessages = page.HasMore;
                MarkChanged(ChatSessionInvalidation.Transcript | ChatSessionInvalidation.Header);
            }
        }

        private static bool IsEmptyAssistant(ChatMessage message)
            => message != null
                && message.Role == ChatRole.Assistant
                && string.IsNullOrWhiteSpace(message.Text)
                && string.IsNullOrWhiteSpace(message.ThinkingText)
                && message.ToolCalls.Count == 0;

        public void SetPendingQuestion(PendingQuestion question)
        {
            if (question == null) return;
            if (question.Choices.Count == 0 && question.Options != null)
                foreach (var option in question.Options) question.Choices.Add(new PendingOption { Label = option, Value = option });
            question.Key = string.IsNullOrEmpty(question.Key) ? "question:" + (question.Id ?? Guid.NewGuid().ToString("N")) : question.Key;
            UpsertSingleInteraction(question);
        }

        public void ClearPendingQuestion() => RemoveInteraction(CurrentQuestion?.Key);

        public void SetPendingPlan(PendingPlan plan)
        {
            if (plan == null) return;
            plan.Key = string.IsNullOrEmpty(plan.Key) ? "plan:" + (plan.Id ?? Guid.NewGuid().ToString("N")) : plan.Key;
            UpsertSingleInteraction(plan);
        }

        public void ClearPendingPlan() => RemoveInteraction(CurrentPlan?.Key);

        public void SetPendingSpec(PendingSpec spec)
        {
            if (spec == null) return;
            spec.Key = string.IsNullOrEmpty(spec.Key) ? "spec:" + (spec.Id ?? Guid.NewGuid().ToString("N")) : spec.Key;
            UpsertSingleInteraction(spec);
        }

        public void ClearPendingSpec() => RemoveInteraction(CurrentSpec?.Key);

        public void SetPendingDecision(PendingDecision decision)
        {
            if (decision == null) return;
            decision.Key = string.IsNullOrEmpty(decision.Key)
                ? "decision:" + (decision.Id ?? Guid.NewGuid().ToString("N"))
                : decision.Key;
            UpsertSingleInteraction(decision);
        }

        public void ClearPendingDecision() => RemoveInteraction(CurrentDecision?.Key);

        public void SetPendingTasks(PendingTasks tasks)
        {
            if (tasks == null) return;
            tasks.Key = string.IsNullOrEmpty(tasks.Key) ? "tasks:" + (tasks.ToolUseId ?? Guid.NewGuid().ToString("N")) : tasks.Key;
            UpsertSingleInteraction(tasks);
        }

        public void ClearPendingTasks() => RemoveInteraction(CurrentTasks?.Key);

        public void SetPendingToolApproval(PendingToolApproval approval)
        {
            if (approval == null) return;
            approval.Key = string.IsNullOrEmpty(approval.Key)
                ? "approval:" + (approval.ToolUseId ?? Guid.NewGuid().ToString("N"))
                : approval.Key;
            UpsertInteraction(approval);
        }

        public void ClearPendingToolApproval(string toolUseId)
            => RemoveInteraction("approval:" + (toolUseId ?? ""));

        public void SetInteractionSubmitting(string key)
        {
            var interaction = FindInteraction(key);
            if (interaction == null) return;
            interaction.State = PendingInteractionState.Submitting;
            interaction.Error = null;
            MarkChanged(ChatSessionInvalidation.Header | ChatSessionInvalidation.Interactions);
        }

        public void SetInteractionFailed(string key, string error)
        {
            var interaction = FindInteraction(key);
            if (interaction == null) return;
            interaction.State = PendingInteractionState.Failed;
            interaction.Error = error;
            MarkChanged(ChatSessionInvalidation.Header | ChatSessionInvalidation.Interactions);
        }

        public void CompleteInteraction(string key) => RemoveInteraction(key);

        public PendingInteraction FindInteraction(string key)
        {
            return string.IsNullOrEmpty(key) ? null : Interactions.Find(item => item.Key == key);
        }

        private void UpsertInteraction(PendingInteraction interaction)
        {
            var existingIndex = Interactions.FindIndex(item => item.Key == interaction.Key);
            if (existingIndex >= 0) Interactions[existingIndex] = interaction;
            else Interactions.Add(interaction);
            ConnectionState = ChatConnectionState.WaitingForApproval;
            ConnectionDetail = "Aguardando sua resposta";
            MarkChanged(ChatSessionInvalidation.Header | ChatSessionInvalidation.Interactions);
        }

        private void UpsertSingleInteraction(PendingInteraction interaction)
        {
            Interactions.RemoveAll(item => item.Kind == interaction.Kind && item.Key != interaction.Key);
            UpsertInteraction(interaction);
        }

        public bool RecoverPendingInteractionsFromMessages()
        {
            ChatToolCall pending = null;
            for (var messageIndex = Messages.Count - 1; messageIndex >= 0; messageIndex--)
            {
                var calls = Messages[messageIndex]?.ToolCalls;
                if (calls == null) continue;
                for (var callIndex = calls.Count - 1; callIndex >= 0; callIndex--)
                {
                    var call = calls[callIndex];
                    if (call == null || call.Completed || !IsInteractiveToolName(call.Name)) continue;
                    pending = call;
                    break;
                }
                if (pending != null) break;
            }

            if (pending == null)
                return Interactions.RemoveAll(item => item.RecoveredFromToolCall) > 0;

            var kind = InteractionKindForTool(pending.Name);
            var changed = Interactions.RemoveAll(item => item.RecoveredFromToolCall
                && (item.Kind != kind
                    || !string.Equals(item.SourceToolUseId, pending.Id, StringComparison.Ordinal))) > 0;
            if (Interactions.Any(item => item.Kind == kind
                && (!item.RecoveredFromToolCall
                    || string.Equals(item.SourceToolUseId, pending.Id, StringComparison.Ordinal))))
                return changed;
            return RecoverInteractionFromToolCall(pending) || changed;
        }

        public static bool IsInteractiveToolName(string name)
        {
            switch ((name ?? "").Trim())
            {
                case "ask_user":
                case "decision_gate":
                case "submit_plan":
                case "submit_spec":
                case "submit_specification_to_master":
                case "submit_tasks":
                case "task_list":
                    return true;
                default:
                    return false;
            }
        }

        private bool RecoverInteractionFromToolCall(ChatToolCall call)
        {
            if (call == null || call.Completed || !IsInteractiveToolName(call.Name)) return false;
            JObject input;
            try { input = string.IsNullOrWhiteSpace(call.InputJson) ? new JObject() : JObject.Parse(call.InputJson); }
            catch { input = new JObject(); }

            PendingInteraction interaction;
            switch (call.Name)
            {
                case "ask_user":
                {
                    var question = new PendingQuestion
                    {
                        Id = call.Id,
                        Question = (string)input["question"] ?? "O agente precisa de uma resposta.",
                        AllowFreeform = (bool?)input["allow_freeform"] ?? input["options"] == null,
                        MultiSelect = (bool?)input["multi_select"] ?? false,
                    };
                    AddOptions(question.Choices, input["options"] as JArray);
                    interaction = question;
                    break;
                }
                case "decision_gate":
                {
                    var decision = new PendingDecision
                    {
                        Id = call.Id,
                        Header = (string)input["header"],
                        AllowFreeform = (bool?)input["allow_freeform"] ?? false,
                    };
                    foreach (var token in input["questions"] as JArray ?? new JArray())
                    {
                        var question = new PendingDecisionQuestion
                        {
                            Id = (string)token["id"] ?? Guid.NewGuid().ToString("N"),
                            Header = (string)token["header"],
                            Question = (string)token["question"] ?? "Escolha uma opção.",
                            MultiSelect = (bool?)token["multi_select"] ?? false,
                        };
                        AddOptions(question.Choices, token["options"] as JArray);
                        decision.Questions.Add(question);
                    }
                    interaction = decision;
                    break;
                }
                case "submit_plan":
                    interaction = new PendingPlan { Id = call.Id, Plan = (string)input["plan"] ?? call.InputJson };
                    break;
                case "submit_spec":
                case "submit_specification_to_master":
                    interaction = new PendingSpec
                    {
                        Id = call.Id,
                        Name = (string)input["name"] ?? "Especificação",
                        Body = (string)input["body"] ?? call.InputJson,
                        SpecPath = (string)input["spec_path"] ?? "",
                    };
                    break;
                default:
                    interaction = new PendingTasks
                    {
                        ToolUseId = call.Id,
                        Summary = (string)input["summary"] ?? "O agente publicou uma lista de tarefas.",
                    };
                    break;
            }

            if (Interactions.Any(item => item.Kind == interaction.Kind && !item.RecoveredFromToolCall)) return false;
            interaction.SourceToolUseId = call.Id;
            interaction.RecoveredFromToolCall = true;
            switch (interaction)
            {
                case PendingQuestion question: SetPendingQuestion(question); break;
                case PendingPlan plan: SetPendingPlan(plan); break;
                case PendingSpec spec: SetPendingSpec(spec); break;
                case PendingDecision decision: SetPendingDecision(decision); break;
                case PendingTasks tasks: SetPendingTasks(tasks); break;
            }
            return true;
        }

        private static void AddOptions(ICollection<PendingOption> target, JArray options)
        {
            if (options == null) return;
            foreach (var token in options)
            {
                var value = (string)token["value"] ?? (string)token["label"] ?? "";
                target.Add(new PendingOption
                {
                    Label = (string)token["label"] ?? value,
                    Value = value,
                });
            }
        }

        private void ClearInteractionForCompletedTool(ChatToolCall call)
        {
            if (call == null || !IsInteractiveToolName(call.Name)) return;
            var removed = Interactions.RemoveAll(item =>
                item.Kind == InteractionKindForTool(call.Name)
                || string.Equals(item.SourceToolUseId, call.Id, StringComparison.Ordinal));
            if (removed == 0) return;
            if (Interactions.Count == 0 && !IsRunning)
            {
                ConnectionState = ChatConnectionState.Idle;
                ConnectionDetail = "Pronto";
            }
        }

        private static PendingInteractionKind InteractionKindForTool(string name)
        {
            switch (name)
            {
                case "ask_user": return PendingInteractionKind.Question;
                case "decision_gate": return PendingInteractionKind.Decision;
                case "submit_plan": return PendingInteractionKind.Plan;
                case "submit_spec":
                case "submit_specification_to_master": return PendingInteractionKind.Spec;
                default: return PendingInteractionKind.Tasks;
            }
        }

        private void RemoveInteraction(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (Interactions.RemoveAll(item => item.Key == key) == 0) return;
            if (Interactions.Count == 0 && !IsRunning)
            {
                ConnectionState = ChatConnectionState.Idle;
                ConnectionDetail = "Pronto";
            }
            MarkChanged(ChatSessionInvalidation.Header | ChatSessionInvalidation.Interactions);
        }

        private ChatMessage FindEquivalentLocal(ChatMessage remote)
        {
            for (var index = Messages.Count - 1; index >= 0; index--)
            {
                var local = Messages[index];
                if (local.IsRemote || !string.IsNullOrEmpty(local.RemoteId) || local.Role != remote.Role) continue;
                if (EquivalentText(local.Text, remote.Text) && EquivalentToolCalls(local.ToolCalls, remote.ToolCalls)) return local;
            }
            return null;
        }

        private static string RemoteMessageKey(ChatMessage message)
            => (message?.RemoteSessionId ?? "") + "\u001f" + (message?.RemoteId ?? "");

        private static long ParseRemoteSequence(string remoteId)
            => long.TryParse(remoteId, out var sequence) && sequence > 0 ? sequence : 0;

        private static bool EquivalentText(string local, string remote)
        {
            local = local ?? "";
            remote = remote ?? "";
            if (local == remote) return true;
            return local.Length > 0 && remote.EndsWith(local, StringComparison.Ordinal);
        }

        private static bool EquivalentToolCalls(List<ChatToolCall> local, List<ChatToolCall> remote)
        {
            if (local.Count == 0 || remote.Count == 0) return local.Count == remote.Count;
            if (local.Count != remote.Count) return false;
            for (var index = 0; index < local.Count; index++)
                if (!string.Equals(local[index].Id, remote[index].Id, StringComparison.Ordinal)) return false;
            return true;
        }

        private void MarkChanged(ChatSessionInvalidation invalidation = ChatSessionInvalidation.All)
        {
            Revision++;
            if (_batchDepth > 0)
            {
                _invalidatedInBatch |= invalidation;
                return;
            }
            Notify(invalidation);
        }

        private void EndBatch()
        {
            if (_batchDepth == 0) return;
            _batchDepth--;
            if (_batchDepth != 0 || _invalidatedInBatch == ChatSessionInvalidation.None) return;
            var invalidation = _invalidatedInBatch;
            _invalidatedInBatch = ChatSessionInvalidation.None;
            Notify(invalidation);
        }

        private void Notify(ChatSessionInvalidation invalidation)
        {
            try { OnInvalidated?.Invoke(invalidation); }
            catch (Exception exception) { UnityEngine.Debug.LogException(exception); }
            try { OnChanged?.Invoke(); }
            catch (Exception exception) { UnityEngine.Debug.LogException(exception); }
        }

        private sealed class BatchScope : IDisposable
        {
            private ChatSession _owner;

            public BatchScope(ChatSession owner) { _owner = owner; }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                owner?.EndBatch();
            }
        }
    }
}
