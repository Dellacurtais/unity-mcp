using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class HttpMcpServer : IDisposable
    {
        private const string ServerName = "scene-analyzer";
        public static string ServerVersion => SceneAnalyzerVersion.Current;
        private const string ConfirmationArgument = "_confirmationToken";

        private sealed class HttpOutcome
        {
            public int StatusCode = 200;
            public string Body;
        }

        private readonly McpToolRegistry _registry;
        private readonly ConcurrentDictionary<HttpListenerContext, byte> _activeContexts =
            new ConcurrentDictionary<HttpListenerContext, byte>();
        private readonly ConfirmationTokenService _confirmations = new ConfirmationTokenService();

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private McpRequestCoordinator _coordinator;
        private string _token;
        private int _port;
        // Snapshots of Settings captured in Start() (main thread) — HandleAsync
        // runs on HttpListener thread-pool threads where EditorPrefs.GetInt
        // throws UnityException, so it must never read Settings directly.
        private int _maxRequestBodyBytes;
        private int _toolTimeoutSeconds;
        private static int _callSeq;

        public bool IsRunning => _listener != null && _listener.IsListening;
        public int Port => _port;
        public int InFlightCount => _coordinator != null ? _coordinator.InFlightCount : 0;
        public event Action<string> OnLog;

        public HttpMcpServer(McpToolRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public void Start(int port, string token)
        {
            Stop();
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Bearer token is required.");
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _port = port;
            _token = token;
            var maxConcurrentRequests = Settings.MaxConcurrentRequests;
            _maxRequestBodyBytes = Settings.MaxRequestBodyBytes;
            _toolTimeoutSeconds = Settings.ToolTimeoutSeconds;
            _coordinator = new McpRequestCoordinator(maxConcurrentRequests);
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
            _listener.Start();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
            Log($"listening on http://127.0.0.1:{port}/mcp/ (MCP {McpProtocolNegotiator.Preferred}, tools={_registry.Count})");
        }

        public void Stop() => Stop(null);

        public void Stop(string reason)
        {
            if (_listener == null && _coordinator == null) return;
            try { _cts?.Cancel(); } catch { }
            try { _coordinator?.CancelAll(); } catch { }
            MainThreadDispatcher.CancelPending();

            foreach (var context in _activeContexts.Keys)
            {
                try
                {
                    var response = context.Response;
                    response.StatusCode = 503;
                    response.AddHeader("Connection", "close");
                    if (!string.IsNullOrEmpty(reason))
                    {
                        var bytes = Encoding.UTF8.GetBytes(reason);
                        response.ContentType = "text/plain; charset=utf-8";
                        response.ContentLength64 = bytes.Length;
                        response.OutputStream.Write(bytes, 0, bytes.Length);
                    }
                    response.Close();
                }
                catch { }
            }
            _activeContexts.Clear();

            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            try { _coordinator?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _listener = null;
            _coordinator = null;
            _cts = null;
            Log(string.IsNullOrEmpty(reason) ? "stopped" : $"stopped ({reason})");
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { break; }
                _ = HandleAsync(context, cancellationToken);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken serverToken)
        {
            _activeContexts.TryAdd(context, 0);
            try
            {
                var request = context.Request;
                var response = context.Response;
                if (!IsAllowedOrigin(request))
                {
                    await WriteHttpError(response, 403, "Origin is not an allowed loopback origin.", serverToken);
                    return;
                }
                AddCors(request, response);

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }
                if (!IsAuthorized(request))
                {
                    response.StatusCode = 401;
                    response.AddHeader("WWW-Authenticate", "Bearer realm=\"mcp\"");
                    response.Close();
                    return;
                }
                if (request.HttpMethod != "POST")
                {
                    response.StatusCode = 405;
                    response.AddHeader("Allow", "POST, OPTIONS");
                    response.Close();
                    return;
                }
                if (!IsJsonContentType(request.ContentType))
                {
                    await WriteHttpError(response, 415, "Content-Type must be application/json.", serverToken);
                    return;
                }
                if (!AcceptsJson(request.Headers["Accept"]))
                {
                    await WriteHttpError(response, 406, "Accept must include application/json or */*.", serverToken);
                    return;
                }

                var protocolHeader = request.Headers["MCP-Protocol-Version"];
                if (!string.IsNullOrEmpty(protocolHeader) && !McpProtocolNegotiator.IsSupported(protocolHeader))
                {
                    await WriteHttpError(response, 400, $"Unsupported MCP-Protocol-Version '{protocolHeader}'.", serverToken);
                    return;
                }
                if (request.ContentLength64 > _maxRequestBodyBytes)
                {
                    await WriteHttpError(response, 413, "MCP request body exceeds the configured limit.", serverToken);
                    return;
                }

                string body;
                try { body = await ReadBodyBoundedAsync(request, _maxRequestBodyBytes, serverToken); }
                catch (InvalidDataException e)
                {
                    await WriteHttpError(response, 413, e.Message, serverToken);
                    return;
                }

                var negotiated = string.IsNullOrEmpty(protocolHeader)
                    ? McpProtocolNegotiator.V2025March
                    : protocolHeader;
                var outcome = await ProcessAsync(body, negotiated, request.Headers["Origin"], serverToken);
                response.StatusCode = outcome.StatusCode;
                if (string.IsNullOrEmpty(outcome.Body))
                {
                    response.ContentLength64 = 0;
                    response.Close();
                    return;
                }

                var responseBytes = Encoding.UTF8.GetBytes(outcome.Body);
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = responseBytes.Length;
                await response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length, serverToken);
                response.Close();
            }
            catch (OperationCanceledException)
            {
                try { context.Response.StatusCode = 499; context.Response.Close(); } catch { }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
            finally
            {
                _activeContexts.TryRemove(context, out _);
            }
        }

        private async Task<HttpOutcome> ProcessAsync(
            string body, string protocolVersion, string origin, CancellationToken serverToken)
        {
            JToken request;
            try { request = JToken.Parse(body); }
            catch { return JsonOutcome(ErrorResp(null, -32700, "Parse error")); }

            if (request is JArray batch)
            {
                if (batch.Count == 0) return JsonOutcome(ErrorResp(null, -32600, "Invalid Request: empty batch"));
                var results = new JArray();
                foreach (var item in batch)
                {
                    var response = await ProcessOne(item, protocolVersion, origin, serverToken);
                    if (response != null) results.Add(response);
                }
                return results.Count == 0
                    ? new HttpOutcome { StatusCode = 202 }
                    : JsonOutcome(results.ToString(Formatting.None));
            }

            var single = await ProcessOne(request, protocolVersion, origin, serverToken);
            return single == null
                ? new HttpOutcome { StatusCode = 202 }
                : JsonOutcome(single.ToString(Formatting.None));
        }

        private async Task<JObject> ProcessOne(
            JToken token, string protocolVersion, string origin, CancellationToken serverToken)
        {
            if (!(token is JObject request) || (string)request["jsonrpc"] != "2.0")
                return ParseObject(ErrorResp(null, -32600, "Invalid Request"));

            var id = request["id"];
            var method = (string)request["method"];
            var parameters = request["params"] as JObject ?? new JObject();
            var notification = id == null;
            if (string.IsNullOrEmpty(method)) return ParseObject(ErrorResp(id, -32600, "Invalid Request: method is required"));

            try
            {
                switch (method)
                {
                    case "initialize":
                    {
                        var requested = (string)parameters["protocolVersion"];
                        var selected = McpProtocolNegotiator.Negotiate(requested);
                        return ParseObject(OkResp(id, new JObject
                        {
                            ["protocolVersion"] = selected,
                            ["capabilities"] = new JObject
                            {
                                ["tools"] = new JObject { ["listChanged"] = false },
                            },
                            ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = ServerVersion },
                            ["instructions"] = "High-risk tools return CONFIRMATION_REQUIRED with a single-use token; retry the same arguments with _confirmationToken.",
                        }));
                    }
                    case "notifications/initialized":
                        return notification ? null : ParseObject(OkResp(id, new JObject()));
                    case "notifications/cancelled":
                    {
                        var requestId = parameters["requestId"]?.ToString();
                        _coordinator?.Cancel(requestId);
                        return notification ? null : ParseObject(OkResp(id, new JObject { ["cancelled"] = requestId }));
                    }
                    case "ping":
                        return notification ? null : ParseObject(OkResp(id, new JObject()));
                    case "tools/list":
                        return notification
                            ? null
                            : ParseObject(OkResp(id, await MainThreadDispatcher.RunAsync(ListTools, serverToken)));
                    case "tools/call":
                        if (notification)
                        {
                            _ = CallToolAsync(id, parameters, protocolVersion, origin, serverToken);
                            return null;
                        }
                        return ParseObject(await CallToolAsync(id, parameters, protocolVersion, origin, serverToken));
                    default:
                        return notification ? null : ParseObject(ErrorResp(id, -32601, $"Method not found: {method}"));
                }
            }
            catch (OperationCanceledException)
            {
                return notification ? null : ParseObject(OkResp(id, ToolErrorBody("CANCELLED", "The MCP request was cancelled.")));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return notification ? null : ParseObject(ErrorResp(id, -32603, e.Message));
            }
        }

        private JObject ListTools()
        {
            var tools = new JArray();
            foreach (var tool in _registry.All)
            {
                var descriptor = _registry.GetDescriptor(tool.Name) ?? ToolDescriptor.Infer(tool);
                var inputSchema = BuildAdvertisedInputSchema(tool, descriptor);
                if (ToolAuthorization.RequiresConfirmation(descriptor))
                {
                    var properties = inputSchema["properties"] as JObject;
                    if (properties == null) inputSchema["properties"] = properties = new JObject();
                    properties[ConfirmationArgument] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Single-use token returned by a prior CONFIRMATION_REQUIRED response for these exact arguments.",
                    };
                }

                var item = new JObject
                {
                    ["name"] = tool.Name,
                    ["title"] = tool.Name.Replace('_', ' '),
                    ["description"] = tool.Description,
                    ["inputSchema"] = inputSchema,
                    ["annotations"] = new JObject
                    {
                        ["readOnlyHint"] = !descriptor.IsWrite,
                        ["destructiveHint"] = (descriptor.Capabilities & McpCapability.Destructive) != 0,
                        ["idempotentHint"] = descriptor.IsIdempotent,
                        ["openWorldHint"] = (descriptor.Capabilities & (McpCapability.PackageManager | McpCapability.Build)) != 0,
                    },
                    ["_meta"] = new JObject
                    {
                        ["category"] = descriptor.Category,
                        ["capabilities"] = descriptor.Capabilities.ToString(),
                        ["risk"] = descriptor.Risk.ToString(),
                        ["confirmationRequired"] = ToolAuthorization.RequiresConfirmation(descriptor),
                        ["editorMode"] = descriptor.EditorMode.ToString(),
                    },
                };
                if (tool.OutputSchema != null) item["outputSchema"] = tool.OutputSchema;
                tools.Add(item);
            }
            return new JObject
            {
                ["tools"] = tools,
                ["_meta"] = new JObject
                {
                    ["registryCount"] = _registry.Count,
                    ["registryHash"] = _registry.VersionHash,
                },
            };
        }

        private static JObject BuildAdvertisedInputSchema(IMcpTool tool, ToolDescriptor descriptor)
        {
            var schema = (JObject)(tool.InputSchema ?? new JObject { ["type"] = "object" }).DeepClone();
            var properties = schema["properties"] as JObject;
            if (properties == null || properties["instanceId"] == null) return schema;

            properties["target"] = SchemaHelpers.TargetRefSchema;
            if (schema["required"] is JArray required
                && required.Any(token => string.Equals((string)token, "instanceId", StringComparison.Ordinal)))
            {
                for (int i = required.Count - 1; i >= 0; i--)
                    if (string.Equals((string)required[i], "instanceId", StringComparison.Ordinal)) required.RemoveAt(i);
                if (required.Count == 0) schema.Remove("required");
                schema["anyOf"] = new JArray
                {
                    new JObject { ["required"] = new JArray { "instanceId" } },
                    new JObject { ["required"] = new JArray { "target" } },
                };
            }
            return schema;
        }

        private async Task<string> CallToolAsync(
            JToken id, JObject parameters, string protocolVersion, string origin, CancellationToken serverToken)
        {
            var name = (string)parameters["name"];
            if (string.IsNullOrEmpty(name)) return ErrorResp(id, -32602, "Tool name is required.");
            if (!_registry.TryGet(name, out var tool)) return ErrorResp(id, -32602, $"Tool not found: {name}");

            var arguments = parameters["arguments"] as JObject ?? new JObject();
            var executionArguments = (JObject)arguments.DeepClone();
            var confirmationToken = (string)executionArguments[ConfirmationArgument];
            executionArguments.Remove(ConfirmationArgument);

            ToolDescriptor descriptor = null;
            var requiresConfirmation = false;
            string preparationCode = null;
            string preparationMessage = null;
            var prepared = await MainThreadDispatcher.RunAsync(() =>
            {
                var inputSchema = tool.InputSchema ?? new JObject { ["type"] = "object" };
                if (executionArguments["target"] != null && inputSchema["properties"]?["instanceId"] != null)
                {
                    var target = ToolTargetResolver.Resolve(
                        ToolTargetRef.FromJson(executionArguments["target"]), out var targetError);
                    if (target == null)
                    {
                        preparationCode = "TARGET_NOT_FOUND";
                        preparationMessage = targetError;
                        return false;
                    }
                    var gameObject = target as GameObject ?? (target as Component)?.gameObject;
                    executionArguments["instanceId"] = gameObject != null
                        ? gameObject.GetInstanceID()
                        : target.GetInstanceID();
                    executionArguments.Remove("target");
                }
                if (!ToolSchemaValidator.ValidateArguments(executionArguments, inputSchema, out var argumentError))
                {
                    preparationCode = "BAD_ARGS";
                    preparationMessage = argumentError;
                    return false;
                }

                descriptor = _registry.GetDescriptor(name) ?? ToolDescriptor.Infer(tool);
                if (!ToolAuthorization.TryAuthorize(
                    tool, descriptor, out preparationCode, out preparationMessage)) return false;
                requiresConfirmation = ToolAuthorization.RequiresConfirmation(descriptor);
                return true;
            }, serverToken);
            if (!prepared) return OkResp(id, ToolErrorBody(preparationCode, preparationMessage));

            if (requiresConfirmation)
            {
                var projectKey = Bootstrap.UnityProjectRoot ?? string.Empty;
                if (!_confirmations.TryConsume(confirmationToken, name, executionArguments, projectKey))
                {
                    var issued = _confirmations.Issue(name, executionArguments, projectKey, TimeSpan.FromMinutes(2));
                    return OkResp(id, ToolErrorBody("CONFIRMATION_REQUIRED",
                        "Review the requested operation, then retry the same arguments with _confirmationToken.",
                        new JObject
                        {
                            ["confirmationToken"] = issued,
                            ["expiresInSeconds"] = 120,
                            ["tool"] = name,
                            ["risk"] = descriptor.Risk.ToString(),
                            ["capabilities"] = descriptor.Capabilities.ToString(),
                            ["arguments"] = executionArguments.DeepClone(),
                        }));
                }
            }

            var requestId = id != null ? id.ToString(Formatting.None) : Guid.NewGuid().ToString("N");
            var timeout = TimeSpan.FromSeconds(_toolTimeoutSeconds);
            using (var lease = await _coordinator.BeginAsync(requestId, descriptor.IsWrite, serverToken, timeout))
            using (ToolExecutionContext.Push(new ToolExecutionContext
            {
                ToolName = name,
                RequestId = requestId,
                ProtocolVersion = protocolVersion,
                Origin = origin,
                RequestPath = "/mcp/",
                CancellationToken = lease.CancellationToken,
                DeadlineUtc = DateTimeOffset.UtcNow.Add(timeout),
                IsNotification = id == null,
                RequestMeta = parameters["_meta"] as JObject,
            }))
            {
                ToolResult result;
                var preMutationScenes = (descriptor.Capabilities & McpCapability.SceneWrite) != 0
                    ? await MainThreadDispatcher.RunAsync(
                        () => MutationScope.CaptureLegacySceneHandles(executionArguments), lease.CancellationToken)
                    : null;
                try
                {
                    result = await tool.ExecuteAsync(executionArguments);
                    if (result == null) result = ToolResult.Error("NULL_RESULT", $"Tool '{name}' returned no result.");
                    if (!result.IsError && result.Json != null)
                        await MainThreadDispatcher.RunAsync(
                            () => EnrichStableTargets(result.Json, 0), lease.CancellationToken);
                    if (descriptor.IsWrite && !result.IsError)
                    {
                        var resultInstanceId = result.Json is JObject resultObject
                            ? (int?)resultObject["instanceId"] ?? 0
                            : 0;
                        var mutationMeta = await MainThreadDispatcher.RunAsync(
                            () => MutationScope.FinalizeLegacyMutation(
                                executionArguments, resultInstanceId, preMutationScenes),
                            lease.CancellationToken);
                        if (result.Metadata == null) result.Metadata = new JObject();
                        result.Metadata["mutation"] = mutationMeta;
                        await MainThreadDispatcher.RunAsync(Bootstrap.NotifyWriteCompleted, lease.CancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    result = ToolResult.Error("CANCELLED", $"Tool '{name}' was cancelled or exceeded its deadline.");
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    result = ToolResult.Error("INTERNAL_ERROR", e.Message,
                        new JObject { ["exceptionType"] = e.GetType().FullName });
                }

                return OkResp(id, BuildToolResult(result, name));
            }
        }

        private static void EnrichStableTargets(JToken token, int depth)
        {
            if (token == null || depth > 6) return;
            if (token is JObject obj)
            {
                if (obj["target"] == null && obj["instanceId"]?.Type == JTokenType.Integer)
                {
                    var unityObject = UnityEditor.EditorUtility.InstanceIDToObject((int)obj["instanceId"]);
                    var target = ToolTargetRef.Capture(unityObject);
                    if (target != null) obj["target"] = target.ToJson();
                }
                foreach (var property in obj.Properties())
                    if (property.Name != "target") EnrichStableTargets(property.Value, depth + 1);
            }
            else if (token is JArray array)
            {
                var count = Math.Min(array.Count, 1000);
                for (int i = 0; i < count; i++) EnrichStableTargets(array[i], depth + 1);
            }
        }

        private JObject BuildToolResult(ToolResult result, string toolName)
        {
            JArray content;
            if (result.ContentBlocks != null && result.ContentBlocks.Count > 0)
                content = (JArray)result.ContentBlocks.DeepClone();
            else
            {
                content = new JArray();
                if (!string.IsNullOrEmpty(result.Text))
                    content.Add(new JObject { ["type"] = "text", ["text"] = result.Text });
                if (result.Json != null)
                    content.Add(new JObject { ["type"] = "text", ["text"] = result.Json.ToString(Formatting.None) });
                if (content.Count == 0) content.Add(new JObject { ["type"] = "text", ["text"] = string.Empty });
            }

            var callSequence = Interlocked.Increment(ref _callSeq);
            var meta = result.Metadata != null ? (JObject)result.Metadata.DeepClone() : new JObject();
            meta["unityCallSequence"] = callSequence;
            meta["tool"] = toolName;
            meta["registryHash"] = _registry.VersionHash;
            var payload = new JObject
            {
                ["content"] = content,
                ["isError"] = result.IsError,
                ["_meta"] = meta,
            };
            if (result.Json != null) payload["structuredContent"] = result.Json.DeepClone();
            if (result.Warnings != null && result.Warnings.Count > 0) payload["warnings"] = result.Warnings.DeepClone();
            return payload;
        }

        private static JObject ToolErrorBody(string code, string message, JToken data = null)
        {
            var structured = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) structured["data"] = data;
            return new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = $"[{code}] {message}" } },
                ["structuredContent"] = structured,
                ["isError"] = true,
            };
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            var authorization = request.Headers["Authorization"];
            const string prefix = "Bearer ";
            if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return FixedTimeEquals(authorization.Substring(prefix.Length).Trim(), _token);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var aBytes = Encoding.UTF8.GetBytes(a);
            var bBytes = Encoding.UTF8.GetBytes(b);
            int diff = aBytes.Length ^ bBytes.Length;
            int max = Math.Max(aBytes.Length, bBytes.Length);
            for (int i = 0; i < max; i++)
            {
                byte av = i < aBytes.Length ? aBytes[i] : (byte)0;
                byte bv = i < bBytes.Length ? bBytes[i] : (byte)0;
                diff |= av ^ bv;
            }
            return diff == 0;
        }

        private static bool IsAllowedOrigin(HttpListenerRequest request)
        {
            var origin = request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin)) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            return uri.IsLoopback && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static void AddCors(HttpListenerRequest request, HttpListenerResponse response)
        {
            var origin = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                response.AddHeader("Access-Control-Allow-Origin", origin);
                response.AddHeader("Vary", "Origin");
            }
            response.AddHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, Accept, MCP-Protocol-Version, Mcp-Session-Id");
            response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
        }

        private static bool IsJsonContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;
            return contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AcceptsJson(string accept)
        {
            return string.IsNullOrEmpty(accept) || accept.Contains("application/json") || accept.Contains("*/*");
        }

        private static async Task<string> ReadBodyBoundedAsync(
            HttpListenerRequest request, int maxBytes, CancellationToken cancellationToken)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read <= 0) break;
                    if (memory.Length + read > maxBytes) throw new InvalidDataException("MCP request body exceeds the configured limit.");
                    memory.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }

        private static async Task WriteHttpError(
            HttpListenerResponse response, int statusCode, string message, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(new JObject { ["error"] = message }.ToString(Formatting.None));
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            response.Close();
        }

        private static HttpOutcome JsonOutcome(string body) => new HttpOutcome { StatusCode = 200, Body = body };
        private static JObject ParseObject(string json) => JObject.Parse(json);

        private static string OkResp(JToken id, JToken result)
        {
            var response = new JObject { ["jsonrpc"] = "2.0", ["result"] = result };
            if (id != null) response["id"] = id.DeepClone();
            return response.ToString(Formatting.None);
        }

        private static string ErrorResp(JToken id, int code, string message)
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };
            if (id != null) response["id"] = id.DeepClone();
            return response.ToString(Formatting.None);
        }

        private void Log(string message)
        {
            try { OnLog?.Invoke(message); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
