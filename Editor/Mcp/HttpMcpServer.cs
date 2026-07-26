using System;
using System.Collections.Concurrent;
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
    public sealed class HttpMcpServer : IDisposable
    {
        public static string ServerVersion => SceneAnalyzerVersion.Current;

        private readonly McpToolRegistry _registry;
        private readonly ConcurrentDictionary<HttpListenerContext, byte> _activeContexts =
            new ConcurrentDictionary<HttpListenerContext, byte>();

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private McpRequestProcessor _processor;
        private string _token;
        private int _port;
        private int _maxRequestBodyBytes;

        public bool IsRunning => _listener != null && _listener.IsListening;
        public int Port => _port;
        public int InFlightCount => _processor != null ? _processor.InFlightCount : 0;
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

            McpRequestProcessor processor = null;
            CancellationTokenSource cts = null;
            HttpListener listener = null;
            var maxRequestBodyBytes = Settings.MaxRequestBodyBytes;
            try
            {
                processor = new McpRequestProcessor(
                    _registry,
                    Settings.MaxConcurrentRequests,
                    Settings.ToolTimeoutSeconds);
                cts = new CancellationTokenSource();
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
                listener.Start();

                _port = port;
                _token = token;
                _maxRequestBodyBytes = maxRequestBodyBytes;
                _processor = processor;
                _cts = cts;
                _listener = listener;
                _ = Task.Run(() => AcceptLoop(
                    listener, processor, token, maxRequestBodyBytes, cts.Token));
                Log($"listening on http://127.0.0.1:{port}/mcp/ (MCP {McpProtocolNegotiator.Preferred}, tools={_registry.Count})");
            }
            catch
            {
                if (ReferenceEquals(_listener, listener)) _listener = null;
                if (ReferenceEquals(_processor, processor)) _processor = null;
                if (ReferenceEquals(_cts, cts)) _cts = null;
                _port = 0;
                _token = null;
                _maxRequestBodyBytes = 0;
                try { listener?.Close(); } catch { }
                try { processor?.Dispose(); } catch { }
                try { cts?.Dispose(); } catch { }
                throw;
            }
        }

        public void Stop() => Stop(null);

        public void Stop(string reason)
        {
            var listener = _listener;
            var processor = _processor;
            var cts = _cts;
            if (listener == null && processor == null) return;
            _listener = null;
            _processor = null;
            _cts = null;
            _token = null;
            _maxRequestBodyBytes = 0;

            try { cts?.Cancel(); } catch { }
            try { processor?.CancelAll(); } catch { }
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

            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
            try { processor?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }
            Log(string.IsNullOrEmpty(reason) ? "stopped" : $"stopped ({reason})");
        }

        public void Dispose() => Stop();

        internal static bool RequiresLegacyMutationFinalization(ToolDescriptor descriptor) =>
            McpRequestProcessor.RequiresLegacyMutationFinalization(descriptor);

        private async Task AcceptLoop(
            HttpListener listener,
            McpRequestProcessor processor,
            string token,
            int maxRequestBodyBytes,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { break; }
                _ = HandleAsync(
                    context, processor, token, maxRequestBodyBytes, cancellationToken);
            }
        }

        private async Task HandleAsync(
            HttpListenerContext context,
            McpRequestProcessor processor,
            string token,
            int maxRequestBodyBytes,
            CancellationToken serverToken)
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
                if (!IsAuthorized(request, token))
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
                if (request.ContentLength64 > maxRequestBodyBytes)
                {
                    await WriteHttpError(response, 413, "MCP request body exceeds the configured limit.", serverToken);
                    return;
                }

                string body;
                try { body = await ReadBodyBoundedAsync(request, maxRequestBodyBytes, serverToken); }
                catch (InvalidDataException exception)
                {
                    await WriteHttpError(response, 413, exception.Message, serverToken);
                    return;
                }

                var negotiated = string.IsNullOrEmpty(protocolHeader)
                    ? McpProtocolNegotiator.V2025March
                    : protocolHeader;
                var outcome = await processor.ProcessAsync(
                    body, negotiated, request.Headers["Origin"], serverToken);
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
            catch (Exception exception)
            {
                _ = MainThreadDispatcher.RunAsync(() => Debug.LogException(exception));
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
            finally
            {
                _activeContexts.TryRemove(context, out _);
            }
        }

        private static bool IsAuthorized(HttpListenerRequest request, string token)
        {
            var authorization = request.Headers["Authorization"];
            const string prefix = "Bearer ";
            if (string.IsNullOrEmpty(authorization)
                || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return FixedTimeEquals(authorization.Substring(prefix.Length).Trim(), token);
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);
            var diff = leftBytes.Length ^ rightBytes.Length;
            var max = Math.Max(leftBytes.Length, rightBytes.Length);
            for (var i = 0; i < max; i++)
            {
                var leftValue = i < leftBytes.Length ? leftBytes[i] : (byte)0;
                var rightValue = i < rightBytes.Length ? rightBytes[i] : (byte)0;
                diff |= leftValue ^ rightValue;
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
            response.AddHeader(
                "Access-Control-Allow-Headers",
                "Authorization, Content-Type, Accept, MCP-Protocol-Version, Mcp-Session-Id");
            response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
        }

        private static bool IsJsonContentType(string contentType) =>
            !string.IsNullOrEmpty(contentType)
            && contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

        private static bool AcceptsJson(string accept) =>
            string.IsNullOrEmpty(accept)
            || accept.Contains("application/json")
            || accept.Contains("*/*");

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
                    if (memory.Length + read > maxBytes)
                        throw new InvalidDataException("MCP request body exceeds the configured limit.");
                    memory.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }

        private static async Task WriteHttpError(
            HttpListenerResponse response, int statusCode, string message, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(
                new JObject { ["error"] = message }.ToString(Formatting.None));
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            response.Close();
        }

        private void Log(string message)
        {
            try { OnLog?.Invoke(message); }
            catch { }
        }
    }
}
