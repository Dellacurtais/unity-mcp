using System;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    public sealed class SceneAnalyzerWindow : EditorWindow
    {
        private Vector2 _logScroll;
        private Vector2 _issuesScroll;
        private bool _tokenVisible;
        private string _portBuffer;
        private int _issueCount;

        [MenuItem("Window/Scene Analyzer")]
        public static void Open()
        {
            var w = GetWindow<SceneAnalyzerWindow>("Scene Analyzer");
            w.minSize = new Vector2(360, 480);
            w.Show();
        }

        private void OnEnable()
        {
            Bootstrap.OnIssuesChanged += Repaint;
            Bootstrap.OnServerCoordinationChanged += Repaint;
            Bootstrap.OnServerLog += OnLog;
            _portBuffer = Settings.Port.ToString();
        }

        private void OnDisable()
        {
            Bootstrap.OnIssuesChanged -= Repaint;
            Bootstrap.OnServerCoordinationChanged -= Repaint;
            Bootstrap.OnServerLog -= OnLog;
        }

        private void OnLog(string _) => Repaint();

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Scene Analyzer (MCP)", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawServerSection();
            EditorGUILayout.Space();
            DrawSettingsSection();
            EditorGUILayout.Space();
            DrawSceneSection();
            EditorGUILayout.Space();
            DrawIssuesSection();
            EditorGUILayout.Space();
            DrawLogSection();
        }

        private void DrawServerSection()
        {
            EditorGUILayout.LabelField("MCP Server", EditorStyles.boldLabel);

            var server = Bootstrap.Server;
            var instanceState = Bootstrap.ServerInstanceState;
            bool running = server != null && server.IsRunning;
            bool remoteOwner = instanceState.HasRemoteOwner;
            bool transitioning = instanceState.IsTransitioning;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(running || remoteOwner || transitioning))
                {
                    if (GUILayout.Button("Start"))
                    {
                        if (int.TryParse(_portBuffer, out var p)) Settings.Port = p;
                        try { Bootstrap.StartServer(); }
                        catch (Exception e) { EditorUtility.DisplayDialog("Failed to start", e.Message, "OK"); }
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

            EditorGUILayout.LabelField(ServerStatus(server, instanceState), EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(instanceState.OwnerProjectPath) && remoteOwner)
                EditorGUILayout.SelectableLabel(instanceState.OwnerProjectPath, EditorStyles.miniLabel,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (transitioning && !string.IsNullOrEmpty(instanceState.HandoverMessage))
                EditorGUILayout.HelpBox(instanceState.HandoverMessage, MessageType.Info);
            else if (string.Equals(instanceState.HandoverStatus, "failed", StringComparison.Ordinal)
                     && !string.IsNullOrEmpty(instanceState.HandoverMessage))
                EditorGUILayout.HelpBox(instanceState.HandoverMessage, MessageType.Warning);

            var coordinationError = instanceState.Error ?? Bootstrap.ServerCoordinationError;
            if (!string.IsNullOrEmpty(coordinationError))
                EditorGUILayout.HelpBox($"Multi-project coordination unavailable: {coordinationError}", MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Port", GUILayout.Width(40));
                using (new EditorGUI.DisabledScope(running || remoteOwner || transitioning))
                    _portBuffer = EditorGUILayout.TextField(_portBuffer, GUILayout.Width(80));
                GUILayout.FlexibleSpace();
                Settings.AutoStartServer = GUILayout.Toggle(Settings.AutoStartServer, " Auto-start on load");
            }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Token", GUILayout.Width(40));
                if (string.IsNullOrEmpty(Settings.Token))
                    GUILayout.Label("(none)", EditorStyles.miniLabel);
                else if (_tokenVisible)
                    EditorGUILayout.SelectableLabel(Settings.Token, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                else
                    GUILayout.Label(new string('•', Mathf.Min(24, Settings.Token.Length)));

                if (GUILayout.Button(_tokenVisible ? "Hide" : "Show", GUILayout.Width(50)))
                    _tokenVisible = !_tokenVisible;
                if (GUILayout.Button("Copy", GUILayout.Width(50)))
                {
                    EditorGUIUtility.systemCopyBuffer = Settings.Token ?? "";
                    Bootstrap.RecordEntry("token copied to clipboard");
                }
                using (new EditorGUI.DisabledScope(remoteOwner || transitioning))
                {
                    if (GUILayout.Button("Regenerate", GUILayout.Width(90)))
                    {
                        Settings.Token = Settings.GenerateToken();
                        Bootstrap.RecordEntry("token regenerated");
                        if (Bootstrap.Server.IsRunning)
                            Bootstrap.RestartServer();
                    }
                }
            }
        }

        private static string ServerStatus(Mcp.HttpMcpServer server, Mcp.McpServerInstanceSnapshot instanceState)
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
                $"Move the MCP server from '{source}' to this Unity project?\n\n" +
                $"Port {instanceState.Port} and the current token will be preserved. " +
                "Requests in flight may receive HTTP 503 and should be retried.",
                "Bring here",
                "Cancel");
            if (!confirmed) return;

            try { Bootstrap.BringServerHere(); }
            catch (Exception e) { EditorUtility.DisplayDialog("Transfer failed", e.Message, "OK"); }
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.LabelField("Safety", EditorStyles.boldLabel);
            Settings.AllowWrites = EditorGUILayout.Toggle(
                new GUIContent("Allow writes", "Disable to make the agent read-only on this scene."),
                Settings.AllowWrites);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Max batch ops", GUILayout.Width(120));
                var newMax = EditorGUILayout.IntField(Settings.MaxBatchOps);
                if (newMax > 0 && newMax != Settings.MaxBatchOps) Settings.MaxBatchOps = newMax;
            }

            if (!Settings.AllowWrites)
                EditorGUILayout.HelpBox("Writes disabled. All write tools return WRITE_DISABLED.", MessageType.Warning);
        }

        private void DrawSceneSection()
        {
            EditorGUILayout.LabelField("Active Scene", EditorStyles.boldLabel);
            var s = Bootstrap.Snapshot;
            if (s == null) { GUILayout.Label("(snapshot unavailable)"); return; }
            s.RefreshIfNeeded();
            EditorGUILayout.LabelField($"{s.SceneName ?? "(none)"}  ·  {s.Count} GameObjects");
        }

        private void DrawIssuesSection()
        {
            EditorGUILayout.LabelField("Issues", EditorStyles.boldLabel);
            var runner = Bootstrap.Runner;
            if (runner == null) { GUILayout.Label("(runner unavailable)"); return; }

            var issues = runner.GetIssues();
            _issueCount = issues.Count;
            EditorGUILayout.LabelField($"{_issueCount} reported");

            using (var sv = new EditorGUILayout.ScrollViewScope(_issuesScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(200)))
            {
                _issuesScroll = sv.scrollPosition;
                foreach (var i in issues)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(SeverityIcon(i.Severity), GUILayout.Width(60));
                        GUILayout.Label($"[{i.Category}] {i.Message}", EditorStyles.miniLabel);
                        GUILayout.FlexibleSpace();
                        if (i.InstanceID != 0 && GUILayout.Button("Select", GUILayout.Width(60)))
                        {
                            var obj = EditorUtility.InstanceIDToObject(i.InstanceID);
                            if (obj != null)
                            {
                                Selection.activeObject = obj;
                                EditorGUIUtility.PingObject(obj);
                            }
                        }
                    }
                }
            }
        }

        private void DrawLogSection()
        {
            EditorGUILayout.LabelField("Recent Activity", EditorStyles.boldLabel);
            using (var sv = new EditorGUILayout.ScrollViewScope(_logScroll, GUILayout.MinHeight(80), GUILayout.MaxHeight(160)))
            {
                _logScroll = sv.scrollPosition;
                foreach (var line in Bootstrap.RecentEntries)
                    EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
            }
        }

        private static string SeverityIcon(IssueSeverity s)
        {
            switch (s)
            {
                case IssueSeverity.Error: return "ERR";
                case IssueSeverity.Warning: return "WARN";
                default: return "INFO";
            }
        }
    }
}
