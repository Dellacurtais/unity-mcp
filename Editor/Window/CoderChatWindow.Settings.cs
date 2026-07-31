using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Chat;
using UnityEngine.UIElements;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    public sealed partial class CoderChatWindow
    {
        private Foldout _settingsFoldout;
        private TextField _urlField;
        private TextField _tokenField;
        private IntegerField _projectField;
        private TextField _sessionIdField;
        private DropdownField _providerField;
        private DropdownField _modelField;
        private Toggle _autoContextToggle;
        private Toggle _showSystemToggle;
        private TextField _preambleField;
        private Label _projectStatusLabel;
        private Label _activeModelStatusLabel;
        private DropdownField _executionModeField;
        private Toggle _thinkingToggle;
        private DropdownField _planDepthSettingsField;
        private DropdownField _outputCapSettingsField;
        private DropdownField _permissionModeSettingsField;
        private DropdownField _responseLanguageField;
        private DropdownField _outputStyleField;
        private IntegerField _maxTurnsField;
        private IntegerField _maxAutoContinuesField;
        private readonly Dictionary<string, CoderProviderInfo> _providerByLabel = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CoderModelInfo> _modelByLabel = new(StringComparer.Ordinal);

        private void BuildSettingsPanel(VisualElement host)
        {
            if (host == null) return;
            host.Clear();

            _settingsFoldout = new Foldout
            {
                text = "Configurações avançadas",
                value = false,
            };
            _settingsFoldout.AddToClassList("settings-foldout");
            host.Add(_settingsFoldout);

            var grid = new VisualElement();
            grid.AddToClassList("settings-grid");
            _settingsFoldout.Add(grid);

            _urlField = new TextField("URL do runtime") { value = Settings.CoderUrl };
            var urlRow = CreateSettingsRow(_urlField);
            urlRow.Add(new Button(SaveConnectionSettings) { text = "Salvar" });
            urlRow.Add(new Button(() =>
                Bootstrap.ChatClient?.TestConnection(CurrentUrl(), CurrentToken())) { text = "Testar" });
            grid.Add(urlRow);

            _tokenField = new TextField("Token")
            {
                value = Settings.CoderToken,
                isPasswordField = true,
            };
            _tokenField.RegisterValueChangedCallback(evt => Settings.CoderToken = evt.newValue ?? "");
            grid.Add(CreateSettingsRow(_tokenField));

            _projectField = new IntegerField("Projeto") { value = Settings.CoderProjectId };
            _projectField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderProjectId = Math.Max(0, evt.newValue);
                _catalogDirty = true;
            });
            var projectRow = CreateSettingsRow(_projectField);
            projectRow.Add(new Button(() =>
            {
                SaveConnectionSettings();
                Bootstrap.ChatClient?.EnsureProject(CurrentUrl(), CurrentToken(), Bootstrap.UnityProjectRoot);
            }) { text = "Auto-detectar" });
            projectRow.Add(new Button(ClearProjectBinding) { text = "Limpar" });
            grid.Add(projectRow);

            _projectStatusLabel = new Label();
            _projectStatusLabel.AddToClassList("settings-note");
            grid.Add(_projectStatusLabel);

            _sessionIdField = new TextField("Sessão UUID")
            {
                value = Settings.CoderSessionId,
                tooltip = "UUID canônico da sessão no CodeStudio",
            };
            var sessionRow = CreateSettingsRow(_sessionIdField);
            sessionRow.Add(new Button(() => AttachSession(_sessionIdField.value, true))
            {
                text = "Anexar",
            });
            grid.Add(sessionRow);

            _providerField = new DropdownField("Provider");
            _providerField.RegisterValueChangedCallback(evt =>
            {
                if (!_providerByLabel.TryGetValue(evt.newValue, out var provider)) return;
                Settings.CoderProvider = provider.Id;
                RefreshModelChoices(provider);
                ApplySelectedModelToActiveSession();
                UpdateModelButton(true);
                RefreshEffortAvailability();
            });
            grid.Add(CreateSettingsRow(_providerField));

            _modelField = new DropdownField("Modelo");
            _modelField.RegisterValueChangedCallback(evt =>
            {
                if (!_modelByLabel.TryGetValue(evt.newValue, out var model)) return;
                Settings.CoderModel = model.Id;
                ApplySelectedModelToActiveSession();
                UpdateModelButton(true);
                RefreshEffortAvailability();
            });
            var modelRow = CreateSettingsRow(_modelField);
            modelRow.Add(new Button(ApplySelectedModelToActiveSession)
            {
                text = "Aplicar à sessão",
                tooltip = "Trocar imediatamente o provider/modelo da sessão anexada",
            });
            modelRow.Add(new Button(() =>
            {
                SaveConnectionSettings();
                Bootstrap.ChatClient?.RefreshProviders(CurrentUrl(), CurrentToken());
            }) { text = "Atualizar" });
            grid.Add(modelRow);

            _activeModelStatusLabel = new Label();
            _activeModelStatusLabel.AddToClassList("settings-note");
            grid.Add(_activeModelStatusLabel);

            var composerSection = new Label("COMPOSER / SESSÃO");
            composerSection.AddToClassList("settings-section-title");
            grid.Add(composerSection);

            _executionModeField = new DropdownField(
                "Modo de execução", new List<string> { "Padrão", "Ultra" }, 0);
            _executionModeField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderExecutionMode = evt.newValue == "Ultra" ? "ultra" : "standard";
                ApplyToActiveSession(client => client.SetExecutionMode(
                    CurrentUrl(), CurrentToken(), Settings.CoderSessionId, Settings.CoderExecutionMode));
            });
            grid.Add(CreateSettingsRow(_executionModeField));

            _thinkingToggle = new Toggle("Thinking estendido")
            {
                value = Settings.CoderThinking,
                tooltip = "Ativa o thinking nativo do provider, separado do nível de esforço",
            };
            _thinkingToggle.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderThinking = evt.newValue;
                ApplyToActiveSession(client => client.SetThinking(
                    CurrentUrl(), CurrentToken(), Settings.CoderSessionId, evt.newValue));
            });
            grid.Add(_thinkingToggle);

            _planDepthSettingsField = new DropdownField(
                "Profundidade do plano", new List<string> { "Simples", "Normal", "Complexo" }, 1);
            _planDepthSettingsField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderPlanDepth = PlanDepthValue(evt.newValue);
                ApplyToActiveSession(client => client.SetPlanDepth(
                    CurrentUrl(), CurrentToken(), Settings.CoderSessionId, Settings.CoderPlanDepth));
                RefreshPlanDepthControl();
            });
            grid.Add(CreateSettingsRow(_planDepthSettingsField));

            _outputCapSettingsField = new DropdownField(
                "Saída das tools",
                new List<string> { "Econômica", "Média", "Alta", "Extra", "Máxima", "XL" },
                0);
            _outputCapSettingsField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderToolOutputCapLevel = OutputCapValue(evt.newValue);
                ApplyToActiveSession(client => client.SetToolOutputCapLevel(
                    CurrentUrl(), CurrentToken(), Settings.CoderSessionId, Settings.CoderToolOutputCapLevel));
            });
            grid.Add(CreateSettingsRow(_outputCapSettingsField));

            _permissionModeSettingsField = new DropdownField(
                "Permissões das tools",
                new List<string> { "Perguntar", "Auto", "Sem impedimento" },
                1);
            _permissionModeSettingsField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderToolPermissionMode = PermissionModeValue(evt.newValue);
                ApplyToActiveSession(client => client.SetToolPermissionMode(
                    CurrentUrl(), CurrentToken(), Settings.CoderSessionId, Settings.CoderToolPermissionMode));
            });
            grid.Add(CreateSettingsRow(_permissionModeSettingsField));

            _responseLanguageField = new DropdownField(
                "Idioma da resposta",
                new List<string> { "Auto", "Português (Brasil)", "English", "Español" },
                0);
            _responseLanguageField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderResponseLanguage = evt.newValue == "Auto" ? "" : evt.newValue;
                ApplyToActiveSession(client => client.SetResponseLanguage(
                    CurrentUrl(), CurrentToken(), Settings.CoderSessionId, Settings.CoderResponseLanguage));
            });
            grid.Add(CreateSettingsRow(_responseLanguageField));

            _outputStyleField = new DropdownField(
                "Estilo da resposta", new List<string> { "Proativo", "Explicativo", "Aprendizado" }, 0);
            _outputStyleField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderOutputStyle = evt.newValue == "Explicativo"
                    ? "explanatory"
                    : evt.newValue == "Aprendizado" ? "learning" : "";
                ApplyToActiveSession(client => client.SetOutputStyle(
                    CurrentUrl(), CurrentToken(), Settings.CoderSessionId, Settings.CoderOutputStyle));
            });
            grid.Add(CreateSettingsRow(_outputStyleField));

            _maxTurnsField = new IntegerField("Máximo de turnos (0 = ilimitado)")
            {
                value = Settings.CoderMaxTurns,
            };
            _maxTurnsField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderMaxTurns = Math.Max(0, evt.newValue);
                ApplyRunLimitsToActiveSession();
            });
            grid.Add(CreateSettingsRow(_maxTurnsField));

            _maxAutoContinuesField = new IntegerField("Continuações automáticas")
            {
                value = Settings.CoderMaxAutoContinues,
            };
            _maxAutoContinuesField.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderMaxAutoContinues = evt.newValue;
                ApplyRunLimitsToActiveSession();
            });
            grid.Add(CreateSettingsRow(_maxAutoContinuesField));

            _autoContextToggle = new Toggle("Injetar contexto Unity na primeira mensagem")
            {
                value = Settings.CoderAutoInjectContext,
            };
            _autoContextToggle.RegisterValueChangedCallback(evt => Settings.CoderAutoInjectContext = evt.newValue);
            grid.Add(_autoContextToggle);

            _showSystemToggle = new Toggle("Mostrar mensagens do sistema")
            {
                value = Settings.CoderShowSystemMessages,
            };
            _showSystemToggle.RegisterValueChangedCallback(evt =>
            {
                Settings.CoderShowSystemMessages = evt.newValue;
                _showSystemMessages = evt.newValue;
                _sessionDirty = true;
            });
            grid.Add(_showSystemToggle);

            _preambleField = new TextField("Preamble personalizado")
            {
                multiline = true,
                value = Settings.CoderCustomPreamble,
            };
            _preambleField.AddToClassList("settings-preamble");
            _preambleField.RegisterValueChangedCallback(evt => Settings.CoderCustomPreamble = evt.newValue ?? "");
            grid.Add(_preambleField);

            var note = new Label(
                "As alterações acima são salvas como padrão e também aplicadas à sessão anexada. "
                + "Trocar provider/modelo usa o mesmo /switch do composer do CodeStudio.");
            note.AddToClassList("settings-note");
            grid.Add(note);

            RefreshSettingsControls();
        }

        private void RefreshSettingsControls()
        {
            if (_settingsFoldout == null) return;

            _urlField?.SetValueWithoutNotify(Settings.CoderUrl);
            _tokenField?.SetValueWithoutNotify(Settings.CoderToken);
            _projectField?.SetValueWithoutNotify(Settings.CoderProjectId);
            _sessionIdField?.SetValueWithoutNotify(Settings.CoderSessionId);
            _autoContextToggle?.SetValueWithoutNotify(Settings.CoderAutoInjectContext);
            _showSystemToggle?.SetValueWithoutNotify(Settings.CoderShowSystemMessages);
            _preambleField?.SetValueWithoutNotify(Settings.CoderCustomPreamble);

            RefreshProviderChoices();
            RefreshProjectStatus();
            RefreshSessionSettingsControls();
        }

        private void RefreshProviderChoices()
        {
            if (_providerField == null) return;
            _providerByLabel.Clear();
            var labels = new List<string>();
            foreach (var provider in Bootstrap.CoderProviders ?? new List<CoderProviderInfo>())
            {
                if (provider == null || string.IsNullOrEmpty(provider.Id)) continue;
                var label = string.IsNullOrEmpty(provider.Name) ? provider.Id : provider.Name;
                if (_providerByLabel.ContainsKey(label)) label = $"{label} ({provider.Id})";
                _providerByLabel[label] = provider;
                labels.Add(label);
            }

            if (labels.Count == 0)
            {
                var fallback = new CoderProviderInfo { Id = Settings.CoderProvider, Name = Settings.CoderProvider };
                var fallbackLabel = string.IsNullOrEmpty(fallback.Id) ? "Provider padrão" : fallback.Id;
                _providerByLabel[fallbackLabel] = fallback;
                labels.Add(fallbackLabel);
            }

            _providerField.choices = labels;
            var selected = _providerByLabel.FirstOrDefault(pair => pair.Value.Id == Settings.CoderProvider);
            if (string.IsNullOrEmpty(selected.Key)) selected = new KeyValuePair<string, CoderProviderInfo>(labels[0], _providerByLabel[labels[0]]);
            _providerField.SetValueWithoutNotify(selected.Key);
            RefreshModelChoices(selected.Value);
        }

        private void RefreshModelChoices(CoderProviderInfo provider)
        {
            if (_modelField == null) return;
            _modelByLabel.Clear();
            var labels = new List<string>();
            foreach (var model in provider?.Models ?? new List<CoderModelInfo>())
            {
                if (model == null || string.IsNullOrEmpty(model.Id)) continue;
                var label = string.IsNullOrEmpty(model.Name) ? model.Id : model.Name;
                if (_modelByLabel.ContainsKey(label)) label = $"{label} ({model.Id})";
                _modelByLabel[label] = model;
                labels.Add(label);
            }

            if (labels.Count == 0)
            {
                var fallback = new CoderModelInfo { Id = Settings.CoderModel, Name = Settings.CoderModel, CanReason = true };
                var fallbackLabel = string.IsNullOrEmpty(fallback.Id) ? "Modelo padrão" : fallback.Id;
                _modelByLabel[fallbackLabel] = fallback;
                labels.Add(fallbackLabel);
            }

            _modelField.choices = labels;
            var selected = _modelByLabel.FirstOrDefault(pair => pair.Value.Id == Settings.CoderModel);
            if (string.IsNullOrEmpty(selected.Key)) selected = new KeyValuePair<string, CoderModelInfo>(labels[0], _modelByLabel[labels[0]]);
            _modelField.SetValueWithoutNotify(selected.Key);
            if (!string.Equals(Settings.CoderModel, selected.Value.Id, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(selected.Value.Id))
                Settings.CoderModel = selected.Value.Id;
        }

        private void RefreshSessionSettingsControls()
        {
            var remote = Bootstrap.ChatSession?.RemoteSession;
            var executionMode = remote?.ExecutionModeOverride ?? Settings.CoderExecutionMode;
            var planDepth = remote?.PlanDepthOverride ?? Settings.CoderPlanDepth;
            var cap = remote?.ToolOutputCapLevel ?? Settings.CoderToolOutputCapLevel;
            var permission = remote?.ToolPermissionMode ?? Settings.CoderToolPermissionMode;
            var language = remote?.ResponseLanguageOverride ?? Settings.CoderResponseLanguage;
            var style = remote?.OutputStyleOverride ?? Settings.CoderOutputStyle;

            _executionModeField?.SetValueWithoutNotify(executionMode == "ultra" ? "Ultra" : "Padrão");
            _thinkingToggle?.SetValueWithoutNotify(remote?.ThinkingEnabled ?? Settings.CoderThinking);
            _planDepthSettingsField?.SetValueWithoutNotify(PlanDepthLabel(planDepth));
            _outputCapSettingsField?.SetValueWithoutNotify(OutputCapLabel(cap));
            _permissionModeSettingsField?.SetValueWithoutNotify(PermissionModeLabel(permission));
            _responseLanguageField?.SetValueWithoutNotify(string.IsNullOrEmpty(language) ? "Auto" : language);
            _outputStyleField?.SetValueWithoutNotify(
                style == "explanatory" ? "Explicativo" : style == "learning" ? "Aprendizado" : "Proativo");
            _maxTurnsField?.SetValueWithoutNotify(remote?.MaxTurns ?? Settings.CoderMaxTurns);
            _maxAutoContinuesField?.SetValueWithoutNotify(
                remote == null ? Settings.CoderMaxAutoContinues : remote.MaxAutoContinues);

            if (_activeModelStatusLabel != null)
            {
                var selected = $"{Settings.CoderProvider} · {Settings.CoderModel}";
                _activeModelStatusLabel.text = remote == null
                    ? $"Nova sessão: {selected}"
                    : $"Sessão ativa: {remote.Provider} · {remote.Model}"
                        + (remote.Provider == Settings.CoderProvider && remote.Model == Settings.CoderModel
                            ? ""
                            : $" · selecionado: {selected}");
            }
        }

        private void ApplySelectedModelToActiveSession()
        {
            ApplyToActiveSession(client => client.SwitchProvider(
                CurrentUrl(),
                CurrentToken(),
                Settings.CoderSessionId,
                Settings.CoderProvider,
                Settings.CoderModel));
        }

        private void ApplyRunLimitsToActiveSession()
        {
            ApplyToActiveSession(client => client.SetRunLimits(
                CurrentUrl(),
                CurrentToken(),
                Settings.CoderSessionId,
                Settings.CoderMaxTurns,
                Settings.CoderMaxAutoContinues));
        }

        private static void ApplyToActiveSession(Action<CoderApiClient> action)
        {
            if (!CoderSessionIdentity.IsValid(Settings.CoderSessionId) || Bootstrap.ChatClient == null) return;
            action?.Invoke(Bootstrap.ChatClient);
        }

        private static string PlanDepthValue(string label)
            => label == "Simples" ? "simple" : label == "Complexo" ? "complex" : "normal";

        private static string PlanDepthLabel(string value)
            => value == "simple" ? "Simples" : value == "complex" ? "Complexo" : "Normal";

        private static string OutputCapValue(string label)
        {
            switch (label)
            {
                case "Média": return "medium";
                case "Alta": return "high";
                case "Extra": return "extra";
                case "Máxima": return "max";
                case "XL": return "ultra";
                default: return "economic";
            }
        }

        private static string OutputCapLabel(string value)
        {
            switch (value)
            {
                case "medium": return "Média";
                case "high": return "Alta";
                case "extra": return "Extra";
                case "max": return "Máxima";
                case "ultra": return "XL";
                default: return "Econômica";
            }
        }

        private static string PermissionModeValue(string label)
            => label == "Perguntar" ? "ask" : label == "Sem impedimento" ? "unrestricted" : "auto";

        private static string PermissionModeLabel(string value)
            => value == "ask" ? "Perguntar" : value == "unrestricted" ? "Sem impedimento" : "Auto";

        private void RefreshProjectStatus()
        {
            if (_projectStatusLabel == null) return;
            if (Settings.CoderProjectId <= 0)
            {
                _projectStatusLabel.text = $"Projeto não resolvido · workdir: {Bootstrap.UnityProjectRoot ?? "indisponível"}";
                return;
            }

            var name = string.IsNullOrEmpty(Settings.CoderProjectName) ? "nome não carregado" : Settings.CoderProjectName;
            var workdir = string.IsNullOrEmpty(Settings.CoderProjectWorkdir) ? "workdir não carregado" : Settings.CoderProjectWorkdir;
            var mismatch = !string.IsNullOrEmpty(Settings.CoderProjectWorkdir)
                && !CoderApiClient.WorkdirMatches(Settings.CoderProjectWorkdir, Bootstrap.UnityProjectRoot);
            _projectStatusLabel.text = $"#{Settings.CoderProjectId} · {name} · {workdir}"
                + (mismatch ? "\nAtenção: o projeto salvo aponta para outro workdir; use Auto-detectar." : "");
        }

        private void SaveConnectionSettings()
        {
            Settings.CoderUrl = CurrentUrl();
            Settings.CoderToken = CurrentToken();
            _catalogDirty = true;
            RefreshRemoteCatalogs();
        }

        private void ClearProjectBinding()
        {
            Settings.CoderProjectId = 0;
            Settings.CoderProjectName = "";
            Settings.CoderProjectWorkdir = "";
            _projectField?.SetValueWithoutNotify(0);
            RefreshProjectStatus();
            _catalogDirty = true;
        }

        private string CurrentUrl()
        {
            var value = (_urlField?.value ?? Settings.CoderUrl ?? "").Trim();
            return string.IsNullOrEmpty(value) ? "http://localhost:7333" : value;
        }

        private string CurrentToken() => _tokenField?.value ?? Settings.CoderToken ?? "";

        private static VisualElement CreateSettingsRow(VisualElement field)
        {
            var row = new VisualElement();
            row.AddToClassList("settings-row");
            row.Add(field);
            return row;
        }
    }
}
