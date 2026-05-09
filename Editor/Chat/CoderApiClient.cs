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
            _ = Task.Run(() => SendAsync(serverUrl, token, sessionId, content, profile, agent, token2));
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
