using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    /// MCP streamable_http transport implemented over System.Net.HttpListener.
    /// Binds to 127.0.0.1 only; bearer-token auth on every request. The accept
    /// loop runs on a background thread; tool execution is marshalled to the
    /// editor main thread via MainThreadDispatcher.
    public sealed class HttpMcpServer : IDisposable
    {
        private const string ProtocolVersion = "2024-11-05";
        private const string ServerName = "scene-analyzer";
        private const string ServerVersion = "0.1.0";

        private readonly McpToolRegistry _registry;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private string _token;
        private int _port;

        public bool IsRunning => _listener != null && _listener.IsListening;
        public int Port => _port;
        public event Action<string> OnLog;

        public HttpMcpServer(McpToolRegistry registry) { _registry = registry; }

        public void Start(int port, string token)
        {
            Stop();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Bearer token is required.");
            _port = port;
            _token = token;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
            Log($"listening on http://127.0.0.1:{port}/mcp/");
        }

        public void Stop()
        {
            if (_listener == null) return;
            try { _cts?.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _listener = null;
            Log("stopped");
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = HandleAsync(ctx, ct);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    AddCors(ctx.Response);
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                    return;
                }

                if (!IsAuthorized(ctx.Request))
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.AddHeader("WWW-Authenticate", "Bearer realm=\"mcp\"");
                    AddCors(ctx.Response);
                    ctx.Response.Close();
                    return;
                }

                if (ctx.Request.HttpMethod != "POST")
                {
                    ctx.Response.StatusCode = 405;
                    AddCors(ctx.Response);
                    ctx.Response.Close();
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                var responseBody = await ProcessAsync(body);
                var bytes = Encoding.UTF8.GetBytes(responseBody);

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                AddCors(ctx.Response);
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
                ctx.Response.Close();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        private bool IsAuthorized(HttpListenerRequest req)
        {
            var auth = req.Headers["Authorization"];
            if (string.IsNullOrEmpty(auth)) return false;
            const string prefix = "Bearer ";
            if (!auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var token = auth.Substring(prefix.Length).Trim();
            return string.Equals(token, _token, StringComparison.Ordinal);
        }

        private static void AddCors(HttpListenerResponse resp)
        {
            resp.AddHeader("Access-Control-Allow-Origin", "*");
            resp.AddHeader("Access-Control-Allow-Headers", "Authorization, Content-Type");
            resp.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
        }

        private async Task<string> ProcessAsync(string body)
        {
            JToken request;
            try { request = JToken.Parse(body); }
            catch { return ErrorResp(null, -32700, "Parse error"); }

            if (request.Type == JTokenType.Array)
            {
                var results = new JArray();
                foreach (var item in (JArray)request)
                {
                    var s = await ProcessOne(item);
                    if (string.IsNullOrEmpty(s)) continue; // notification — no response per JSON-RPC 2.0 spec
                    results.Add(JToken.Parse(s));
                }
                return results.Count == 0 ? "" : results.ToString(Formatting.None);
            }
            return await ProcessOne(request);
        }

        private async Task<string> ProcessOne(JToken t)
        {
            var id = t["id"];
            var method = (string)t["method"];
            var prms = t["params"] as JObject ?? new JObject();

            // Notifications (id absent) — no response body required, but we
            // return "{}" so batched responses keep their shape.
            try
            {
                switch (method)
                {
                    case "initialize":
                        return OkResp(id, new JObject
                        {
                            ["protocolVersion"] = ProtocolVersion,
                            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = ServerVersion },
                        });
                    case "notifications/initialized":
                        // Spec-compliant: notifications carry no id and expect
                        // no response body. Returning empty makes the HTTP
                        // listener still flush a 200 with no JSON-RPC envelope.
                        return id == null ? "" : OkResp(id, new JObject());
                    case "ping":
                        return OkResp(id, new JObject());
                    case "tools/list":
                        return OkResp(id, ListTools());
                    case "tools/call":
                        return await CallToolAsync(id, prms);
                    default:
                        return ErrorResp(id, -32601, $"Method not found: {method}");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return ErrorResp(id, -32603, e.Message);
            }
        }

        private JObject ListTools()
        {
            var arr = new JArray();
            foreach (var tool in _registry.All)
            {
                arr.Add(new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = tool.InputSchema ?? new JObject { ["type"] = "object" },
                });
            }
            return new JObject { ["tools"] = arr };
        }

        private async Task<string> CallToolAsync(JToken id, JObject prms)
        {
            var name = (string)prms["name"];
            var args = prms["arguments"] as JObject ?? new JObject();

            if (!_registry.TryGet(name, out var tool))
                return ErrorResp(id, -32602, $"Tool not found: {name}");

            ToolResult result;
            try
            {
                // Run BOTH the AllowWrites guard and the actual tool on the
                // editor main thread. EditorPrefs (used by Settings.*) and any
                // UnityEditor / UnityEngine API will throw from the HTTP
                // listener thread pool ("can only be called from the main
                // thread"). Dispatching the whole block keeps the tool free to
                // hit the editor APIs without surprise crashes.
                result = await MainThreadDispatcher.RunAsync(() =>
                {
                    if (tool.IsWrite && !Settings.AllowWrites)
                        return ToolResult.Error("WRITE_DISABLED",
                            "Writes are disabled in the Scene Analyzer panel.");
                    return tool.Execute(args);
                });
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return OkResp(id, ToolErrorBody("INTERNAL_ERROR", e.Message));
            }

            JArray content;
            if (result.ContentBlocks != null && result.ContentBlocks.Count > 0)
            {
                content = result.ContentBlocks;
            }
            else
            {
                content = new JArray();
                if (!string.IsNullOrEmpty(result.Text))
                    content.Add(new JObject { ["type"] = "text", ["text"] = result.Text });
                if (result.Json != null)
                    content.Add(new JObject { ["type"] = "text", ["text"] = result.Json.ToString(Formatting.None) });
                if (content.Count == 0)
                    content.Add(new JObject { ["type"] = "text", ["text"] = "" });
            }

            return OkResp(id, new JObject
            {
                ["content"] = content,
                ["isError"] = result.IsError,
            });
        }

        private static JObject ToolErrorBody(string code, string message)
        {
            var content = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = $"[{code}] {message}" }
            };
            return new JObject { ["content"] = content, ["isError"] = true };
        }

        private static string OkResp(JToken id, JToken result)
        {
            var o = new JObject { ["jsonrpc"] = "2.0", ["result"] = result };
            if (id != null) o["id"] = id;
            return o.ToString(Formatting.None);
        }

        private static string ErrorResp(JToken id, int code, string message)
        {
            var o = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };
            if (id != null) o["id"] = id;
            return o.ToString(Formatting.None);
        }

        private void Log(string msg)
        {
            try { OnLog?.Invoke(msg); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
