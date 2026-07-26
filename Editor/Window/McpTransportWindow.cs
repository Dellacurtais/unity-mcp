using System;
using System.IO;
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
        private string _portBuffer;
        private string _brokerUrlBuffer;
        private string _pythonPathBuffer;
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
            _portBuffer = Settings.Port.ToString();
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
            if (Settings.TransportMode != McpTransportMode.Broker) return;
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
            EditorGUILayout.LabelField("Transport", EditorStyles.boldLabel);
            var mode = (McpTransportMode)EditorGUILayout.EnumPopup("Mode", Settings.TransportMode);
            if (mode != Settings.TransportMode)
            {
                if (IsTransportRunning())
                {
                    EditorUtility.DisplayDialog(
                        "Stop transport first",
                        "Stop the current MCP transport before changing modes.",
                        "OK");
                }
                else
                {
                    Settings.TransportMode = mode;
                }
            }

            if (Settings.TransportMode == McpTransportMode.Broker)
                DrawBrokerTransport();
            else
                DrawEmbeddedTransport();

            DrawToken();
        }

        private void DrawEmbeddedTransport()
        {
            var server = Bootstrap.Server;
            var instanceState = Bootstrap.ServerInstanceState;
            var running = server != null && server.IsRunning;
            var remoteOwner = instanceState.HasRemoteOwner;
            var transitioning = instanceState.IsTransitioning;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(running || remoteOwner || transitioning))
                {
                    if (GUILayout.Button("Start"))
                    {
                        if (int.TryParse(_portBuffer, out var port)) Settings.Port = port;
                        TryTransportAction(Bootstrap.StartServer, "Failed to start embedded server");
                    }
                }
                using (new EditorGUI.DisabledScope(!running))
                {
                    if (GUILayout.Button("Stop")) Bootstrap.StopServer();
                }
                using (new EditorGUI.DisabledScope(!instanceState.CanBringHere))
                {
                    if (GUILayout.Button("Bring here", GUILayout.Width(90)))
                        ConfirmBringServerHere(instanceState);
                }
            }

            EditorGUILayout.LabelField(EmbeddedStatus(server, instanceState), EditorStyles.miniLabel);
            if (remoteOwner && !string.IsNullOrEmpty(instanceState.OwnerProjectPath))
                EditorGUILayout.SelectableLabel(
                    instanceState.OwnerProjectPath,
                    EditorStyles.miniLabel,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (transitioning && !string.IsNullOrEmpty(instanceState.HandoverMessage))
                EditorGUILayout.HelpBox(instanceState.HandoverMessage, MessageType.Info);

            var coordinationError = instanceState.Error ?? Bootstrap.ServerCoordinationError;
            if (!string.IsNullOrEmpty(coordinationError))
                EditorGUILayout.HelpBox(
                    "Multi-project coordination unavailable: " + coordinationError,
                    MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Port", GUILayout.Width(80));
                using (new EditorGUI.DisabledScope(running || remoteOwner || transitioning))
                    _portBuffer = EditorGUILayout.TextField(_portBuffer, GUILayout.Width(100));
                GUILayout.FlexibleSpace();
                Settings.AutoStartServer = GUILayout.Toggle(
                    Settings.AutoStartServer,
                    " Auto-start on load");
            }
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
            EditorGUILayout.LabelField("Bridge: " + state, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(snapshot.Error))
                EditorGUILayout.HelpBox(snapshot.Error, MessageType.Warning);
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
                using (new EditorGUI.DisabledScope(!managed.IsProcessActive))
                {
                    if (GUILayout.Button("Stop"))
                        TryTransportAction(StopManagedBroker, "Failed to stop managed broker");
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Python", GUILayout.Width(80));
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
            if (managed.IsInstalled && !managed.IsCurrentVersion)
                EditorGUILayout.HelpBox(
                    "The installed broker does not match this package version. Stop it and click Install.",
                    MessageType.Warning);
            if (!string.IsNullOrEmpty(managed.Error))
                EditorGUILayout.HelpBox(managed.Error, MessageType.Error);
        }

        private static void StartManagedBroker()
        {
            ManagedBrokerProcess.Start();
            if (Bootstrap.BrokerBridge == null) Bootstrap.StartServer();
            Bootstrap.RecordEntry("managed broker process started");
        }

        private static void StopManagedBroker()
        {
            if (Bootstrap.BrokerBridge != null) Bootstrap.StopServer();
            else Settings.BrokerAutoConnect = false;
            ManagedBrokerProcess.Stop();
            Bootstrap.RecordEntry("managed broker process stopped");
        }

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
            var restartManaged = Settings.TransportMode == McpTransportMode.Broker
                && ManagedBrokerProcess.Inspect().IsProcessActive;
            if (restartManaged)
            {
                if (Bootstrap.BrokerBridge != null) Bootstrap.StopServer();
                ManagedBrokerProcess.Stop();
            }

            Settings.Token = Settings.GenerateToken();
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
        {
            return Settings.TransportMode == McpTransportMode.Broker
                ? Bootstrap.BrokerBridge != null || ManagedBrokerProcess.Inspect().IsProcessActive
                : Bootstrap.Server != null && Bootstrap.Server.IsRunning;
        }

        private static string EmbeddedStatus(
            Mcp.HttpMcpServer server,
            Mcp.McpServerInstanceSnapshot instanceState)
        {
            if (server != null && server.IsRunning)
                return $"running here · http://127.0.0.1:{server.Port}/mcp/";
            if (instanceState.HasRemoteOwner)
                return $"running in {instanceState.OwnerProjectName} · http://127.0.0.1:{instanceState.Port}/mcp/";
            if (instanceState.IsTransitioning)
                return $"transferring · http://127.0.0.1:{instanceState.Port}/mcp/";
            return "stopped";
        }

        private static void ConfirmBringServerHere(Mcp.McpServerInstanceSnapshot instanceState)
        {
            var source = string.IsNullOrEmpty(instanceState.OwnerProjectName)
                ? instanceState.OwnerProjectPath
                : instanceState.OwnerProjectName;
            var confirmed = EditorUtility.DisplayDialog(
                "Bring MCP server here?",
                $"Move the embedded MCP server from '{source}' to this Unity project?\n\n" +
                $"Port {instanceState.Port} and the current token will be preserved.",
                "Bring here",
                "Cancel");
            if (!confirmed) return;
            TryTransportAction(Bootstrap.BringServerHere, "Transfer failed");
        }

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
