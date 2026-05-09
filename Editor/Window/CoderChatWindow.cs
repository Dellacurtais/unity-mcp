using Maicon.SceneAnalyzer.Editor.Chat;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    /// Embedded chat panel — Model B. Talks to the coder runtime in D:\MCP via
    /// `/api/coder/sessions/<id>/messages` (SSE). The Unity plugin still hosts
    /// the MCP server (Model A) so the agent on the other side can call back
    /// into Unity for ext_unity_editor__* tools while answering.
    public sealed class CoderChatWindow : EditorWindow
    {
        private const string AgentProfileName = "build";

        private Vector2 _scroll;
        private string _input = "";
        private bool _showSettings = true;
        private string _urlBuffer;
        private string _sessionBuffer;

        [MenuItem("Window/Coder Chat (Scene Analyzer)")]
        public static void Open()
        {
            var w = GetWindow<CoderChatWindow>("Coder Chat");
            w.minSize = new Vector2(380, 520);
            w.Show();
        }

        private void OnEnable()
        {
            _urlBuffer = Settings.CoderUrl;
            _sessionBuffer = Settings.CoderSessionId.ToString();
            if (Bootstrap.ChatSession != null)
            {
                Bootstrap.ChatSession.OnChanged += Repaint;
                // Re-sync the Session text field whenever the transcript updates,
                // so the field reflects the id assigned by auto-create or by the
                // 'New' button instead of stale local state.
                Bootstrap.ChatSession.OnChanged += SyncSessionBuffer;
            }
        }

        private void SyncSessionBuffer()
        {
            var s = Settings.CoderSessionId.ToString();
            if (s != _sessionBuffer) _sessionBuffer = s;
        }

        private void OnDisable()
        {
            if (Bootstrap.ChatSession != null)
            {
                Bootstrap.ChatSession.OnChanged -= Repaint;
                Bootstrap.ChatSession.OnChanged -= SyncSessionBuffer;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Coder Chat", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawSettingsSection();
            EditorGUILayout.Space();
            DrawMessageList();
            EditorGUILayout.Space();
            DrawInput();
        }

        // --- Sections ---

        private void DrawSettingsSection()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "Coder server", true);
            if (!_showSettings) return;

            using (new EditorGUI.IndentLevelScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("URL", GUILayout.Width(60));
                    _urlBuffer = EditorGUILayout.TextField(_urlBuffer);
                    if (GUILayout.Button("Save", GUILayout.Width(60)))
                    {
                        Settings.CoderUrl = _urlBuffer.Trim();
                        Bootstrap.RecordEntry($"[chat] coder URL set to {Settings.CoderUrl}");
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Token", GUILayout.Width(60));
                    var t = EditorGUILayout.PasswordField(Settings.CoderToken ?? "");
                    if (t != Settings.CoderToken) Settings.CoderToken = t;
                    GUILayout.Label("(bearer; leave empty if MCP_AUTH_ENABLED=false)", EditorStyles.miniLabel);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Project", GUILayout.Width(60));
                    var newProjectId = EditorGUILayout.IntField(Settings.CoderProjectId, GUILayout.Width(80));
                    if (newProjectId >= 0 && newProjectId != Settings.CoderProjectId)
                        Settings.CoderProjectId = newProjectId;
                    if (GUILayout.Button("Auto-detect", GUILayout.Width(110)))
                    {
                        Bootstrap.ChatClient?.EnsureProject(
                            Settings.CoderUrl,
                            Settings.CoderToken,
                            Bootstrap.UnityProjectRoot);
                    }
                    if (GUILayout.Button("Clear", GUILayout.Width(50)))
                    {
                        Settings.CoderProjectId = 0;
                    }
                }
                EditorGUILayout.LabelField("Workdir", Bootstrap.UnityProjectRoot ?? "(unavailable)", EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Provider", GUILayout.Width(60));
                    var providers = new[] { "anthropic", "openai", "google", "ollama" };
                    int pIdx = System.Array.IndexOf(providers, Settings.CoderProvider);
                    if (pIdx < 0) pIdx = 0;
                    var newPIdx = EditorGUILayout.Popup(pIdx, providers, GUILayout.Width(140));
                    if (newPIdx != pIdx) Settings.CoderProvider = providers[newPIdx];

                    GUILayout.Label("Model", GUILayout.Width(50));
                    var newModel = EditorGUILayout.TextField(Settings.CoderModel ?? "");
                    if (newModel != Settings.CoderModel) Settings.CoderModel = newModel;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Session", GUILayout.Width(60));
                    _sessionBuffer = EditorGUILayout.TextField(_sessionBuffer);
                    if (GUILayout.Button("Set", GUILayout.Width(50)))
                    {
                        if (int.TryParse(_sessionBuffer, out var sid)) Settings.CoderSessionId = sid;
                    }
                    if (GUILayout.Button("New", GUILayout.Width(50)))
                    {
                        // Actually creates a session on the server (POST /api/coder/sessions)
                        // and stores the returned id. Auto-fills the field on success.
                        Bootstrap.ChatSession?.Reset();
                        Bootstrap.ChatClient?.CreateSession(
                            Settings.CoderUrl,
                            Settings.CoderToken,
                            Settings.CoderProjectId,
                            $"Unity chat — {System.DateTime.Now:HH:mm:ss}",
                            Settings.CoderProvider,
                            Settings.CoderModel,
                            Settings.CoderAgent);
                    }
                    if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    {
                        _sessionBuffer = Settings.CoderSessionId.ToString();
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Profile", GUILayout.Width(60));
                    var profiles = new[] { "auto", "bootstrap", "navigate", "full" };
                    int idx = System.Array.IndexOf(profiles, Settings.CoderProfile);
                    if (idx < 0) idx = 0;
                    var newIdx = EditorGUILayout.Popup(idx, profiles, GUILayout.Width(140));
                    if (newIdx != idx) Settings.CoderProfile = profiles[newIdx];

                    GUILayout.Label("Agent", GUILayout.Width(50));
                    var agents = new[] { "build", "explore", "plan", "admin" };
                    int aIdx = System.Array.IndexOf(agents, Settings.CoderAgent);
                    if (aIdx < 0) aIdx = 0;
                    var newAIdx = EditorGUILayout.Popup(aIdx, agents);
                    if (newAIdx != aIdx) Settings.CoderAgent = agents[newAIdx];
                }

                EditorGUILayout.HelpBox(
                    "If Project=0, Send auto-detects (or creates) a D:\\MCP project pointing at this Unity workdir, " +
                    "then auto-creates a session, then sends. Use 'Auto-detect' to do just step 1 without sending. " +
                    "The Unity MCP server (Window → Scene Analyzer) must also be running and registered in the dashboard " +
                    "so the agent can call ext_unity_editor__* tools while replying.",
                    MessageType.Info);
            }
        }

        private void DrawMessageList()
        {
            EditorGUILayout.LabelField("Conversation", EditorStyles.boldLabel);
            var session = Bootstrap.ChatSession;
            if (session == null) { GUILayout.Label("(chat session not initialised)"); return; }

            using (var sv = new EditorGUILayout.ScrollViewScope(_scroll, GUILayout.MinHeight(280), GUILayout.ExpandHeight(true)))
            {
                _scroll = sv.scrollPosition;
                if (session.Messages.Count == 0)
                {
                    EditorGUILayout.HelpBox("No messages yet. Type below and press Send.", MessageType.None);
                }
                else
                {
                    foreach (var msg in session.Messages) DrawMessage(msg);
                }
            }

            if (session.IsRunning)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("● in progress…", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                        Bootstrap.ChatClient?.Cancel();
                }
            }

            if (!string.IsNullOrEmpty(session.LastError))
                EditorGUILayout.HelpBox(session.LastError, MessageType.Error);
        }

        private void DrawMessage(ChatMessage msg)
        {
            var bg = new GUIStyle(EditorStyles.helpBox);
            using (new EditorGUILayout.VerticalScope(bg))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(LabelFor(msg.Role), EditorStyles.boldLabel, GUILayout.Width(80));
                    GUILayout.Label(msg.Timestamp.ToString("HH:mm:ss"), EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (msg.Streaming) GUILayout.Label("…", EditorStyles.miniLabel);
                }

                if (!string.IsNullOrEmpty(msg.Text))
                {
                    EditorGUILayout.SelectableLabel(msg.Text,
                        EditorStyles.wordWrappedLabel,
                        GUILayout.MinHeight(EditorGUIUtility.singleLineHeight),
                        GUILayout.ExpandHeight(true));
                }

                if (msg.ToolCalls != null)
                {
                    foreach (var tc in msg.ToolCalls)
                    {
                        var line = tc.Completed
                            ? $"⚙ {tc.Name}  ({tc.DurationMs:0}ms)"
                            : $"⚙ {tc.Name}  …";
                        GUILayout.Label(line, EditorStyles.miniLabel);
                        if (tc.Completed && !string.IsNullOrEmpty(tc.ResultText))
                            GUILayout.Label("    " + tc.ResultText, EditorStyles.miniLabel);
                    }
                }
            }
        }

        private void DrawInput()
        {
            EditorGUILayout.LabelField("Message", EditorStyles.boldLabel);
            _input = EditorGUILayout.TextArea(_input, GUILayout.MinHeight(60));
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_input)
                    || (Bootstrap.ChatSession?.IsRunning ?? false)))
                {
                    if (GUILayout.Button("Send", GUILayout.Width(120))) DoSend();
                }
                if (GUILayout.Button("Clear", GUILayout.Width(80)))
                {
                    _input = "";
                    Bootstrap.ChatSession?.Reset();
                }
            }
        }

        private void DoSend()
        {
            var text = _input.Trim();
            if (string.IsNullOrEmpty(text)) return;

            Bootstrap.ChatSession.AddUser(text);
            Bootstrap.ChatClient.Send(
                Settings.CoderUrl,
                Settings.CoderToken,
                Settings.CoderSessionId,
                text,
                Settings.CoderProfile,
                Settings.CoderAgent);
            _input = "";
            GUI.FocusControl(null);
        }

        private static string LabelFor(ChatRole role)
        {
            switch (role)
            {
                case ChatRole.User: return "You";
                case ChatRole.Assistant: return "Coder";
                case ChatRole.System: return "System";
                case ChatRole.Error: return "Error";
                default: return role.ToString();
            }
        }
    }
}
