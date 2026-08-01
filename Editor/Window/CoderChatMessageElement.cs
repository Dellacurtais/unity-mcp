using System;
using System.Collections.Generic;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Chat;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine.UIElements;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    internal static class ChatMessagePresentation
    {
        public static bool IsCompactActivity(ChatMessage message)
            => message != null
                && message.Role == ChatRole.Assistant
                && string.IsNullOrWhiteSpace(message.Text)
                && string.IsNullOrWhiteSpace(message.ThinkingText)
                && message.ToolCalls.Count > 0;

        public static string CleanMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var normalized = value.Replace("\r\n", "\n")
                .Replace("**", "")
                .Replace("__", "")
                .Replace("`", "");
            var lines = normalized.Split('\n');
            var output = new StringBuilder(normalized.Length);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var contentStart = HeadingContentStart(line);
                if (contentStart > 0) line = line.Substring(contentStart);
                else if (line.StartsWith("- ", StringComparison.Ordinal)
                    || line.StartsWith("* ", StringComparison.Ordinal))
                    line = "• " + line.Substring(2);
                if (index > 0) output.Append('\n');
                output.Append(line);
            }
            return output.ToString();
        }

        public static string ToolName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Ferramenta";
            var name = value.Trim();
            foreach (var prefix in new[] { "ext_unity__", "mcp__unity__", "unity__" })
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                name = name.Substring(prefix.Length);
                return "Unity · " + Humanize(name);
            }
            return Humanize(name);
        }

        private static int HeadingContentStart(string line)
        {
            if (string.IsNullOrEmpty(line) || line[0] != '#') return 0;
            var index = 0;
            while (index < line.Length && line[index] == '#') index++;
            return index > 0 && index < line.Length && line[index] == ' ' ? index + 1 : 0;
        }

        private static string Humanize(string value)
        {
            var words = (value ?? "").Replace('_', ' ').Trim();
            return string.IsNullOrEmpty(words) ? "Ferramenta" : words;
        }
    }

    internal sealed class CoderChatMessageElement : VisualElement
    {
        private readonly VisualElement _bubble;
        private readonly VisualElement _header;
        private readonly Label _role;
        private readonly Label _time;
        private readonly Label _streaming;
        private readonly Label _body;
        private readonly Foldout _thinking;
        private readonly Label _thinkingText;
        private readonly VisualElement _tools;
        private readonly VisualElement _references;
        private readonly Dictionary<string, ToolCardElement> _toolCards = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ReferenceChipElement> _referenceChips = new(StringComparer.Ordinal);

        public ChatMessage BoundMessage { get; private set; }
        public int BoundRevision { get; private set; } = -1;

        public CoderChatMessageElement()
        {
            AddToClassList("message-row");

            _bubble = new VisualElement();
            _bubble.AddToClassList("message-bubble");
            Add(_bubble);

            _header = new VisualElement();
            _header.AddToClassList("message-header");
            _bubble.Add(_header);

            _role = new Label();
            _role.AddToClassList("message-role");
            _header.Add(_role);

            _streaming = new Label("recebendo…");
            _streaming.AddToClassList("message-streaming");
            _header.Add(_streaming);

            _time = new Label();
            _time.AddToClassList("message-time");
            _header.Add(_time);

            _body = new Label();
            _body.AddToClassList("message-body");
            _bubble.Add(_body);

            _thinking = new Foldout { text = "Raciocínio", value = false };
            _thinking.AddToClassList("message-thinking");
            _thinking.RegisterValueChangedCallback(evt =>
            {
                if (BoundMessage != null) BoundMessage.ThinkingExpanded = evt.newValue;
            });
            _thinkingText = new Label();
            _thinkingText.AddToClassList("message-body");
            _thinking.Add(_thinkingText);
            _bubble.Add(_thinking);

            _tools = new VisualElement();
            _tools.AddToClassList("tool-list");
            _bubble.Add(_tools);

            _references = new VisualElement();
            _references.AddToClassList("message-references");
            _bubble.Add(_references);
        }

        public void Bind(ChatMessage message)
            => BindCore(message, true);

        public void BindForTranscript(ChatMessage message, bool showHeader)
            => BindCore(message, showHeader);

        private void BindCore(ChatMessage message, bool showHeader)
        {
            if (message == null)
            {
                ClearBinding();
                return;
            }

            var messageChanged = !ReferenceEquals(BoundMessage, message);
            BoundMessage = message;
            BoundRevision = message.Revision;

            if (messageChanged)
            {
                ClearRoleClasses();
                AddToClassList(RoleClass(message.Role));
                _role.text = RoleLabel(message.Role);
                _time.text = message.Timestamp.ToString("HH:mm:ss");
            }

            var compactActivity = ChatMessagePresentation.IsCompactActivity(message);
            EnableInClassList("compact-activity", compactActivity);
            SetDisplayed(_header, showHeader && !compactActivity);
            SetTextIfChanged(_body, ChatMessagePresentation.CleanMarkdown(message.Text));
            SetDisplayed(_body, !string.IsNullOrEmpty(message.Text));
            SetDisplayed(_streaming, message.Streaming);

            var hasThinking = !string.IsNullOrEmpty(message.ThinkingText);
            _thinking.SetValueWithoutNotify(message.ThinkingExpanded);
            SetTextIfChanged(_thinkingText, ChatMessagePresentation.CleanMarkdown(message.ThinkingText));
            SetDisplayed(_thinking, hasThinking);

            ReconcileToolCards(message.ToolCalls);
            ReconcileReferenceChips(message.References);
        }

        private void ClearBinding()
        {
            BoundMessage = null;
            BoundRevision = -1;
            ClearRoleClasses();
            _role.text = "";
            _time.text = "";
            _body.text = "";
            _thinkingText.text = "";
            _tools.Clear();
            _references.Clear();
            _toolCards.Clear();
            _referenceChips.Clear();
            RemoveFromClassList("compact-activity");
            SetDisplayed(_header, false);
            SetDisplayed(_body, false);
            SetDisplayed(_streaming, false);
            SetDisplayed(_thinking, false);
            SetDisplayed(_tools, false);
            SetDisplayed(_references, false);
        }

        private void ReconcileToolCards(IReadOnlyList<ChatToolCall> calls)
        {
            var activeKeys = new HashSet<string>(StringComparer.Ordinal);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var visualIndex = 0;
            for (var index = 0; index < calls.Count; index++)
            {
                var call = calls[index];
                if (call == null) continue;
                // Interactive tools have a canonical, actionable card in the
                // dedicated interaction area. Keeping their raw JSON inside
                // the assistant bubble duplicates the prompt and can make a
                // single virtualized row taller than the entire viewport.
                if (!call.Completed && ChatSession.IsInteractiveToolName(call.Name)) continue;
                var key = OccurrenceKey("tool", call.Id, index, occurrences, out var occurrence);
                activeKeys.Add(key);
                if (!_toolCards.TryGetValue(key, out var card))
                {
                    card = new ToolCardElement();
                    card.name = ElementName("tool-card-", call.Id, index, occurrence);
                    _toolCards.Add(key, card);
                }
                card.UpdateFrom(call);
                EnsureChildOrder(_tools, card, visualIndex++);
            }

            RemoveMissing(_toolCards, activeKeys, _tools);
            SetDisplayed(_tools, _toolCards.Count > 0);
        }

        private void ReconcileReferenceChips(IReadOnlyList<ChatReference> references)
        {
            var activeKeys = new HashSet<string>(StringComparer.Ordinal);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var visualIndex = 0;
            for (var index = 0; index < references.Count; index++)
            {
                var reference = references[index];
                if (reference == null) continue;
                var key = OccurrenceKey("reference", reference.CanonicalKey, index, occurrences, out var occurrence);
                activeKeys.Add(key);
                if (!_referenceChips.TryGetValue(key, out var chip))
                {
                    chip = new ReferenceChipElement();
                    chip.name = ElementName("reference-chip-", reference.CanonicalKey, index, occurrence);
                    _referenceChips.Add(key, chip);
                }
                chip.UpdateFrom(reference);
                EnsureChildOrder(_references, chip, visualIndex++);
            }

            RemoveMissing(_referenceChips, activeKeys, _references);
            SetDisplayed(_references, _referenceChips.Count > 0);
        }

        private static void EnsureChildOrder(VisualElement parent, VisualElement child, int index)
        {
            var currentIndex = parent.IndexOf(child);
            if (currentIndex == index) return;
            if (currentIndex >= 0) parent.RemoveAt(currentIndex);
            parent.Insert(Math.Min(index, parent.childCount), child);
        }

        private static void RemoveMissing<T>(Dictionary<string, T> elements, HashSet<string> activeKeys, VisualElement parent)
            where T : VisualElement
        {
            var removedKeys = new List<string>();
            foreach (var pair in elements)
            {
                if (activeKeys.Contains(pair.Key)) continue;
                pair.Value.RemoveFromHierarchy();
                removedKeys.Add(pair.Key);
            }
            foreach (var key in removedKeys) elements.Remove(key);
        }

        private static string OccurrenceKey(
            string keyNamespace,
            string stableValue,
            int fallbackIndex,
            Dictionary<string, int> occurrences,
            out int occurrence)
        {
            var baseKey = string.IsNullOrEmpty(stableValue)
                ? $"{keyNamespace}:fallback:{fallbackIndex}"
                : $"{keyNamespace}:stable:{stableValue}";
            occurrences.TryGetValue(baseKey, out occurrence);
            occurrences[baseKey] = occurrence + 1;
            return $"{baseKey}#{occurrence}";
        }

        private static string ElementName(string prefix, string stableValue, int fallbackIndex, int occurrence)
        {
            var value = string.IsNullOrEmpty(stableValue) ? $"fallback-index-{fallbackIndex}" : stableValue;
            return occurrence == 0 ? prefix + value : $"{prefix}{value}-duplicate-{occurrence}";
        }

        private static void SetTextIfChanged(Label label, string value)
        {
            value ??= "";
            if (!string.Equals(label.text, value, StringComparison.Ordinal)) label.text = value;
        }

        private static void PingReference(ChatReference reference)
        {
            if (reference?.Target == null) return;
            var resolved = ToolTargetResolver.Resolve(reference.Target, out var error);
            reference.IsAvailable = resolved != null;
            reference.ValidationError = error;
            if (resolved == null) return;
            Selection.activeObject = resolved;
            EditorGUIUtility.PingObject(resolved);
        }

        private void ClearRoleClasses()
        {
            RemoveFromClassList("role-user");
            RemoveFromClassList("role-assistant");
            RemoveFromClassList("role-system");
            RemoveFromClassList("role-error");
            RemoveFromClassList("role-tool");
        }

        private static string RoleClass(ChatRole role)
        {
            switch (role)
            {
                case ChatRole.User: return "role-user";
                case ChatRole.Assistant: return "role-assistant";
                case ChatRole.System: return "role-system";
                case ChatRole.Error: return "role-error";
                case ChatRole.Tool: return "role-tool";
                default: return "role-system";
            }
        }

        private static string RoleLabel(ChatRole role)
        {
            switch (role)
            {
                case ChatRole.User: return "Você";
                case ChatRole.Assistant: return "Agente";
                case ChatRole.System: return "Sistema";
                case ChatRole.Error: return "Erro";
                case ChatRole.Tool: return "Ferramenta";
                default: return role.ToString();
            }
        }

        private static void SetDisplayed(VisualElement element, bool displayed)
        {
            element.style.display = displayed ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? "";
            return value.Substring(0, max) + "\n… conteúdo truncado";
        }

        private sealed class ToolCardElement : Foldout
        {
            private readonly Label _meta;
            private readonly Label _inputCaption;
            private readonly Label _input;
            private readonly Label _resultCaption;
            private readonly Label _result;
            private ChatToolCall _call;

            public ToolCardElement()
            {
                AddToClassList("tool-card");
                RegisterCallback<ChangeEvent<bool>>(evt =>
                {
                    if (_call == null) return;
                    _call.Expanded = evt.newValue;
                    RenderDetails(evt.newValue);
                }, TrickleDown.TrickleDown);

                _meta = CreateLabel("tool-meta");
                Add(_meta);
                _inputCaption = CreateLabel("tool-meta", "Entrada");
                Add(_inputCaption);
                _input = CreateLabel("tool-code");
                Add(_input);
                _resultCaption = CreateLabel("tool-meta", "Resultado");
                Add(_resultCaption);
                _result = CreateLabel("tool-code");
                Add(_result);
            }

            public void UpdateFrom(ChatToolCall call)
            {
                _call = call;
                text = ChatMessagePresentation.ToolName(call.Name);
                SetValueWithoutNotify(call.Expanded);
                EnableInClassList("failed", call.Failed);

                var status = call.Completed ? call.Failed ? "falhou" : "concluída" : "executando";
                var duration = call.DurationMs > 0 ? $" • {call.DurationMs:F0} ms" : "";
                SetTextIfChanged(_meta, status + duration);

                RenderDetails(call.Expanded);
            }

            private void RenderDetails(bool expanded)
            {
                var hasInput = expanded && !string.IsNullOrEmpty(_call?.InputJson);
                SetTextIfChanged(_input, hasInput ? Truncate(_call.InputJson, 4000) : "");
                SetDisplayed(_inputCaption, hasInput);
                SetDisplayed(_input, hasInput);

                var hasResult = expanded && !string.IsNullOrEmpty(_call?.ResultText);
                SetTextIfChanged(_result, hasResult ? Truncate(_call.ResultText, 8000) : "");
                SetDisplayed(_resultCaption, hasResult);
                SetDisplayed(_result, hasResult);
            }
        }

        private sealed class ReferenceChipElement : Button
        {
            private ChatReference _reference;

            public ReferenceChipElement()
            {
                AddToClassList("message-reference-chip");
                clicked += () => PingReference(_reference);
            }

            public void UpdateFrom(ChatReference reference)
            {
                _reference = reference;
                text = string.IsNullOrEmpty(reference.Label) ? reference.Kind : reference.Label;
                tooltip = reference.IsAvailable
                    ? $"Localizar {reference.Kind} no Unity"
                    : reference.ValidationError ?? "Referência indisponível";
                EnableInClassList("unavailable", !reference.IsAvailable);
            }
        }

        private static Label CreateLabel(string className, string text = "")
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }
    }
}
