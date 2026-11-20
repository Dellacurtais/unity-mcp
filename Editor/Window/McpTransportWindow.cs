using System;
using System.IO;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Broker;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    public sealed class McpTransportWindow : EditorWindow
    {
        private Vector2 _logScroll;
        private bool _tokenVisible;
        private string _brokerUrlBuffer;
        private string _pythonPathBuffer;
        private string _clientInstallMessage;
        private MessageType _clientInstallMessageType = MessageType.None;
        private ExternalMcpClientSnapshot _chatGptClientSnapshot;
        private ExternalMcpClientSnapshot _claudeClientSnapshot;
        private string _clientSnapshotFingerprint;
        private double _nextClientInspection;
        private double _nextBrokerRepaint;

        [MenuItem("Window/Unity MCP Transport")]
        public static void Open()
        {
            var window = GetWindow<McpTransportWindow>("Unity MCP");
            window.minSize = new Vector2(440, 620);
            window.Show();
        }

        private void OnEnable()
        {
            Bootstrap.OnServerCoordinationChanged += Repaint;
            Bootstrap.OnServerLog += OnLog;
            EditorApplication.update += OnEditorUpdate;
            _brokerUrlBuffer = Settings.BrokerUrl;
            _pythonPathBuffer = Settings.BrokerPythonPath;
        }

        private void OnDisable()
        {
            Bootstrap.OnServerCoordinationChanged -= Repaint;
            Bootstrap.OnServerLog -= OnLog;
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnLog(string _) => Repaint();

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < _nextBrokerRepaint) return;
            _nextBrokerRepaint = EditorApplication.timeSinceStartup + 0.5d;
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Unity MCP Transport", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            DrawTransportSection();
            EditorGUILayout.Space();
            DrawSafetySection();
            EditorGUILayout.Space();
            DrawLogSection();
        }

        private void DrawTransportSection()
        {
            EditorGUILayout.LabelField("Broker", EditorStyles.boldLabel);
            DrawBrokerTransport();

            DrawToken();
            DrawExternalClientInstallers();
        }

        private void DrawExternalClientInstallers()
        {
            var endpoint = BuildCurrentMcpEndpoint();
            var token = Settings.Token;
            RefreshExternalClientSnapshots(endpoint, token);
            var chatGpt = _chatGptClientSnapshot;
            var claude = _claudeClientSnapshot;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("AI clients", EditorStyles.boldLabel);
            DrawExternalClientRow(
                "ChatGPT",
                chatGpt,
                () => InstallExternalClient(
                    "ChatGPT",
                    ExternalMcpClientInstaller.InstallOrUpdateChatGpt));
            DrawExternalClientRow(
                "Claude Code",
                claude,
                () => InstallExternalClient(
                    "Claude Code",
                    ExternalMcpClientInstaller.InstallOrUpdateClaudeCode));

            if (!string.IsNullOrEmpty(chatGpt.Error))
                EditorGUILayout.HelpBox("ChatGPT: " + chatGpt.Error, MessageType.Warning);
            if (!string.IsNullOrEmpty(claude.Error))
                EditorGUILayout.HelpBox("Claude Code: " + claude.Error, MessageType.Warning);
            if (!string.IsNullOrEmpty(_clientInstallMessage))
                EditorGUILayout.HelpBox(_clientInstallMessage, _clientInstallMessageType);
        }

        private void RefreshExternalClientSnapshots(string endpoint, string token)
        {
            var fingerprint = endpoint + "|" + (token ?? string.Empty);
            var now = EditorApplication.timeSinceStartup;
            if (_chatGptClientSnapshot != null
                && _claudeClientSnapshot != null
                && string.Equals(
                    fingerprint, _clientSnapshotFingerprint, StringComparison.Ordinal)
                && now < _nextClientInspection)
                return;

            _chatGptClientSnapshot = ExternalMcpClientInstaller.InspectChatGpt(endpoint, token);
            _claudeClientSnapshot = ExternalMcpClientInstaller.InspectClaudeCode(endpoint, token);
            _clientSnapshotFingerprint = fingerprint;
            _nextClientInspection = now + 5d;
        }

        private static void DrawExternalClientRow(
            string clientName,
            ExternalMcpClientSnapshot snapshot,
            Action installOrUpdate)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(clientName, GUILayout.Width(100));
                EditorGUILayout.LabelField(snapshot.State, EditorStyles.miniLabel);
                var action = snapshot.IsConfigured ? "Update" : "Install";
                if (GUILayout.Button(
                        new GUIContent(
                            action,
                            "Install or update the global Unity MCP connection for " + clientName + "."),
                        GUILayout.Width(80)))
                    installOrUpdate();
            }
        }

        private void InstallExternalClient(
            string clientName,
            Action<string, string> installOrUpdate)
        {
            try
            {
                Maicon.SceneAnalyzer.Editor.Mcp.Broker.BrokerTokenSync.Converge();
                Settings.BrokerUrl = _brokerUrlBuffer?.Trim();
                var endpoint = BuildCurrentMcpEndpoint();
                installOrUpdate(endpoint, Settings.Token);
                _clientSnapshotFingerprint = null;
                _nextClientInspection = 0d;
                _clientInstallMessage = clientName
                    + " configured. Restart " + clientName + " to load the Unity tools.";
                _clientInstallMessageType = MessageType.Info;
                Bootstrap.RecordEntry(clientName + " MCP configuration installed/updated");
            }
            catch (Exception exception)
            {
                _clientInstallMessage = clientName + " installation failed: "
                    + exception.GetBaseException().Message;
                _clientInstallMessageType = MessageType.Error;
                Bootstrap.RecordEntry(clientName + " MCP configuration failed");
            }
        }

        private string BuildCurrentMcpEndpoint() => BuildMcpEndpoint(_brokerUrlBuffer);

        /// The MCP endpoint served by the broker whose bridge URL is given
        /// (ws://host:port/bridge -> http://host:port/mcp/).
        internal static string BuildMcpEndpoint(string brokerUrl)
        {
            if (Uri.TryCreate(brokerUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == "ws" || uri.Scheme == "wss")
                && uri.Port > 0)
                return new UriBuilder(uri.Scheme == "wss" ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                    uri.Host, uri.Port, "/mcp/").Uri.AbsoluteUri;
            return "http://127.0.0.1:8765/mcp/";
        }

        private void DrawBrokerTransport()
        {
            DrawManagedBroker();
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Unity bridge", EditorStyles.boldLabel);

            var snapshot = Bootstrap.BrokerBridgeState;
            var running = Bootstrap.BrokerBridge != null;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(running))
                {
                    if (GUILayout.Button("Connect"))
                    {
                        Settings.BrokerUrl = _brokerUrlBuffer?.Trim();
                        TryTransportAction(Bootstrap.StartServer, "Failed to connect broker bridge");
                    }
                }
                using (new EditorGUI.DisabledScope(!running))
                {
                    if (GUILayout.Button("Disconnect")) Bootstrap.StopServer();
                }
            }

            var state = snapshot.IsConnected ? "connected" : snapshot.State;
            if (snapshot.IsConnected)
                state += snapshot.IsActiveProject ? " · active route" : " · standby (not active)";
            EditorGUILayout.LabelField("Bridge: " + state, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(snapshot.Error))
                EditorGUILayout.HelpBox(snapshot.Error, MessageType.Warning);
            if (snapshot.IsConnected && !snapshot.IsActiveProject)
                EditorGUILayout.HelpBox(
                    "This Unity Editor is connected, but MCP calls are routed to another project. Use broker_activate_project before calling Unity tools here.",
                    MessageType.Warning);
            if (!string.IsNullOrEmpty(snapshot.ProjectKey))
                EditorGUILayout.SelectableLabel(
                    "Project: " + snapshot.ProjectKey,
                    EditorStyles.miniLabel,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (snapshot.ProcessId > 0)
                EditorGUILayout.LabelField("Editor PID: " + snapshot.ProcessId, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(snapshot.GenerationId))
                EditorGUILayout.SelectableLabel(
                    "Generation: " + snapshot.GenerationId,
                    EditorStyles.miniLabel,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (!string.IsNullOrEmpty(snapshot.RegistryHash))
                EditorGUILayout.SelectableLabel(
                    "Registry: " + snapshot.RegistryHash,
                    EditorStyles.miniLabel,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Broker URL", GUILayout.Width(80));
                using (new EditorGUI.DisabledScope(running))
                    _brokerUrlBuffer = EditorGUILayout.TextField(_brokerUrlBuffer);
            }
            var mcpEndpoint = BuildMcpEndpoint(_brokerUrlBuffer);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("MCP endpoint", GUILayout.Width(80));
                EditorGUILayout.SelectableLabel(
                    mcpEndpoint,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Copy", GUILayout.Width(50)))
                {
                    EditorGUIUtility.systemCopyBuffer = mcpEndpoint;
                    Bootstrap.RecordEntry("MCP endpoint copied to clipboard");
                }
            }
            Settings.BrokerAutoConnect = EditorGUILayout.Toggle(
                new GUIContent(
                    "Auto-connect",
                    "Reconnect this Unity project to the broker after Domain Reload."),
                Settings.BrokerAutoConnect);
            EditorGUILayout.HelpBox(
                "The managed broker remains a separate process and survives compilation and Domain Reload. You can also connect to a broker started by another operator.",
                MessageType.Info);
        }

        private void DrawManagedBroker()
        {
            var managed = ManagedBrokerProcess.Inspect();
            EditorGUILayout.LabelField("Managed Python broker", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                    managed.IsInstallInProgress
                    || managed.IsProcessActive
                    || managed.IsInstalled && managed.IsCurrentVersion))
                {
                    if (GUILayout.Button("Install"))
                    {
                        Settings.BrokerPythonPath = _pythonPathBuffer?.Trim();
                        TryTransportAction(
                            () => ManagedBrokerProcess.BeginInstall(false),
                            "Failed to install managed broker");
                    }
                }
                using (new EditorGUI.DisabledScope(managed.IsInstallInProgress || managed.IsProcessActive))
                {
                    if (GUILayout.Button("Repair"))
                    {
                        Settings.BrokerPythonPath = _pythonPathBuffer?.Trim();
                        TryTransportAction(
                            () => ManagedBrokerProcess.BeginInstall(true),
                            "Failed to repair managed broker");
                    }
                }
                if (GUILayout.Button("Logs")) OpenManagedBrokerLogs();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                    !managed.IsInstalled
                    || !managed.IsCurrentVersion
                    || managed.IsInstallInProgress
                    || managed.IsProcessActive))
                {
                    if (GUILayout.Button("Start"))
                    {
                        Settings.BrokerUrl = _brokerUrlBuffer?.Trim();
                        TryTransportAction(StartManagedBroker, "Failed to start managed broker");
                    }
                }
                using (new EditorGUI.DisabledScope(!managed.CanStopProcess))
                {
                    if (GUILayout.Button("Stop"))
                        TryTransportAction(StopManagedBroker, "Failed to stop managed broker");
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Base Python", GUILayout.Width(80));
                using (new EditorGUI.DisabledScope(managed.IsInstallInProgress))
                {
                    EditorGUI.BeginChangeCheck();
                    _pythonPathBuffer = EditorGUILayout.TextField(_pythonPathBuffer ?? string.Empty);
                    if (EditorGUI.EndChangeCheck()) Settings.BrokerPythonPath = _pythonPathBuffer?.Trim();
                    if (GUILayout.Button("Browse", GUILayout.Width(65)))
                    {
                        var initialDirectory = string.IsNullOrEmpty(_pythonPathBuffer)
                            ? string.Empty
                            : Path.GetDirectoryName(_pythonPathBuffer);
                        var selected = EditorUtility.OpenFilePanel(
                            "Select Python 3.10+ executable",
                            initialDirectory ?? string.Empty,
                            string.Empty);
                        if (!string.IsNullOrEmpty(selected))
                        {
                            _pythonPathBuffer = selected;
                            Settings.BrokerPythonPath = selected;
                        }
                    }
                }
            }

            EditorGUILayout.LabelField("Process: " + managed.State, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(managed.Message))
                EditorGUILayout.LabelField(managed.Message, EditorStyles.miniLabel);
            if (managed.ProcessId > 0)
                EditorGUILayout.LabelField("Broker PID: " + managed.ProcessId, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(managed.InstalledVersion))
                EditorGUILayout.LabelField(
                    "Installed: " + managed.InstalledVersion + " · Package: " + SceneAnalyzerVersion.Current,
                    EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(managed.PythonPath))
                EditorGUILayout.LabelField(
                    new GUIContent("Managed runtime: " + managed.PythonPath, managed.PythonPath),
                    EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(managed.EndpointVersion))
                EditorGUILayout.LabelField("Endpoint: " + managed.EndpointVersion, EditorStyles.miniLabel);
            if (managed.IsExternalProcess)
                EditorGUILayout.HelpBox(
                    "A healthy Unity MCP broker is already listening at this URL. This editor will reuse it; Start is unnecessary and Stop stays disabled because another operator owns the process.",
                    MessageType.Info);
            if (managed.IsInstalled && !managed.IsCurrentVersion)
                EditorGUILayout.HelpBox(
                    "The installed broker does not match this package version. Stop it and click Install.",
                    MessageType.Warning);
            if (managed.IsEndpointReady
                && !string.IsNullOrEmpty(managed.EndpointVersion)
                && !string.Equals(managed.EndpointVersion, SceneAnalyzerVersion.Current, StringComparison.Ordinal))
                EditorGUILayout.HelpBox(
                    "The broker listening at this URL has a different version. Stop it from its owning process before starting this package version.",
                    MessageType.Warning);
            if (!string.IsNullOrEmpty(managed.Error))
                EditorGUILayout.HelpBox(managed.Error, MessageType.Error);
        }

        // Start and Stop wait for the supervisor (seconds), so they run off the main thread
        // and report through Recent Activity; the bridge reconnects once the broker answers.
        private static void StartManagedBroker()
        {
            ManagedBrokerProcess.EnsureRunningInBackground(RecordFromBackground);
            if (Bootstrap.BrokerBridge == null) Bootstrap.StartServer();
            Bootstrap.RecordEntry("managed broker starting");
        }

        private static void StopManagedBroker()
        {
            if (Bootstrap.BrokerBridge != null) Bootstrap.StopServer();
            else Settings.BrokerAutoConnect = false;
            Bootstrap.RecordEntry("managed broker stopping");
            Task.Run(() =>
            {
                try
                {
                    ManagedBrokerProcess.Stop();
                    RecordFromBackground("managed broker process stopped");
                }
                catch (Exception exception)
                {
                    RecordFromBackground("managed broker not stopped: " + exception.Message);
                }
            });
        }

        private static void RecordFromBackground(string message)
            => _ = MainThreadDispatcher.RunAsync(() => Bootstrap.RecordEntry("[broker] " + message));

        private static void OpenManagedBrokerLogs()
        {
            Directory.CreateDirectory(ManagedBrokerProcess.RootPath);
            EditorUtility.RevealInFinder(ManagedBrokerProcess.PreferredLogPath());
        }

        private void DrawToken()
        {
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Token", GUILayout.Width(80));
                if (string.IsNullOrEmpty(Settings.Token))
                    GUILayout.Label("(none)", EditorStyles.miniLabel);
                else if (_tokenVisible)
                    EditorGUILayout.SelectableLabel(
                        Settings.Token,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                else
                    GUILayout.Label(new string('•', Mathf.Min(24, Settings.Token.Length)));

                if (GUILayout.Button(_tokenVisible ? "Hide" : "Show", GUILayout.Width(50)))
                    _tokenVisible = !_tokenVisible;
                if (GUILayout.Button("Copy", GUILayout.Width(50)))
                {
                    EditorGUIUtility.systemCopyBuffer = Settings.Token ?? string.Empty;
                    Bootstrap.RecordEntry("token copied to clipboard");
                }
                if (GUILayout.Button("Regenerate", GUILayout.Width(90)))
                    TryTransportAction(RegenerateToken, "Failed to regenerate token");
            }
        }

        private static void RegenerateToken()
        {
            var restartTransport = IsTransportRunning();
            var restartManaged = ManagedBrokerProcess.Inspect(true).IsProcessActive;
            if (restartManaged)
            {
                if (Bootstrap.BrokerBridge != null) Bootstrap.StopServer();
                ManagedBrokerProcess.Stop();
            }

            // EditorPrefs and the per-user variable change together; the broker re-reads the
            // variable, and clients with the headersHelper follow without a restart.
            Maicon.SceneAnalyzer.Editor.Mcp.Broker.BrokerTokenSync.Rotate();
            Bootstrap.RecordEntry("token regenerated");
            if (restartManaged)
            {
                ManagedBrokerProcess.Start();
                Bootstrap.StartServer();
            }
            else if (restartTransport)
            {
                Bootstrap.RestartServer();
            }
        }

        private static bool IsTransportRunning()
            => Bootstrap.BrokerBridge != null || ManagedBrokerProcess.Inspect(true).IsProcessActive;

        private static void TryTransportAction(Action action, string title)
        {
            try { action(); }
            catch (Exception exception) { EditorUtility.DisplayDialog(title, exception.Message, "OK"); }
        }

        private void DrawSafetySection()
        {
            EditorGUILayout.LabelField("Safety", EditorStyles.boldLabel);
            Settings.AllowWrites = EditorGUILayout.Toggle(
                new GUIContent("Allow writes", "Disable to make MCP tools read-only."),
                Settings.AllowWrites);
            Settings.RequireHighRiskConfirmation = EditorGUILayout.Toggle(
                new GUIContent(
                    "Confirm high-risk tools",
                    "When enabled, high-risk tools require a second call with a single-use confirmation token. Disabled by default so authorized agents can execute directly."),
                Settings.RequireHighRiskConfirmation);
            Settings.AutoActivateRequiredWindows = EditorGUILayout.Toggle(
                new GUIContent(
                    "Autonomous editor activation",
                    "Automatically opens/focuses the Scene or Game view required by a tool, keeps editor updates flowing, and restores the previous Unity window afterwards."),
                Settings.AutoActivateRequiredWindows);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Max batch ops", GUILayout.Width(120));
                var newMax = EditorGUILayout.IntField(Settings.MaxBatchOps);
                if (newMax > 0 && newMax != Settings.MaxBatchOps) Settings.MaxBatchOps = newMax;
            }

            if (!Settings.AllowWrites)
                EditorGUILayout.HelpBox(
                    "Writes disabled. All write tools return WRITE_DISABLED.",
                    MessageType.Warning);
            else if (Settings.RequireHighRiskConfirmation)
                EditorGUILayout.HelpBox(
                    "High-risk confirmation is enabled. Agents must retry gated calls with the returned token.",
                    MessageType.Info);
        }

        private void DrawLogSection()
        {
            EditorGUILayout.LabelField("Recent Activity", EditorStyles.boldLabel);
            using (var scroll = new EditorGUILayout.ScrollViewScope(
                _logScroll,
                GUILayout.MinHeight(100),
                GUILayout.MaxHeight(180)))
            {
                _logScroll = scroll.scrollPosition;
                foreach (var line in Bootstrap.RecentEntries)
                    EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
            }
        }
    }
}
