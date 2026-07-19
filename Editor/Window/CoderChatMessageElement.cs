using System;
using Maicon.SceneAnalyzer.Editor.Chat;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine.UIElements;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    internal sealed class CoderChatMessageElement : VisualElement
    {
        private readonly VisualElement _bubble;
        private readonly Label _role;
        private readonly Label _time;
        private readonly Label _streaming;
        private readonly Label _body;
        private readonly Foldout _thinking;
        private readonly Label _thinkingText;
        private readonly VisualElement _tools;
        private readonly VisualElement _references;

        public ChatMessage BoundMessage { get; private set; }
        public int BoundRevision { get; private set; } = -1;

        public CoderChatMessageElement()
        {
            AddToClassList("message-row");

            _bubble = new VisualElement();
            _bubble.AddToClassList("message-bubble");
            Add(_bubble);

            var header = new VisualElement();
            header.AddToClassList("message-header");
            _bubble.Add(header);

            _role = new Label();
            _role.AddToClassList("message-role");
            header.Add(_role);

            _streaming = new Label("recebendo…");
            _streaming.AddToClassList("message-streaming");
            header.Add(_streaming);

            _time = new Label();
            _time.AddToClassList("message-time");
            header.Add(_time);

            _body = new Label();
            _body.AddToClassList("message-body");
            _bubble.Add(_body);

            _thinking = new Foldout { text = "Raciocínio", value = false };
            _thinking.AddToClassList("message-thinking");
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
        {
            BoundMessage = message;
            BoundRevision = message?.Revision ?? -1;
            ClearRoleClasses();
            _tools.Clear();
            _references.Clear();

            if (message == null)
            {
                _role.text = "";
                _time.text = "";
                _body.text = "";
                SetDisplayed(_streaming, false);
                SetDisplayed(_thinking, false);
                SetDisplayed(_tools, false);
                SetDisplayed(_references, false);
                return;
            }

            AddToClassList(RoleClass(message.Role));
            _role.text = RoleLabel(message.Role);
            _time.text = message.Timestamp.ToString("HH:mm:ss");
            _body.text = message.Text ?? "";
            SetDisplayed(_body, !string.IsNullOrEmpty(message.Text));
            SetDisplayed(_streaming, message.Streaming);

            var hasThinking = !string.IsNullOrEmpty(message.ThinkingText);
            _thinkingText.text = message.ThinkingText ?? "";
            SetDisplayed(_thinking, hasThinking);

            foreach (var call in message.ToolCalls)
                _tools.Add(CreateToolCard(call));
            SetDisplayed(_tools, message.ToolCalls.Count > 0);

            foreach (var reference in message.References)
                if (reference != null) _references.Add(CreateReferenceChip(reference));
            SetDisplayed(_references, message.References.Count > 0);
        }

        private static VisualElement CreateToolCard(ChatToolCall call)
        {
            var card = new Foldout
            {
                text = string.IsNullOrEmpty(call?.Name) ? "Ferramenta" : call.Name,
                value = false,
            };
            card.AddToClassList("tool-card");
            if (call?.Failed == true) card.AddToClassList("failed");

            var status = call == null
                ? "sem dados"
                : call.Completed
                    ? call.Failed ? "falhou" : "concluída"
                    : "executando";
            var duration = call != null && call.DurationMs > 0 ? $" • {call.DurationMs:F0} ms" : "";
            var meta = new Label(status + duration);
            meta.AddToClassList("tool-meta");
            card.Add(meta);

            if (!string.IsNullOrEmpty(call?.InputJson))
            {
                var inputCaption = new Label("Entrada");
                inputCaption.AddToClassList("tool-meta");
                card.Add(inputCaption);
                var input = new Label(Truncate(call.InputJson, 4000));
                input.AddToClassList("tool-code");
                card.Add(input);
            }

            if (!string.IsNullOrEmpty(call?.ResultText))
            {
                var resultCaption = new Label("Resultado");
                resultCaption.AddToClassList("tool-meta");
                card.Add(resultCaption);
                var result = new Label(Truncate(call.ResultText, 8000));
                result.AddToClassList("tool-code");
                card.Add(result);
            }

            return card;
        }

        private static VisualElement CreateReferenceChip(ChatReference reference)
        {
            var button = new Button(() => PingReference(reference))
            {
                text = string.IsNullOrEmpty(reference.Label) ? reference.Kind : reference.Label,
                tooltip = reference.IsAvailable
                    ? $"Localizar {reference.Kind} no Unity"
                    : reference.ValidationError ?? "Referência indisponível",
            };
            button.AddToClassList("message-reference-chip");
            if (!reference.IsAvailable) button.AddToClassList("unavailable");
            return button;
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
    }
}
