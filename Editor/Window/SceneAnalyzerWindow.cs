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
            Bootstrap.OnServerLog += OnLog;
            _portBuffer = Settings.Port.ToString();
        }

        private void OnDisable()
        {
            Bootstrap.OnIssuesChanged -= Repaint;
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
            using (new EditorGUILayout.HorizontalScope())
            {
                bool running = server != null && server.IsRunning;
                using (new EditorGUI.DisabledScope(running))
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
                GUILayout.Label(running ? $"running · http://127.0.0.1:{server.Port}/mcp/" : "stopped");
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Port", GUILayout.Width(40));
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
                if (GUILayout.Button("Regenerate", GUILayout.Width(90)))
                {
                    Settings.Token = Settings.GenerateToken();
                    Bootstrap.RecordEntry("token regenerated");
                    if (Bootstrap.Server.IsRunning)
                    {
                        Bootstrap.StopServer();
                        Bootstrap.StartServer();
                    }
                }
            }
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
