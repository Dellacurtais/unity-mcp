using System.Linq;
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
        private Vector2 _scroll;
        private string _input = "";
        private string _answerInput = "";
        private string _planNote = "";
        private bool _showSettings = true;
        private bool _showPreamble = false;
        private Vector2 _preambleScroll;
        private string _urlBuffer;
        private string _sessionBuffer;
        private static GUIStyle _bubbleStyle;
        private static GUIStyle _bubbleHeaderRoleStyle;
        private static GUIStyle _bodyStyle;
        private static GUIStyle _toolCallStyle;

        [MenuItem("Window/Coder Chat (Scene Analyzer)")]
        public static void Open()
        {
            var w = GetWindow<CoderChatWindow>("Coder Chat");
            w.minSize = new Vector2(420, 560);
            w.Show();
        }

        private void OnEnable()
        {
            _urlBuffer = Settings.CoderUrl;
            _sessionBuffer = Settings.CoderSessionId.ToString();
            if (Bootstrap.ChatSession != null)
            {
                Bootstrap.ChatSession.OnChanged += Repaint;
                Bootstrap.ChatSession.OnChanged += SyncSessionBuffer;
            }
            Bootstrap.OnCoderProvidersChanged += Repaint;

            if (Bootstrap.CoderProviders != null && Bootstrap.CoderProviders.Count == 0)
                Bootstrap.ChatClient?.RefreshProviders(Settings.CoderUrl, Settings.CoderToken);
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
            Bootstrap.OnCoderProvidersChanged -= Repaint;
        }

        private static void EnsureStyles()
        {
            if (_bubbleStyle != null) return;
            _bubbleStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(2, 2, 4, 4),
            };
            _bubbleHeaderRoleStyle = new GUIStyle(EditorStyles.boldLabel) { fixedWidth = 70 };
            _bodyStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = false,
            };
            _toolCallStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                fontStyle = FontStyle.Italic,
            };
        }

        private void OnGUI()
        {
            EnsureStyles();
            EditorGUILayout.LabelField("Coder Chat", EditorStyles.boldLabel);

            DrawSettingsSection();
            DrawPreambleSection();
            EditorGUILayout.Space(4);
            DrawMessageList();
            DrawPendingInteractions();
            DrawInput();
        }

        // -------------------- Settings --------------------

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
                    if (GUILayout.Button("Save", GUILayout.Width(50)))
                    {
                        Settings.CoderUrl = _urlBuffer.Trim();
                        Bootstrap.RecordEntry($"[chat] coder URL set to {Settings.CoderUrl}");
                    }
                    if (GUILayout.Button("Test", GUILayout.Width(50)))
                        Bootstrap.ChatClient?.TestConnection(_urlBuffer.Trim(), Settings.CoderToken);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Token", GUILayout.Width(60));
                    var t = EditorGUILayout.PasswordField(Settings.CoderToken ?? "");
                    if (t != Settings.CoderToken) Settings.CoderToken = t;
                    GUILayout.Label("(empty if MCP_AUTH_ENABLED=false)", EditorStyles.miniLabel);
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
                        Settings.CoderProjectId = 0;
                }
                EditorGUILayout.LabelField("Workdir", Bootstrap.UnityProjectRoot ?? "(unavailable)", EditorStyles.miniLabel);

                DrawProviderModelRow();

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
                        _sessionBuffer = Settings.CoderSessionId.ToString();
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
                    "then auto-creates a session, then sends. New project not showing in dashboard? " +
                    "F5 the dashboard browser tab.",
                    MessageType.Info);
            }
        }

        private void DrawProviderModelRow()
        {
            var cache = Bootstrap.CoderProviders;
            var hasCache = cache != null && cache.Count > 0;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Provider", GUILayout.Width(60));
                if (hasCache)
                {
                    var ids = cache.Select(p =>
                        p.Id + (p.HasKey ? "" : "  [no key]") + (p.IsDefault ? "  (default)" : "")
                    ).ToArray();
                    var rawIds = cache.Select(p => p.Id).ToArray();
                    int pIdx = System.Array.IndexOf(rawIds, Settings.CoderProvider);
                    if (pIdx < 0) pIdx = System.Array.FindIndex(cache.ToArray(), p => p.IsDefault);
                    if (pIdx < 0) pIdx = 0;
                    var newPIdx = EditorGUILayout.Popup(pIdx, ids, GUILayout.Width(220));
                    if (newPIdx != pIdx) Settings.CoderProvider = rawIds[newPIdx];
                }
                else
                {
                    GUILayout.Label("(loading…)", EditorStyles.miniLabel);
                }
                if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    Bootstrap.ChatClient?.RefreshProviders(Settings.CoderUrl, Settings.CoderToken);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Model", GUILayout.Width(60));
                if (hasCache)
                {
                    var prov = cache.FirstOrDefault(p => p.Id == Settings.CoderProvider) ?? cache[0];
                    if (prov.Models == null || prov.Models.Count == 0)
                    {
                        GUILayout.Label("(no models for this provider)", EditorStyles.miniLabel);
                    }
                    else
                    {
                        var modelIds = prov.Models.Select(m => m.Id).ToArray();
                        var modelLabels = prov.Models.Select(m =>
                            string.IsNullOrEmpty(m.Name) ? m.Id : $"{m.Name}  ({m.Id})"
                        ).ToArray();
                        int mIdx = System.Array.IndexOf(modelIds, Settings.CoderModel);
                        if (mIdx < 0) mIdx = 0;
                        var newMIdx = EditorGUILayout.Popup(mIdx, modelLabels, GUILayout.Width(280));
                        if (newMIdx != mIdx) Settings.CoderModel = modelIds[newMIdx];

                        var mInfo = prov.Models[mIdx];
                        if (mInfo.ContextWindow.HasValue)
                            GUILayout.Label($"ctx={mInfo.ContextWindow.Value:N0}", EditorStyles.miniLabel);
                    }
                }
                else
                {
                    var newModel = EditorGUILayout.TextField(Settings.CoderModel ?? "");
                    if (newModel != Settings.CoderModel) Settings.CoderModel = newModel;
                }
            }
        }

        // -------------------- Preamble --------------------

        private void DrawPreambleSection()
        {
            _showPreamble = EditorGUILayout.Foldout(_showPreamble,
                $"Context preamble ({(Settings.CoderAutoInjectContext ? "on" : "off")})",
                true);
            if (!_showPreamble) return;

            using (new EditorGUI.IndentLevelScope())
            {
                Settings.CoderAutoInjectContext = EditorGUILayout.Toggle(
                    new GUIContent("Auto-inject on first message",
                        "Prepends a short context block to the FIRST message of every newly-created session."),
                    Settings.CoderAutoInjectContext);

                EditorGUILayout.LabelField("Custom override (empty = use default)", EditorStyles.miniBoldLabel);
                using (var sv = new EditorGUILayout.ScrollViewScope(_preambleScroll, GUILayout.MinHeight(80), GUILayout.MaxHeight(160)))
                {
                    _preambleScroll = sv.scrollPosition;
                    var newPre = EditorGUILayout.TextArea(Settings.CoderCustomPreamble ?? "", GUILayout.ExpandHeight(true));
                    if (newPre != Settings.CoderCustomPreamble) Settings.CoderCustomPreamble = newPre;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Preview default", GUILayout.Width(120)))
                    {
                        var preview = CoderApiClient.BuildDefaultPreamble(Bootstrap.UnityProjectRoot);
                        Bootstrap.ChatSession?.AddSystemNote("PREVIEW DEFAULT PREAMBLE:\n" + preview);
                    }
                    if (GUILayout.Button("Clear override", GUILayout.Width(110)))
                        Settings.CoderCustomPreamble = "";
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // -------------------- Conversation --------------------

        private void DrawMessageList()
        {
            EditorGUILayout.LabelField("Conversation", EditorStyles.boldLabel);
            var session = Bootstrap.ChatSession;
            if (session == null) { GUILayout.Label("(chat session not initialised)"); return; }

            using (var sv = new EditorGUILayout.ScrollViewScope(_scroll,
                GUILayout.ExpandHeight(true)))
            {
                _scroll = sv.scrollPosition;
                if (session.Messages.Count == 0)
                {
                    EditorGUILayout.HelpBox("No messages yet. Type below and press Send. " +
                                            "If Session=0 and Project=0, the plugin auto-creates both.",
                        MessageType.None);
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
        }

        private void DrawMessage(ChatMessage msg)
        {
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = ColorFor(msg.Role);
            using (new EditorGUILayout.VerticalScope(_bubbleStyle))
            {
                GUI.backgroundColor = prevColor;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(LabelFor(msg.Role), _bubbleHeaderRoleStyle);
                    GUILayout.Label(msg.Timestamp.ToString("HH:mm:ss"), EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (msg.Streaming) GUILayout.Label("typing…", EditorStyles.miniLabel);
                }

                if (!string.IsNullOrEmpty(msg.Text))
                {
                    var width = position.width - 50;
                    var content = new GUIContent(msg.Text);
                    var height = _bodyStyle.CalcHeight(content, width);
                    EditorGUILayout.SelectableLabel(msg.Text, _bodyStyle,
                        GUILayout.MinHeight(height + 2),
                        GUILayout.ExpandHeight(true));
                }

                if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    GUILayout.Space(3);
                    foreach (var tc in msg.ToolCalls)
                    {
                        var line = tc.Completed
                            ? $"⚙ {tc.Name}  ({tc.DurationMs:0}ms)"
                            : $"⚙ {tc.Name}  …";
                        GUILayout.Label(line, _toolCallStyle);
                        if (tc.Completed && !string.IsNullOrEmpty(tc.ResultText))
                        {
                            var tcContent = new GUIContent("    " + tc.ResultText);
                            var tcHeight = _toolCallStyle.CalcHeight(tcContent, position.width - 60);
                            GUILayout.Label(tcContent, _toolCallStyle, GUILayout.MinHeight(tcHeight));
                        }
                    }
                }
            }
        }

        // -------------------- Pending interactions --------------------

        private void DrawPendingInteractions()
        {
            var session = Bootstrap.ChatSession;
            if (session == null) return;

            if (session.CurrentQuestion != null) DrawPendingQuestion(session, session.CurrentQuestion);
            if (session.CurrentPlan != null) DrawPendingPlan(session, session.CurrentPlan);
            if (session.CurrentTasks != null) DrawPendingTasks(session, session.CurrentTasks);
            if (!string.IsNullOrEmpty(session.LastError))
                EditorGUILayout.HelpBox(session.LastError, MessageType.Error);
        }

        private void DrawPendingQuestion(ChatSession session, PendingQuestion q)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.95f, 0.6f);
            using (new EditorGUILayout.VerticalScope(_bubbleStyle))
            {
                GUI.backgroundColor = prev;
                GUILayout.Label("Agent is asking", EditorStyles.boldLabel);

                var content = new GUIContent(q.Question);
                var h = _bodyStyle.CalcHeight(content, position.width - 50);
                EditorGUILayout.SelectableLabel(q.Question, _bodyStyle, GUILayout.MinHeight(h + 2));

                if (q.Options != null && q.Options.Count > 0)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        foreach (var opt in q.Options)
                        {
                            if (GUILayout.Button(opt, GUILayout.Height(24)))
                                Bootstrap.ChatClient?.AnswerQuestion(
                                    Settings.CoderUrl, Settings.CoderToken,
                                    Settings.CoderSessionId, q.Id, opt);
                        }
                    }
                }

                if (q.AllowFreeform)
                {
                    GUILayout.Label("Or type a free-form answer:", EditorStyles.miniLabel);
                    _answerInput = EditorGUILayout.TextArea(_answerInput, GUILayout.MinHeight(50));
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_answerInput)))
                        {
                            if (GUILayout.Button("Send answer", GUILayout.Width(120)))
                            {
                                Bootstrap.ChatClient?.AnswerQuestion(
                                    Settings.CoderUrl, Settings.CoderToken,
                                    Settings.CoderSessionId, q.Id, _answerInput.Trim());
                                _answerInput = "";
                            }
                        }
                        if (GUILayout.Button("Skip", GUILayout.Width(60)))
                        {
                            session.ClearPendingQuestion();
                        }
                    }
                }
            }
        }

        private void DrawPendingPlan(ChatSession session, PendingPlan p)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.7f, 0.85f, 1f);
            using (new EditorGUILayout.VerticalScope(_bubbleStyle))
            {
                GUI.backgroundColor = prev;
                GUILayout.Label("Plan submitted", EditorStyles.boldLabel);

                var content = new GUIContent(p.Plan);
                var h = _bodyStyle.CalcHeight(content, position.width - 50);
                EditorGUILayout.SelectableLabel(p.Plan, _bodyStyle, GUILayout.MinHeight(h + 2));

                GUILayout.Label("Note (optional):", EditorStyles.miniLabel);
                _planNote = EditorGUILayout.TextArea(_planNote, GUILayout.MinHeight(40));

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Approve", GUILayout.Width(110)))
                    {
                        Bootstrap.ChatClient?.RespondToPlan(
                            Settings.CoderUrl, Settings.CoderToken,
                            Settings.CoderSessionId, p.Id, true, _planNote);
                        _planNote = "";
                    }
                    if (GUILayout.Button("Reject", GUILayout.Width(110)))
                    {
                        Bootstrap.ChatClient?.RespondToPlan(
                            Settings.CoderUrl, Settings.CoderToken,
                            Settings.CoderSessionId, p.Id, false, _planNote);
                        _planNote = "";
                    }
                }
            }
        }

        private void DrawPendingTasks(ChatSession session, PendingTasks t)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.7f, 1f, 0.8f);
            using (new EditorGUILayout.VerticalScope(_bubbleStyle))
            {
                GUI.backgroundColor = prev;
                GUILayout.Label("Tasks submitted", EditorStyles.boldLabel);

                var content = new GUIContent(t.Summary);
                var h = _bodyStyle.CalcHeight(content, position.width - 50);
                EditorGUILayout.SelectableLabel(t.Summary, _bodyStyle, GUILayout.MinHeight(h + 2));

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Approve", GUILayout.Width(110)))
                        Bootstrap.ChatClient?.RespondToTasks(
                            Settings.CoderUrl, Settings.CoderToken,
                            Settings.CoderSessionId, t.ToolUseId, true);
                    if (GUILayout.Button("Reject", GUILayout.Width(110)))
                        Bootstrap.ChatClient?.RespondToTasks(
                            Settings.CoderUrl, Settings.CoderToken,
                            Settings.CoderSessionId, t.ToolUseId, false);
                }
            }
        }

        // -------------------- Composer --------------------

        private void DrawInput()
        {
            EditorGUILayout.LabelField("Message", EditorStyles.boldLabel);
            _input = EditorGUILayout.TextArea(_input, GUILayout.MinHeight(70));
            using (new EditorGUILayout.HorizontalScope())
            {
                var newThinking = GUILayout.Toggle(Settings.CoderThinking,
                    new GUIContent(" 🧠 Think mode",
                        "Sends `thinking: true` on every outgoing message."),
                    "Button", GUILayout.Width(120));
                if (newThinking != Settings.CoderThinking) Settings.CoderThinking = newThinking;

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

        // -------------------- Helpers --------------------

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

        private static Color ColorFor(ChatRole role)
        {
            switch (role)
            {
                case ChatRole.User: return new Color(0.85f, 0.95f, 1f);     // light blue
                case ChatRole.Assistant: return new Color(0.92f, 0.92f, 0.95f); // soft gray
                case ChatRole.System: return new Color(0.95f, 0.95f, 0.85f);    // pale yellow
                case ChatRole.Error: return new Color(1f, 0.85f, 0.85f);        // light red
                default: return Color.white;
            }
        }
    }
}
