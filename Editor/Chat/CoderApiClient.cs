using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FormattingNs = Newtonsoft.Json.Formatting;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    /// HTTP client for the coder agent in D:\MCP. Posts a user message to
    /// `/api/coder/sessions/<id>/messages` and parses the SSE response stream
    /// line-by-line, dispatching events into a ChatSession on the editor main
    /// thread.
    ///
    /// Reference: D:\MCP\src\apps\http-api\server.ts:345-348 (server),
    /// D:\MCP\src\apps\dashboard\hooks\agent-stream\openStream.ts:28-172
    /// (browser client mirrored here).
    public sealed class CoderApiClient
    {
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        private CancellationTokenSource _cts;
        private readonly ChatSession _session;

        // Sessions that haven't received their context preamble yet. A session
        // gets added here the moment it's created (either via auto-create on
        // Send, or via the "New" button) and removed on the first message that
        // actually goes out. Locked because creation and send paths can race
        // on different Task.Run continuations.
        private readonly HashSet<int> _pendingPreambleFor = new();
        private readonly object _preambleLock = new();

        private void MarkPreamblePending(int sessionId)
        {
            if (sessionId <= 0) return;
            lock (_preambleLock) _pendingPreambleFor.Add(sessionId);
        }

        private bool ConsumePendingPreamble(int sessionId)
        {
            if (sessionId <= 0) return false;
            lock (_preambleLock) return _pendingPreambleFor.Remove(sessionId);
        }

        public CoderApiClient(ChatSession session)
        {
            _session = session;
        }

        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
        }

        public void Send(string serverUrl, string token, int sessionId, string content,
            string profile, string agent)
        {
            // Caller is the main thread (button click). Don't dispatch + wait —
            // would deadlock against the dispatcher running on EditorApplication.update.
            if (IsRunning)
            {
                _session.AddSystemNote("a request is already in flight — cancel it first");
                return;
            }

            _cts = new CancellationTokenSource();
            var token2 = _cts.Token;
            // Capture EVERYTHING from Settings on the main thread before
            // dispatching — EditorPrefs is not guaranteed thread-safe.
            var unityRoot = Bootstrap.UnityProjectRoot;
            var injectContext = Settings.CoderAutoInjectContext;
            var customPreamble = Settings.CoderCustomPreamble;
            var snapProjectId = Settings.CoderProjectId;
            var snapProvider = Settings.CoderProvider;
            var snapModel = Settings.CoderModel;
            var snapThinking = Settings.CoderThinking;

            _ = Task.Run(async () =>
            {
                try
                {
                    int effectiveSessionId = sessionId;

                    if (effectiveSessionId <= 0)
                    {
                        int projectId = snapProjectId;
                        if (projectId <= 0)
                        {
                            await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                                $"[1/3] ensuring D:\\MCP project for workdir `{unityRoot}` (server={serverUrl})…"));
                            projectId = await EnsureProjectAsync(serverUrl, token, unityRoot, token2);
                            if (projectId <= 0)
                            {
                                await MainThreadDispatcher.RunAsync(() => _session.AddError(
                                    "step 1 failed (no project id) — aborting before session/message"));
                                return;
                            }
                            await MainThreadDispatcher.RunAsync(() =>
                            {
                                Settings.CoderProjectId = projectId;
                                _session.AddSystemNote($"[1/3] project id = {projectId}");
                            });
                        }
                        else
                        {
                            await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                                $"[1/3] using existing project id {projectId}"));
                        }

                        await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                            $"[2/3] creating session under project {projectId}…"));
                        effectiveSessionId = await CreateSessionAsync(
                            serverUrl, token, projectId,
                            $"Unity chat — {DateTime.Now:HH:mm:ss}",
                            snapProvider, snapModel, agent, token2);
                        if (effectiveSessionId <= 0)
                        {
                            await MainThreadDispatcher.RunAsync(() => _session.AddError(
                                "step 2 failed (no session id) — aborting before message"));
                            return;
                        }
                        await MainThreadDispatcher.RunAsync(() =>
                        {
                            Settings.CoderSessionId = effectiveSessionId;
                            _session.AddSystemNote($"[2/3] session id = {effectiveSessionId}");
                        });
                        MarkPreamblePending(effectiveSessionId);
                    }

                    var contentToSend = content;
                    var injectThisTime = injectContext && ConsumePendingPreamble(effectiveSessionId);
                    if (injectThisTime)
                    {
                        var preamble = string.IsNullOrEmpty(customPreamble)
                            ? BuildDefaultPreamble(unityRoot)
                            : customPreamble;
                        contentToSend = preamble + "\n\n---\n\n" + content;
                        await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                            "(injected unity context preamble on first message of this session)"));
                    }

                    await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                        $"[3/3] POST /api/coder/sessions/{effectiveSessionId}/messages " +
                        (snapThinking ? "(🧠 think mode)" : "") + " …"));
                    await SendAsync(serverUrl, token, effectiveSessionId, contentToSend, profile, agent, snapThinking, token2);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    try
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            $"unhandled error in send pipeline: {e.GetType().Name}: {e.Message}"));
                    }
                    catch { /* last-ditch — don't mask the original */ }
                }
                finally
                {
                    _cts = null;
                }
            });
        }

        public static string BuildDefaultPreamble(string unityRoot)
        {
            return
                "[unity-context — auto-injected by the Unity Scene Analyzer plugin on the first message of this session]\n\n" +
                $"You're connected to the Unity Editor running at `{unityRoot ?? "<unknown>"}` via the Model Context Protocol. " +
                "The plugin `com.maicon.scene-analyzer` exposes scene tools prefixed `ext_unity_editor__`:\n" +
                "- **Read / inspect**: `read_hierarchy`, `read_scene`, `read_component`, `get_project_meta`, `screenshot_scene_view`.\n" +
                "- **Analyze**: `analyze_performance`, `validate_scene`.\n" +
                "- **Discover assets & types**: `list_assets`, `search_assets`, `read_asset_metadata`, `find_references_to_asset`, " +
                "`list_component_types` (every Component subclass loaded — built-in + user MonoBehaviours), " +
                "`describe_component_type` (schema of a type without an instance: serialized fields, attributes like Range/Tooltip, RequireComponent list).\n" +
                "- **Create**: `create_primitive`, `create_empty`, `instantiate_prefab`, `create_light`, `create_camera`, " +
                "`create_audio_source`, `create_particle_system`.\n" +
                "- **Modify scene**: `set_transform`, `set_camera_props`, `set_property` (any SerializedProperty), `set_material`, " +
                "`add_component`, `remove_component` (drops a script/collider/etc), `set_enabled` (Behaviour/Renderer/Collider on/off without removing), " +
                "`set_active` (GameObject SetActive), `rename_object`, `set_tag`, `set_layer`, `parent_object`, `delete_object`.\n" +
                "- **Render & post-fx** (URP/HDRP): `set_render_settings`, `create_volume`, `add_post_effect`, " +
                "`set_post_effect_property`, `apply_color_grading`.\n" +
                "- **Batch & selection**: `apply_changeset` (atomic batch — all create/modify ops above as ONE Undo group, " +
                "with `label`+`{ref:…}` chaining for parent references), `select_object`.\n\n" +
                "When asked to add or tweak a component you don't know yet, the typical loop is: " +
                "`list_component_types` → `describe_component_type` → `add_component` → `set_property` per field.\n\n" +
                "Two skills live under `.claude/skills/` of this project. Invoke them **by name** via the skill-runner tool when the user's intent matches:\n" +
                "- `unity-scene-audit` — read-only diagnostic flow (performance + validation). Use when the user asks to audit, diagnose, " +
                "find broken refs, or understand the scene.\n" +
                "- `unity-scene-author` — authoring flow with `apply_changeset` (create/modify/post-fx/render-settings) preserving Unity Undo. " +
                "Use when the user asks to create, modify, decorate, or stylize the scene.\n\n" +
                "Subsequent messages in this session don't need this preamble — the agent should remember. Now respond to the user message that follows.";
        }

        /// Public entry point so the UI's "New" button can create a session
        /// without sending a message. Fire-and-forget; status is reported via
        /// the ChatSession transcript.
        public void CreateSession(string serverUrl, string token, int projectId, string title,
            string provider, string model, string agent)
        {
            if (IsRunning)
            {
                _session.AddSystemNote("a request is already in flight — cancel it first");
                return;
            }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var id = await CreateSessionAsync(serverUrl, token, projectId, title, provider, model, agent, ct);
                    if (id > 0)
                    {
                        // Mark the session as needing the preamble on its first
                        // outgoing message — the New button creates the session
                        // here, but the message hasn't been sent yet, so the
                        // Send path won't see "justCreatedSession" anymore.
                        MarkPreamblePending(id);
                        await MainThreadDispatcher.RunAsync(() =>
                        {
                            Settings.CoderSessionId = id;
                            _session.AddSystemNote(
                                $"session #{id} created (project_id={projectId}) — preamble will be injected on first message");
                        });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"create session pipeline error: {e.GetType().Name}: {e.Message}"));
                }
                finally { _cts = null; }
            });
        }

        /// Fire-and-forget for the UI: ensures a D:\MCP project exists matching
        /// the Unity workdir (creates if needed) and stores its id in Settings.
        public void EnsureProject(string serverUrl, string token, string unityRoot)
        {
            if (IsRunning) { _session.AddSystemNote("a request is already in flight — cancel it first"); return; }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var id = await EnsureProjectAsync(serverUrl, token, unityRoot, ct);
                    if (id > 0)
                    {
                        await MainThreadDispatcher.RunAsync(() =>
                        {
                            Settings.CoderProjectId = id;
                            _session.AddSystemNote($"project #{id} ready (root_path={unityRoot})");
                        });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"ensure project pipeline error: {e.GetType().Name}: {e.Message}"));
                }
                finally { _cts = null; }
            });
        }

        /// Answer an `ask_user` tool call. POSTs the dev's answer to
        /// `/api/coder/sessions/<id>/answer` so the paused run continues.
        public void AnswerQuestion(string serverUrl, string token, int sessionId, string questionId, string answer)
        {
            if (sessionId <= 0) { _session.AddError("session not set — cannot answer"); return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/answer";
                    var body = new JObject { ["answer"] = answer };
                    if (!string.IsNullOrEmpty(questionId)) body["question_id"] = questionId;
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(body.ToString(FormattingNs.None), Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await Http.SendAsync(req);
                    var bodyText = await resp.Content.ReadAsStringAsync();
                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        _session.ClearPendingQuestion();
                        if (resp.IsSuccessStatusCode)
                            _session.AddSystemNote($"answered question with: {answer}");
                        else
                            _session.AddError($"answer failed: HTTP {(int)resp.StatusCode} — {bodyText}");
                    });
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError($"answer error: {e.Message}"));
                }
            });
        }

        /// Approve / reject a plan submitted by the agent via `submit_plan`.
        public void RespondToPlan(string serverUrl, string token, int sessionId, string planId, bool approved, string note)
        {
            if (sessionId <= 0) { _session.AddError("session not set — cannot respond to plan"); return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/plan-response";
                    var body = new JObject
                    {
                        ["approved"] = approved,
                        ["note"] = note ?? "",
                    };
                    if (!string.IsNullOrEmpty(planId)) body["plan_id"] = planId;
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(body.ToString(FormattingNs.None), Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await Http.SendAsync(req);
                    var bodyText = await resp.Content.ReadAsStringAsync();
                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        _session.ClearPendingPlan();
                        if (resp.IsSuccessStatusCode)
                            _session.AddSystemNote($"plan {(approved ? "approved" : "rejected")}{(string.IsNullOrEmpty(note) ? "" : $" — {note}")}");
                        else
                            _session.AddError($"plan response failed: HTTP {(int)resp.StatusCode} — {bodyText}");
                    });
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError($"plan response error: {e.Message}"));
                }
            });
        }

        public void RespondToTasks(string serverUrl, string token, int sessionId, string toolUseId, bool approved)
        {
            if (sessionId <= 0) { _session.AddError("session not set — cannot respond to tasks"); return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/tasks-response";
                    var body = new JObject { ["approved"] = approved };
                    if (!string.IsNullOrEmpty(toolUseId)) body["tool_use_id"] = toolUseId;
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(body.ToString(FormattingNs.None), Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await Http.SendAsync(req);
                    var bodyText = await resp.Content.ReadAsStringAsync();
                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        _session.ClearPendingTasks();
                        if (resp.IsSuccessStatusCode)
                            _session.AddSystemNote($"tasks {(approved ? "approved" : "rejected")}");
                        else
                            _session.AddError($"tasks response failed: HTTP {(int)resp.StatusCode} — {bodyText}");
                    });
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError($"tasks response error: {e.Message}"));
                }
            });
        }

        /// Quick connectivity probe: GETs /api/projects and reports the HTTP
        /// status to the chat transcript. Useful when "I sent a message and
        /// nothing happened" — tells you immediately if the URL/port/token are
        /// pointing at a reachable D:\MCP instance.
        public void TestConnection(string serverUrl, string token)
        {
            if (IsRunning) { _session.AddSystemNote("a request is already in flight — cancel it first"); return; }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"{serverUrl.TrimEnd('/')}/api/projects";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await Http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync();
                    var status = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                    int count = 0;
                    try { count = JArray.Parse(body).Count; } catch { /* not an array */ }
                    await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                        $"connection ok: GET {url} → {status} ({count} projects visible)"));
                }
                catch (HttpRequestException e)
                {
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"connection failed: {e.Message}\nCheck: is the D:\\MCP runtime running and is the URL/port correct?"));
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"test connection error: {e.GetType().Name}: {e.Message}"));
                }
                finally { _cts = null; }
            });
        }

        /// Resolves the D:\MCP `project_id` for the given Unity workdir. Lists
        /// `/api/projects`, returns the id of one whose `root_path` matches the
        /// Unity workdir (case-insensitive, separator-insensitive). Creates a
        /// new one via POST if no match.
        public async Task<int> EnsureProjectAsync(string serverUrl, string token, string unityRoot, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrEmpty(unityRoot))
                {
                    await MainThreadDispatcher.RunAsync(() =>
                        _session.AddError("Unity project root is empty — cannot auto-detect project"));
                    return 0;
                }

                // 1. List
                var listUrl = $"{serverUrl.TrimEnd('/')}/api/projects";
                using (var lreq = new HttpRequestMessage(HttpMethod.Get, listUrl))
                {
                    if (!string.IsNullOrEmpty(token))
                        lreq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var lresp = await Http.SendAsync(lreq, ct);
                    var lbody = await lresp.Content.ReadAsStringAsync();
                    if (!lresp.IsSuccessStatusCode)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            $"list projects failed — HTTP {(int)lresp.StatusCode}: {lbody}"));
                        return 0;
                    }
                    var arr = JArray.Parse(lbody);
                    var target = NormalizePath(unityRoot);
                    foreach (var p in arr)
                    {
                        var rp = (string)p["root_path"];
                        if (string.IsNullOrEmpty(rp)) continue;
                        if (string.Equals(NormalizePath(rp), target, StringComparison.OrdinalIgnoreCase))
                        {
                            var existingId = (int?)p["id"] ?? 0;
                            if (existingId > 0)
                            {
                                await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                                    $"matched existing project #{existingId} for {unityRoot}"));
                                return existingId;
                            }
                        }
                    }
                }

                // 2. Create
                var createUrl = $"{serverUrl.TrimEnd('/')}/api/projects";
                var createBody = new JObject
                {
                    ["root_path"] = unityRoot,
                    ["name"] = SafeName(unityRoot),
                    ["description"] = "Auto-created by Unity Scene Analyzer plugin",
                };
                using (var creq = new HttpRequestMessage(HttpMethod.Post, createUrl)
                {
                    Content = new StringContent(createBody.ToString(FormattingNs.None),
                        Encoding.UTF8, "application/json"),
                })
                {
                    if (!string.IsNullOrEmpty(token))
                        creq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var cresp = await Http.SendAsync(creq, ct);
                    var cbody = await cresp.Content.ReadAsStringAsync();
                    if (!cresp.IsSuccessStatusCode)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            $"create project failed — HTTP {(int)cresp.StatusCode}: {cbody}"));
                        return 0;
                    }
                    var created = JObject.Parse(cbody);
                    var id = (int?)created["id"] ?? 0;
                    if (id > 0)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                            $"created project #{id} '{(string)created["name"]}' for {unityRoot}"));
                    }
                    return id;
                }
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception e)
            {
                Debug.LogException(e);
                await MainThreadDispatcher.RunAsync(() => _session.AddError($"ensure project error: {e.Message}"));
                return 0;
            }
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { p = Path.GetFullPath(p); } catch { /* leave as-is */ }
            return p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }

        private static string SafeName(string root)
        {
            try { return Path.GetFileName(root.TrimEnd('/', '\\')); }
            catch { return root; }
        }

        /// Fire-and-forget: fetches `/api/coder/providers`, fills
        /// Bootstrap.CoderProviders, fires the OnCoderProvidersChanged event so
        /// the window dropdowns refresh.
        public void RefreshProviders(string serverUrl, string token)
        {
            if (IsRunning) { _session.AddSystemNote("a request is already in flight — cancel it first"); return; }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"{serverUrl.TrimEnd('/')}/api/coder/providers";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await Http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            $"refresh providers failed — HTTP {(int)resp.StatusCode}: {body}"));
                        return;
                    }
                    var root = JObject.Parse(body);
                    var arr = root["providers"] as JArray ?? new JArray();
                    var list = new List<CoderProviderInfo>();
                    foreach (var p in arr)
                    {
                        var info = new CoderProviderInfo
                        {
                            Id = (string)p["id"],
                            Name = (string)p["name"],
                            Kind = (string)p["kind"],
                            IsDefault = (bool?)p["is_default"] ?? false,
                            HasKey = (bool?)p["has_key"] ?? false,
                        };
                        var models = p["models"] as JArray;
                        if (models != null)
                        {
                            foreach (var m in models)
                            {
                                info.Models.Add(new CoderModelInfo
                                {
                                    Id = (string)m["id"],
                                    Name = (string)m["name"],
                                    Mode = (string)m["mode"],
                                    ContextWindow = (int?)m["context_window"],
                                    DefaultMaxTokens = (int?)m["default_max_tokens"],
                                });
                            }
                        }
                        list.Add(info);
                    }

                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        Bootstrap.CoderProviders = list;
                        Bootstrap.NotifyCoderProvidersChanged();
                        _session.AddSystemNote(
                            $"providers refreshed: {list.Count} provider(s), " +
                            $"{list.Sum(p => p.Models.Count)} model(s) total");
                    });
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"refresh providers error: {e.GetType().Name}: {e.Message}"));
                }
                finally { _cts = null; }
            });
        }

        public async Task<int> CreateSessionAsync(string serverUrl, string token, int projectId,
            string title, string provider, string model, string agent, CancellationToken ct)
        {
            try
            {
                var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions";
                var body = new JObject
                {
                    ["project_id"] = projectId,
                    ["title"] = string.IsNullOrEmpty(title) ? $"Unity chat — {DateTime.Now:yyyy-MM-dd HH:mm}" : title,
                    ["provider"] = string.IsNullOrEmpty(provider) ? "anthropic" : provider,
                };
                if (!string.IsNullOrEmpty(model)) body["model"] = model;
                if (!string.IsNullOrEmpty(agent)) body["agent"] = agent;

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None),
                        Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var resp = await Http.SendAsync(req, ct);
                var bodyText = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"create session failed — HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}\n{bodyText}"));
                    return 0;
                }

                var json = JObject.Parse(bodyText);
                var id = (int?)json["id"] ?? 0;
                if (id <= 0)
                {
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"create session returned no id: {bodyText}"));
                }
                return id;
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception e)
            {
                Debug.LogException(e);
                await MainThreadDispatcher.RunAsync(() => _session.AddError($"create session error: {e.Message}"));
                return 0;
            }
        }

        private async Task SendAsync(string serverUrl, string token, int sessionId, string content,
            string profile, string agent, bool thinking, CancellationToken ct)
        {
            try
            {
                var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/messages";
                var body = new JObject
                {
                    ["content"] = content,
                    ["profile"] = profile,
                    ["agent"] = agent,
                };
                // THINK MODE — provider-agnostic boolean. The D:\MCP coder maps
                // it server-side to Anthropic budget_tokens, OpenAI
                // reasoning_effort, or Gemini thinkingConfig depending on the
                // active provider.
                if (thinking) body["thinking"] = true;

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"),
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (!string.IsNullOrEmpty(_session.LastEventId))
                    req.Headers.Add("Last-Event-ID", _session.LastEventId);
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                await MainThreadDispatcher.RunAsync(() => _session.StartAssistant());

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}\n{errBody}"));
                    return;
                }

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await ParseSseAsync(reader, ct);
            }
            catch (OperationCanceledException)
            {
                await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote("(cancelled)"));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                await MainThreadDispatcher.RunAsync(() => _session.AddError($"client error: {e.Message}"));
            }
            finally
            {
                _cts = null;
                await MainThreadDispatcher.RunAsync(() => _session.FinishAssistant());
            }
        }

        private async Task ParseSseAsync(StreamReader reader, CancellationToken ct)
        {
            string eventType = null;
            var dataBuilder = new StringBuilder();
            string lastId = null;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                if (line.Length == 0)
                {
                    if (dataBuilder.Length > 0)
                    {
                        var data = dataBuilder.ToString();
                        var type = eventType ?? "message";
                        await MainThreadDispatcher.RunAsync(() => DispatchEvent(type, data, lastId));
                        if (type == "done") break;
                    }
                    eventType = null;
                    dataBuilder.Clear();
                    continue;
                }

                if (line.StartsWith(":")) continue; // SSE comment / heartbeat
                if (line.StartsWith("id:")) lastId = line.Substring(3).TrimStart();
                else if (line.StartsWith("event:")) eventType = line.Substring(6).TrimStart();
                else if (line.StartsWith("data:"))
                {
                    if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                    dataBuilder.Append(line.Substring(5).TrimStart());
                }
            }
        }

        // ---- Safe JToken accessors ----------------------------------------
        // A JObject/JArray cast directly to string throws ArgumentException
        // ("Can not convert Object to String"). The agent's SSE payloads can
        // legitimately carry objects/arrays where we expected strings (e.g.
        // `question.options` may be `[{label, value}, ...]` instead of plain
        // strings). These helpers tolerate any shape and fall back to a safe
        // textual representation.

        private static string AsString(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            if (t is JValue v) return v.Value?.ToString() ?? "";
            return t.ToString(FormattingNs.None);
        }

        private static int? AsInt(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.Float) return (int)(double)t;
            if (t.Type == JTokenType.String && int.TryParse((string)t, out var v)) return v;
            return null;
        }

        private static bool? AsBool(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (t.Type == JTokenType.String && bool.TryParse((string)t, out var v)) return v;
            return null;
        }

        private static double? AsDouble(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (double)t;
            if (t.Type == JTokenType.String && double.TryParse((string)t,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            return null;
        }

        /// Options on a question can be plain strings or objects with
        /// `{label, value}` (or similar). Pick the most human-readable label.
        private static string OptionLabel(JToken t)
        {
            if (t == null) return "";
            if (t.Type == JTokenType.String) return (string)t;
            if (t is JObject o)
            {
                var lbl = AsString(o["label"]) ?? AsString(o["text"]) ?? AsString(o["value"]) ?? AsString(o["id"]);
                if (!string.IsNullOrEmpty(lbl)) return lbl;
            }
            return AsString(t) ?? "";
        }

        private void DispatchEvent(string type, string data, string lastId)
        {
            if (!string.IsNullOrEmpty(lastId)) _session.LastEventId = lastId;

            JToken payload = null;
            try { payload = string.IsNullOrEmpty(data) ? null : JToken.Parse(data); }
            catch { /* keep raw */ }

            try
            {
                DispatchEventInner(type, data, payload);
            }
            catch (Exception e)
            {
                // Don't let a malformed/unexpected event abort the entire SSE
                // stream parser. Log it and surface as a system note so the dev
                // can investigate without losing the rest of the conversation.
                Debug.LogException(e);
                _session.AddSystemNote(
                    $"(skipped malformed `{type}` event: {e.GetType().Name}: {e.Message}; raw: " +
                    (data?.Length > 200 ? data.Substring(0, 200) + "…" : data) + ")");
            }
        }

        private void DispatchEventInner(string type, string data, JToken payload)
        {
            switch (type)
            {
                case "text":
                    _session.AppendAssistantText(AsString(payload?["text"]) ?? data ?? "");
                    break;
                case "thinking":
                    // Thinking is meta — surface as a faint system note rather than the main text.
                    _session.AddSystemNote("(thinking)");
                    break;
                case "tool_call_start":
                    _session.AddToolCall(
                        AsString(payload?["id"]) ?? Guid.NewGuid().ToString("N"),
                        AsString(payload?["name"]) ?? "?",
                        payload?["input"]?.ToString(FormattingNs.None) ?? "");
                    break;
                case "tool_call_result":
                    _session.CompleteToolCall(
                        AsString(payload?["id"]) ?? "",
                        TruncateJson(payload?["result"], 800),
                        AsDouble(payload?["duration_ms"]) ?? 0.0);
                    break;
                case "plan":
                {
                    var p = new PendingPlan
                    {
                        Id = AsString(payload?["id"]),
                        Plan = AsString(payload?["plan"]) ?? data,
                    };
                    _session.SetPendingPlan(p);
                    break;
                }
                case "question":
                {
                    var q = new PendingQuestion
                    {
                        Id = AsString(payload?["id"]),
                        Question = AsString(payload?["question"]) ?? data,
                        AllowFreeform = AsBool(payload?["allowFreeform"]) ?? true,
                    };
                    if (payload?["options"] is JArray optsArr)
                    {
                        foreach (var o in optsArr)
                        {
                            var lbl = OptionLabel(o);
                            if (!string.IsNullOrEmpty(lbl)) q.Options.Add(lbl);
                        }
                    }
                    _session.SetPendingQuestion(q);
                    break;
                }
                case "tasks_posted":
                {
                    var t = new PendingTasks
                    {
                        ToolUseId = AsString(payload?["tool_use_id"]),
                        Summary = AsString(payload?["summary"]) ?? data,
                    };
                    _session.SetPendingTasks(t);
                    break;
                }
                case "session":
                    var sid = AsInt(payload?["id"]);
                    if (sid.HasValue && sid.Value > 0) Settings.CoderSessionId = sid.Value;
                    break;
                case "error":
                    _session.AddError(AsString(payload?["message"]) ?? data);
                    break;
                case "message_complete":
                    _session.FinishAssistant();
                    break;
                case "done":
                    _session.FinishAssistant();
                    break;
                case "ping":
                case "context_status":
                case "index_status":
                case "todos_updated":
                    // Silent — could add subtle indicators later if needed.
                    break;
                default:
                    // Unknown event types: store as system note so the dev can debug.
                    _session.AddSystemNote($"{type}: {data}");
                    break;
            }
        }

        private static string TruncateJson(JToken token, int max)
        {
            if (token == null) return "";
            var s = token.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > max ? s.Substring(0, max) + " …" : s;
        }
    }
}
