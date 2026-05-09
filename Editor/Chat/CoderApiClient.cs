using System;
using System.IO;
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
            var unityRoot = Bootstrap.UnityProjectRoot;
            _ = Task.Run(async () =>
            {
                int effectiveSessionId = sessionId;
                if (effectiveSessionId <= 0)
                {
                    // Auto-ensure project exists for this Unity workdir.
                    int projectId = Settings.CoderProjectId;
                    if (projectId <= 0)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                            "auto-detecting D:\\MCP project for this Unity workdir…"));
                        projectId = await EnsureProjectAsync(serverUrl, token, unityRoot, token2);
                        if (projectId <= 0) { _cts = null; return; }
                        await MainThreadDispatcher.RunAsync(() => Settings.CoderProjectId = projectId);
                    }

                    // Auto-create session. The dashboard does the same dance.
                    await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                        "auto-creating session (sessionId was 0)…"));
                    effectiveSessionId = await CreateSessionAsync(
                        serverUrl, token,
                        projectId,
                        $"Unity chat — {DateTime.Now:HH:mm:ss}",
                        Settings.CoderProvider,
                        Settings.CoderModel,
                        agent,
                        token2);
                    if (effectiveSessionId <= 0)
                    {
                        _cts = null;
                        return; // CreateSessionAsync already logged the error
                    }
                    await MainThreadDispatcher.RunAsync(() => Settings.CoderSessionId = effectiveSessionId);
                }
                await SendAsync(serverUrl, token, effectiveSessionId, content, profile, agent, token2);
            });
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
                var id = await CreateSessionAsync(serverUrl, token, projectId, title, provider, model, agent, ct);
                if (id > 0)
                {
                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        Settings.CoderSessionId = id;
                        _session.AddSystemNote($"session #{id} created (project_id={projectId})");
                    });
                }
                _cts = null;
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
                var id = await EnsureProjectAsync(serverUrl, token, unityRoot, ct);
                if (id > 0)
                {
                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        Settings.CoderProjectId = id;
                        _session.AddSystemNote($"project #{id} ready (root_path={unityRoot})");
                    });
                }
                _cts = null;
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
            string profile, string agent, CancellationToken ct)
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

        private void DispatchEvent(string type, string data, string lastId)
        {
            if (!string.IsNullOrEmpty(lastId)) _session.LastEventId = lastId;

            JToken payload = null;
            try { payload = string.IsNullOrEmpty(data) ? null : JToken.Parse(data); }
            catch { /* keep raw */ }

            switch (type)
            {
                case "text":
                    _session.AppendAssistantText(payload != null ? (string)payload["text"] ?? "" : data);
                    break;
                case "thinking":
                    // Thinking is meta — surface as a faint system note rather than the main text.
                    _session.AddSystemNote("(thinking)");
                    break;
                case "tool_call_start":
                    _session.AddToolCall(
                        (string)payload?["id"] ?? Guid.NewGuid().ToString("N"),
                        (string)payload?["name"] ?? "?",
                        payload?["input"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "");
                    break;
                case "tool_call_result":
                    _session.CompleteToolCall(
                        (string)payload?["id"] ?? "",
                        TruncateJson(payload?["result"], 800),
                        (double?)payload?["duration_ms"] ?? 0.0);
                    break;
                case "plan":
                    _session.AddSystemNote($"PLAN:\n{(string)payload?["plan"] ?? data}");
                    break;
                case "question":
                    _session.AddSystemNote($"QUESTION: {(string)payload?["question"] ?? data}");
                    break;
                case "tasks_posted":
                    _session.AddSystemNote($"TASKS: {payload?["summary"] ?? data}");
                    break;
                case "session":
                    var sid = (int?)payload?["id"];
                    if (sid.HasValue && sid.Value > 0) Settings.CoderSessionId = sid.Value;
                    break;
                case "error":
                    _session.AddError((string)payload?["message"] ?? data);
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
