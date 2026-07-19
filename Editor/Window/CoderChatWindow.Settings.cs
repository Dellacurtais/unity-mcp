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
        private IntegerField _sessionIdField;
        private DropdownField _providerField;
        private DropdownField _modelField;
        private Toggle _autoContextToggle;
        private Toggle _showSystemToggle;
        private TextField _preambleField;
        private Label _projectStatusLabel;
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

            _sessionIdField = new IntegerField("Sessão") { value = Settings.CoderSessionId };
            var sessionRow = CreateSettingsRow(_sessionIdField);
            sessionRow.Add(new Button(() => AttachSession(Math.Max(0, _sessionIdField.value), true))
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
                UpdateModelButton();
                RefreshEffortAvailability();
            });
            grid.Add(CreateSettingsRow(_providerField));

            _modelField = new DropdownField("Modelo");
            _modelField.RegisterValueChangedCallback(evt =>
            {
                if (!_modelByLabel.TryGetValue(evt.newValue, out var model)) return;
                Settings.CoderModel = model.Id;
                UpdateModelButton();
                RefreshEffortAvailability();
            });
            var modelRow = CreateSettingsRow(_modelField);
            modelRow.Add(new Button(() =>
            {
                SaveConnectionSettings();
                Bootstrap.ChatClient?.RefreshProviders(CurrentUrl(), CurrentToken());
            }) { text = "Atualizar" });
            grid.Add(modelRow);

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
                "Provider e modelo são aplicados ao criar uma nova sessão. "
                + "O modo de permissão exibido no cabeçalho é somente leitura porque o runtime não expõe um endpoint de alteração confirmado.");
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
            if (string.IsNullOrEmpty(Settings.CoderModel) && !string.IsNullOrEmpty(selected.Value.Id))
                Settings.CoderModel = selected.Value.Id;
        }

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
