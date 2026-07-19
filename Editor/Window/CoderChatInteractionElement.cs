using Maicon.SceneAnalyzer.Editor.Chat;
using UnityEngine.UIElements;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    internal sealed class CoderChatInteractionElement : VisualElement
    {
        private readonly PendingInteraction _interaction;
        private readonly Label _inlineError;

        public CoderChatInteractionElement(PendingInteraction interaction)
        {
            _interaction = interaction;
            AddToClassList("interaction-card");

            var header = new VisualElement();
            header.AddToClassList("interaction-header");
            Add(header);

            var title = new Label(TitleFor(interaction));
            title.AddToClassList("interaction-title");
            header.Add(title);

            var state = new Label(StateFor(interaction));
            state.AddToClassList("badge");
            header.Add(state);

            switch (interaction)
            {
                case PendingQuestion question:
                    BuildQuestion(question);
                    break;
                case PendingPlan plan:
                    BuildPlan(plan);
                    break;
                case PendingTasks tasks:
                    BuildTasks(tasks);
                    break;
            }

            _inlineError = new Label(interaction.Error ?? "");
            _inlineError.AddToClassList("interaction-error");
            SetDisplayed(_inlineError, !string.IsNullOrEmpty(interaction.Error));
            Add(_inlineError);

            if (interaction.State == PendingInteractionState.Submitting)
                SetEnabled(false);
        }

        private void BuildQuestion(PendingQuestion question)
        {
            var copy = new Label(question.Question ?? "O agente precisa de uma resposta.");
            copy.AddToClassList("interaction-copy");
            Add(copy);

            if (question.Choices.Count > 0)
            {
                var choices = new VisualElement();
                choices.AddToClassList("interaction-actions");
                foreach (var option in question.Choices)
                {
                    var captured = option;
                    var button = new Button(() => SubmitQuestion(question, captured.Value))
                    {
                        text = captured.Label,
                        tooltip = captured.Value,
                    };
                    choices.Add(button);
                }
                Add(choices);
            }

            if (!question.AllowFreeform) return;
            var input = CreateDraftField(question, "Resposta livre");
            Add(input);
            var actions = new VisualElement();
            actions.AddToClassList("interaction-actions");
            actions.Add(new Button(() => SubmitQuestion(question, question.ResponseDraft)) { text = "Responder" });
            Add(actions);
        }

        private void BuildPlan(PendingPlan plan)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("interaction-scroll");
            var copy = new Label(plan.Plan ?? "Plano sem conteúdo.");
            copy.AddToClassList("interaction-copy");
            scroll.Add(copy);
            Add(scroll);

            var note = CreateDraftField(plan, "Comentário opcional ou alterações solicitadas");
            Add(note);

            var actions = new VisualElement();
            actions.AddToClassList("interaction-actions");
            actions.Add(new Button(() => SubmitPlan(plan, true, plan.ResponseDraft)) { text = "Aprovar" });
            actions.Add(new Button(() =>
            {
                if (string.IsNullOrWhiteSpace(plan.ResponseDraft))
                {
                    ShowInlineError("Descreva as alterações solicitadas antes de enviar.");
                    return;
                }
                SubmitPlan(plan, false, plan.ResponseDraft);
            }) { text = "Solicitar alterações" });
            actions.Add(new Button(() => SubmitPlan(plan, false, plan.ResponseDraft)) { text = "Rejeitar" });
            Add(actions);
        }

        private void BuildTasks(PendingTasks tasks)
        {
            var copy = new Label(tasks.Summary ?? "O agente publicou uma lista de tarefas.");
            copy.AddToClassList("interaction-copy");
            Add(copy);

            var actions = new VisualElement();
            actions.AddToClassList("interaction-actions");
            actions.Add(new Button(() => SubmitTasks(tasks, true)) { text = "Aprovar tarefas" });
            actions.Add(new Button(() => SubmitTasks(tasks, false)) { text = "Rejeitar" });
            Add(actions);
        }

        private static TextField CreateDraftField(PendingInteraction interaction, string label)
        {
            var input = new TextField(label)
            {
                multiline = true,
                value = interaction.ResponseDraft ?? "",
            };
            input.AddToClassList("interaction-input");
            input.RegisterValueChangedCallback(evt => interaction.ResponseDraft = evt.newValue);
            return input;
        }

        private void SubmitQuestion(PendingQuestion question, string answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
            {
                ShowInlineError("Informe uma resposta antes de enviar.");
                return;
            }
            Bootstrap.ChatClient?.AnswerQuestion(
                Settings.CoderUrl, Settings.CoderToken, Settings.CoderSessionId, question.Id, answer);
        }

        private static void SubmitPlan(PendingPlan plan, bool approved, string note)
        {
            Bootstrap.ChatClient?.RespondToPlan(
                Settings.CoderUrl, Settings.CoderToken, Settings.CoderSessionId, plan.Id, approved, note);
        }

        private static void SubmitTasks(PendingTasks tasks, bool approved)
        {
            Bootstrap.ChatClient?.RespondToTasks(
                Settings.CoderUrl, Settings.CoderToken, Settings.CoderSessionId, tasks.ToolUseId, approved);
        }

        private void ShowInlineError(string message)
        {
            if (_inlineError == null) return;
            _inlineError.text = message;
            SetDisplayed(_inlineError, true);
        }

        private static string TitleFor(PendingInteraction interaction)
        {
            switch (interaction?.Kind)
            {
                case PendingInteractionKind.Question: return "Pergunta do agente";
                case PendingInteractionKind.Plan: return "Plano aguardando aprovação";
                case PendingInteractionKind.Tasks: return "Tarefas aguardando aprovação";
                default: return "Ação necessária";
            }
        }

        private static string StateFor(PendingInteraction interaction)
        {
            switch (interaction?.State)
            {
                case PendingInteractionState.Submitting: return "ENVIANDO";
                case PendingInteractionState.Failed: return "FALHOU";
                default: return "PENDENTE";
            }
        }

        private static void SetDisplayed(VisualElement element, bool displayed)
        {
            element.style.display = displayed ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
