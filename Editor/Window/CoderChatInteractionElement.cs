using System.Linq;
using Maicon.SceneAnalyzer.Editor.Chat;
using Newtonsoft.Json.Linq;
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
                case PendingSpec spec:
                    BuildSpec(spec);
                    break;
                case PendingDecision decision:
                    BuildDecision(decision);
                    break;
                case PendingTasks tasks:
                    BuildTasks(tasks);
                    break;
                case PendingToolApproval approval:
                    BuildToolApproval(approval);
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
                if (question.MultiSelect)
                {
                    choices.AddToClassList("interaction-choice-list");
                    foreach (var option in question.Choices)
                    {
                        var captured = option;
                        var toggle = new Toggle(captured.Label)
                        {
                            value = question.SelectedValues.Contains(captured.Value),
                            tooltip = captured.Value,
                        };
                        toggle.RegisterValueChangedCallback(evt =>
                        {
                            if (evt.newValue) question.SelectedValues.Add(captured.Value);
                            else question.SelectedValues.Remove(captured.Value);
                        });
                        choices.Add(toggle);
                    }
                }
                else
                {
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
                }
                Add(choices);
            }

            if (question.AllowFreeform)
                Add(CreateDraftField(question, "Resposta livre"));
            if (!question.AllowFreeform && !question.MultiSelect) return;
            var actions = new VisualElement();
            actions.AddToClassList("interaction-actions");
            actions.Add(new Button(() =>
            {
                var selected = string.Join(", ", question.SelectedValues);
                var answer = string.IsNullOrWhiteSpace(question.ResponseDraft)
                    ? selected
                    : string.IsNullOrWhiteSpace(selected)
                        ? question.ResponseDraft
                        : selected + " — " + question.ResponseDraft;
                SubmitQuestion(question, answer);
            }) { text = question.MultiSelect ? "Confirmar seleção" : "Responder" });
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
            actions.Add(new Button(() => SubmitPlan(plan, true, plan.ResponseDraft, false)) { text = "Aprovar" });
            actions.Add(new Button(() =>
            {
                if (string.IsNullOrWhiteSpace(plan.ResponseDraft))
                {
                    ShowInlineError("Descreva as alterações solicitadas antes de enviar.");
                    return;
                }
                SubmitPlan(plan, false, plan.ResponseDraft, false);
            }) { text = "Solicitar alterações" });
            actions.Add(new Button(() => SubmitPlan(plan, false, plan.ResponseDraft, true)) { text = "Rejeitar" });
            Add(actions);
        }

        private void BuildSpec(PendingSpec spec)
        {
            var summary = new Label(string.IsNullOrWhiteSpace(spec.Name)
                ? "Especificação pronta para revisão"
                : spec.Name);
            summary.AddToClassList("interaction-copy");
            Add(summary);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("interaction-scroll");
            var copy = new Label(spec.Body ?? "Especificação sem conteúdo.");
            copy.AddToClassList("interaction-copy");
            scroll.Add(copy);
            Add(scroll);

            Add(CreateDraftField(spec, "Alterações solicitadas"));
            var actions = new VisualElement();
            actions.AddToClassList("interaction-actions");
            actions.Add(new Button(() => SubmitSpec(spec, true, "")) { text = "Aprovar" });
            actions.Add(new Button(() =>
            {
                if (string.IsNullOrWhiteSpace(spec.ResponseDraft))
                {
                    ShowInlineError("Descreva as alterações solicitadas antes de enviar.");
                    return;
                }
                SubmitSpec(spec, false, spec.ResponseDraft);
            }) { text = "Solicitar alterações" });
            Add(actions);
        }

        private void BuildDecision(PendingDecision decision)
        {
            if (!string.IsNullOrWhiteSpace(decision.Header))
            {
                var header = new Label(decision.Header);
                header.AddToClassList("interaction-copy");
                Add(header);
            }

            foreach (var question in decision.Questions)
            {
                var group = new VisualElement();
                group.AddToClassList("interaction-decision-group");
                var prompt = new Label(string.IsNullOrWhiteSpace(question.Header)
                    ? question.Question
                    : question.Header + "\n" + question.Question);
                prompt.AddToClassList("interaction-copy");
                group.Add(prompt);

                var choices = new VisualElement();
                choices.AddToClassList("interaction-choice-list");
                foreach (var option in question.Choices)
                {
                    var captured = option;
                    var toggle = new Toggle(captured.Label)
                    {
                        value = question.SelectedValues.Contains(captured.Value),
                        tooltip = captured.Value,
                    };
                    toggle.RegisterValueChangedCallback(evt =>
                    {
                        if (evt.newValue)
                        {
                            if (!question.MultiSelect) question.SelectedValues.Clear();
                            question.SelectedValues.Add(captured.Value);
                            if (!question.MultiSelect)
                            {
                                foreach (var sibling in choices.Children().OfType<Toggle>())
                                    if (!ReferenceEquals(sibling, toggle)) sibling.SetValueWithoutNotify(false);
                            }
                        }
                        else question.SelectedValues.Remove(captured.Value);
                    });
                    choices.Add(toggle);
                }
                group.Add(choices);

                if (decision.AllowFreeform)
                {
                    var input = new TextField
                    {
                        multiline = true,
                        value = question.ResponseDraft ?? "",
                    };
                    input.AddToClassList("interaction-input");
                    input.RegisterValueChangedCallback(evt => question.ResponseDraft = evt.newValue);
                    group.Add(input);
                }
                Add(group);
            }

            var actions = new VisualElement();
            actions.AddToClassList("interaction-actions");
            actions.Add(new Button(() => SubmitDecision(decision)) { text = "Confirmar decisões" });
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

        private void BuildToolApproval(PendingToolApproval approval)
        {
            var summary = new Label(
                $"{approval.ToolName ?? "ferramenta"} · risco: {RiskLabel(approval.RiskClass)}"
                + (approval.Nested ? " · aninhada" : ""));
            summary.AddToClassList("interaction-copy");
            Add(summary);

            var command = string.IsNullOrWhiteSpace(approval.InputJson)
                ? approval.ArgsSummary
                : approval.InputJson;
            if (!string.IsNullOrWhiteSpace(command))
            {
                var scroll = new ScrollView(ScrollViewMode.Vertical);
                scroll.AddToClassList("interaction-scroll");
                var code = new Label(command);
                code.AddToClassList("tool-code");
                scroll.Add(code);
                Add(scroll);
            }

            if (approval.ApprovalKind == "database_write")
            {
                var warning = new Label(
                    "Esta operação grava no banco. Revise o comando antes de executar.");
                warning.AddToClassList("interaction-warning");
                Add(warning);
            }

            var note = CreateDraftField(approval, "Motivo opcional (enviado ao agente)");
            Add(note);

            var alwaysAllow = new Toggle(
                approval.ApprovalKind == "database_write"
                    ? "Sempre permitir esta gravação nesta sessão"
                    : "Sempre permitir esta ferramenta nesta sessão")
            {
                value = approval.AlwaysAllow,
            };
            alwaysAllow.RegisterValueChangedCallback(evt => approval.AlwaysAllow = evt.newValue);
            alwaysAllow.AddToClassList("interaction-always-allow");
            Add(alwaysAllow);

            var actions = new VisualElement();
            actions.AddToClassList("interaction-actions");
            actions.Add(new Button(() => SubmitToolApproval(
                approval, true, approval.ResponseDraft, approval.AlwaysAllow)) { text = "Aprovar" });
            actions.Add(new Button(() => SubmitToolApproval(
                approval, false, approval.ResponseDraft, false)) { text = "Negar" });
            Add(actions);
        }

        private static VisualElement CreateDraftField(PendingInteraction interaction, string label)
        {
            var field = new VisualElement();
            field.AddToClassList("interaction-field");

            var fieldLabel = new Label(label);
            fieldLabel.AddToClassList("interaction-field-label");
            field.Add(fieldLabel);

            var input = new TextField
            {
                multiline = true,
                value = interaction.ResponseDraft ?? "",
            };
            input.AddToClassList("interaction-input");
            input.RegisterValueChangedCallback(evt => interaction.ResponseDraft = evt.newValue);
            field.Add(input);
            return field;
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

        private static void SubmitPlan(PendingPlan plan, bool approved, string note, bool aborted)
        {
            Bootstrap.ChatClient?.RespondToPlan(
                Settings.CoderUrl, Settings.CoderToken, Settings.CoderSessionId, plan.Id, approved, note, aborted);
        }

        private static void SubmitSpec(PendingSpec spec, bool approved, string requestedChanges)
        {
            Bootstrap.ChatClient?.RespondToSpec(
                Settings.CoderUrl,
                Settings.CoderToken,
                Settings.CoderSessionId,
                spec.Id,
                spec.SpecPath,
                approved,
                requestedChanges);
        }

        private void SubmitDecision(PendingDecision decision)
        {
            var answers = new JObject();
            foreach (var question in decision.Questions)
            {
                if (question.SelectedValues.Count == 0 && string.IsNullOrWhiteSpace(question.ResponseDraft))
                {
                    ShowInlineError($"Responda: {question.Question}");
                    return;
                }
                answers[question.Id] = new JObject
                {
                    ["selected"] = new JArray(question.SelectedValues),
                    ["freeform"] = question.ResponseDraft ?? "",
                };
            }
            Bootstrap.ChatClient?.RespondToDecision(
                Settings.CoderUrl,
                Settings.CoderToken,
                Settings.CoderSessionId,
                decision.Id,
                answers);
        }

        private static void SubmitTasks(PendingTasks tasks, bool approved)
        {
            Bootstrap.ChatClient?.RespondToTasks(
                Settings.CoderUrl, Settings.CoderToken, Settings.CoderSessionId, tasks.ToolUseId, approved);
        }

        private static void SubmitToolApproval(
            PendingToolApproval approval, bool approved, string note, bool alwaysAllow)
        {
            Bootstrap.ChatClient?.RespondToToolApproval(
                Settings.CoderUrl,
                Settings.CoderToken,
                Settings.CoderSessionId,
                approval.ToolUseId,
                approved,
                note,
                alwaysAllow);
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
                case PendingInteractionKind.Spec: return "Especificação aguardando aprovação";
                case PendingInteractionKind.Decision: return "Decisão necessária";
                case PendingInteractionKind.Tasks: return "Tarefas aguardando aprovação";
                case PendingInteractionKind.ToolApproval: return "Aprovação necessária";
                default: return "Ação necessária";
            }
        }

        private static string RiskLabel(string risk)
        {
            switch (risk)
            {
                case "execute": return "execução";
                case "admin": return "administrativo";
                default: return "escrita";
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
