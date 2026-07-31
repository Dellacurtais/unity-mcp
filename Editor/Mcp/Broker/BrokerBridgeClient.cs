using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Broker
{
    public sealed class BrokerBridgeClient : IDisposable
    {
        private readonly McpRequestProcessor _processor;
        private readonly McpToolRegistry _registry;
        private readonly string _projectRoot;
        private readonly Uri _uri;
        private readonly string _token;
        private readonly string _projectKey;
        private readonly int _processId;
        private readonly int _maxMessageBytes;
        private string _registryHash;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly object _socketLock = new object();
        private readonly System.Random _random = new System.Random();

        private CancellationTokenSource _lifetime;
        private ClientWebSocket _socket;
        private TaskCompletionSource<JObject> _helloAcknowledgement;
        private string _helloMessageId;
        private bool _activeProject;
        private BrokerBridgeSnapshot _snapshot = BrokerBridgeSnapshot.Stopped;

        public BrokerBridgeSnapshot Snapshot => Volatile.Read(ref _snapshot);
        public event Action<BrokerBridgeSnapshot> StateChanged;
        public event Func<string, JObject, Task> ControlRequested;
        public event Action<string> Log;

        public BrokerBridgeClient(
            McpRequestProcessor processor,
            McpToolRegistry registry,
            string projectRoot,
            string url,
            string token,
            int maxMessageBytes)
        {
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _projectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
            _uri = new Uri(url ?? throw new ArgumentNullException(nameof(url)));
            if (_uri.Scheme != "ws" && _uri.Scheme != "wss")
                throw new ArgumentException("Broker URL must use ws:// or wss://.", nameof(url));
            _token = token ?? string.Empty;
            if (maxMessageBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            _maxMessageBytes = maxMessageBytes;
            _projectKey = BrokerProtocol.ProjectKey(projectRoot);
            _processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            _registryHash = registry.VersionHash;
            _registry.Changed += OnRegistryChanged;
        }

        internal string AdvertisedRegistryHash => Volatile.Read(ref _registryHash);

        public void Start()
        {
            if (_lifetime != null) return;
            _lifetime = new CancellationTokenSource();
            SetState(false, "connecting", null, false);
            _ = Task.Run(() => RunAsync(_lifetime.Token));
        }

        public async Task SendEventAsync(
            string eventName,
            string state,
            JObject data = null,
            CancellationToken cancellationToken = default)
        {
            var payload = data != null ? (JObject)data.DeepClone() : new JObject();
            payload["event"] = eventName;
            payload["state"] = state;
            var lifetimeToken = _lifetime != null ? _lifetime.Token : CancellationToken.None;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken,
                cancellationToken))
            {
                await SendCurrentAsync(BrokerProtocol.Envelope(
                    "event", payload, _projectKey, _processId), linked.Token).ConfigureAwait(false);
            }
        }

        public void CancelRequest(string requestId) => _processor.Cancel(requestId);

        public void Dispose()
        {
            _registry.Changed -= OnRegistryChanged;
            var lifetime = Interlocked.Exchange(ref _lifetime, null);
            try { lifetime?.Cancel(); } catch { }
            ClientWebSocket socket;
            lock (_socketLock)
            {
                socket = _socket;
                _socket = null;
            }
            try { socket?.Abort(); } catch { }
            try { socket?.Dispose(); } catch { }
            try { lifetime?.Dispose(); } catch { }
            SetState(false, "stopped", null, false);
        }

        private void OnRegistryChanged()
        {
            var registryHash = _registry.VersionHash;
            if (string.Equals(registryHash, Volatile.Read(ref _registryHash), StringComparison.Ordinal)) return;
            Volatile.Write(ref _registryHash, registryHash);

            if (_lifetime == null) return;
            ClientWebSocket socket;
            lock (_socketLock) socket = _socket;
            if (socket == null
                || socket.State != WebSocketState.Open && socket.State != WebSocketState.Connecting) return;

            SetState(false, "registry_changed", null, false);
            try { socket.Abort(); }
            catch { }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                ClientWebSocket socket = null;
                try
                {
                    socket = new ClientWebSocket();
                    if (!string.IsNullOrEmpty(_token))
                        socket.Options.SetRequestHeader("Authorization", "Bearer " + _token);
                    lock (_socketLock) _socket = socket;
                    SetState(false, attempt == 0 ? "connecting" : "reconnecting", null, false);
                    await socket.ConnectAsync(_uri, cancellationToken);
                    attempt = 0;
                    using (var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        var acknowledgement = new TaskCompletionSource<JObject>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        _helloAcknowledgement = acknowledgement;
                        var receiveTask = ReceiveLoopAsync(socket, connectionLifetime.Token);
                        SetState(false, "waiting_for_broker_ack", null, false);
                        await SendHelloAsync(cancellationToken);
                        var acknowledgementTimeout = Task.Delay(
                            TimeSpan.FromSeconds(10),
                            connectionLifetime.Token);
                        var acknowledgementCompleted = await Task.WhenAny(
                            acknowledgement.Task,
                            receiveTask,
                            acknowledgementTimeout);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (acknowledgementCompleted == receiveTask)
                        {
                            await receiveTask;
                            throw new WebSocketException(
                                "Broker bridge closed before acknowledging the Unity registry.");
                        }
                        if (acknowledgementCompleted != acknowledgement.Task)
                            throw new TimeoutException(
                                "Broker did not acknowledge the Unity registry within 10 seconds.");
                        var acknowledgementPayload = await acknowledgement.Task;
                        _activeProject = (bool?)acknowledgementPayload["active"] ?? false;
                        SetState(true, "ready", null, _activeProject);
                        var heartbeatTask = HeartbeatLoopAsync(connectionLifetime.Token);
                        var completed = await Task.WhenAny(receiveTask, heartbeatTask);
                        connectionLifetime.Cancel();
                        try { socket.Abort(); } catch { }
                        await completed;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SetState(false, "reconnecting", exception.Message, false);
                    SafeLog("bridge disconnected: " + exception.Message);
                }
                finally
                {
                    _helloAcknowledgement = null;
                    _helloMessageId = null;
                    lock (_socketLock)
                    {
                        if (ReferenceEquals(_socket, socket)) _socket = null;
                    }
                    try { socket?.Dispose(); } catch { }
                }

                attempt++;
                var delayMs = Math.Min(30000, 500 * (1 << Math.Min(attempt, 5)));
                lock (_random) delayMs += _random.Next(0, 500);
                try { await Task.Delay(delayMs, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task SendHelloAsync(CancellationToken cancellationToken)
        {
            var listRequest = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "bridge-registry",
                ["method"] = "tools/list",
            };
            var outcome = await _processor.ProcessAsync(
                listRequest.ToString(Formatting.None),
                McpProtocolNegotiator.V2025March,
                "broker",
                cancellationToken);
            var response = JObject.Parse(outcome.Body);
            var result = response["result"] as JObject ?? new JObject();
            var tools = result["tools"] as JArray ?? new JArray();
            var meta = result["_meta"] as JObject ?? new JObject();
            var registryCount = (int?)meta["registryCount"]
                ?? throw new InvalidDataException("Unity tool registry count is missing.");
            _registryHash = (string)meta["registryHash"]
                ?? throw new InvalidDataException("Unity tool registry hash is missing.");
            var identity = BrokerProtocol.Identity(
                _projectRoot,
                registryCount,
                _registryHash);
            identity["tools"] = tools.DeepClone();
            identity["state"] = "ready";
            var hello = BrokerProtocol.Envelope(
                "hello", identity, _projectKey, _processId);
            _helloMessageId = (string)hello["messageId"];
            await SendCurrentAsync(hello, cancellationToken);
        }

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                var state = Snapshot.State;
                await SendCurrentAsync(BrokerProtocol.Envelope(
                    "heartbeat",
                    new JObject { ["state"] = state },
                    _projectKey,
                    _processId), cancellationToken);
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using (var stream = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                            throw new WebSocketException("Broker closed the WebSocket connection.");
                        if (stream.Length + result.Count > _maxMessageBytes)
                            throw new WebSocketException("Broker message exceeds the configured size limit.");
                        stream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);
                    if (result.MessageType != WebSocketMessageType.Text) continue;
                    var json = Encoding.UTF8.GetString(stream.ToArray());
                    var message = JObject.Parse(json);
                    _ = HandleMessageSafelyAsync(message, cancellationToken);
                }
            }
        }

        private async Task HandleMessageSafelyAsync(
            JObject message,
            CancellationToken cancellationToken)
        {
            try
            {
                await HandleMessageAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                SafeLog("bridge message failed: " + exception.Message);
                await TrySendCommandErrorAsync(message, exception, cancellationToken);
            }
        }

        private async Task HandleMessageAsync(JObject message, CancellationToken cancellationToken)
        {
            if ((int?)message["version"] != BrokerProtocol.Version)
                throw new InvalidDataException("Unsupported broker bridge protocol version.");
            if (!string.Equals((string)message["projectKey"], _projectKey, StringComparison.Ordinal)
                || (int?)message["processId"] != _processId
                || !string.Equals(
                    (string)message["generationId"],
                    BrokerProtocol.GenerationId,
                    StringComparison.Ordinal))
                throw new InvalidDataException("Broker message targets a stale Unity bridge generation.");
            var type = (string)message["type"];
            var payload = message["payload"] as JObject ?? new JObject();
            var correlationId = (string)message["correlationId"];

            if (type == "hello_ack")
            {
                if (!string.Equals(correlationId, _helloMessageId, StringComparison.Ordinal))
                    throw new InvalidDataException("Broker acknowledgement does not match the Unity hello.");
                _helloAcknowledgement?.TrySetResult(payload);
                return;
            }
            if (type == "route_state")
            {
                _activeProject = (bool?)payload["active"] ?? false;
                SetState(true, "ready", null, _activeProject);
                return;
            }
            if (type == "cancel")
            {
                _processor.Cancel((string)payload["requestId"]);
                return;
            }
            if (type != "command") return;
            var control = (string)payload["control"];
            if (!string.IsNullOrEmpty(control))
            {
                var handler = ControlRequested;
                if (handler != null) await handler(control, payload);
                return;
            }

            var requestId = (string)payload["requestId"];
            var jsonRpc = payload["jsonRpc"];
            if (jsonRpc == null) return;
            await SendCurrentAsync(BrokerProtocol.Envelope(
                "accepted",
                new JObject { ["requestId"] = requestId },
                _projectKey,
                _processId,
                correlationId), cancellationToken);

            var executionState = await MainThreadDispatcher.RunAsync(() => new JObject
            {
                ["requestId"] = requestId,
                ["applicationFocused"] = Application.isFocused,
                ["focusedWindow"] = EditorWindow.focusedWindow != null
                    ? EditorWindow.focusedWindow.GetType().Name
                    : null,
                ["editorTimeSinceStartup"] = EditorApplication.timeSinceStartup,
            }, cancellationToken);
            await SendCurrentAsync(BrokerProtocol.Envelope(
                "execution_started",
                executionState,
                _projectKey,
                _processId,
                correlationId), cancellationToken);

            var outcome = await _processor.ProcessAsync(
                jsonRpc.ToString(Formatting.None),
                McpProtocolNegotiator.V2025March,
                "broker",
                cancellationToken);
            if (string.IsNullOrEmpty(outcome.Body)) return;
            await SendCurrentAsync(BrokerProtocol.Envelope(
                "result",
                new JObject { ["requestId"] = requestId, ["jsonRpc"] = JToken.Parse(outcome.Body) },
                _projectKey,
                _processId,
                correlationId), cancellationToken);
        }

        private async Task TrySendCommandErrorAsync(
            JObject message,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (!string.Equals((string)message?["type"], "command", StringComparison.Ordinal))
                return;
            var correlationId = (string)message["correlationId"];
            var payload = message["payload"] as JObject;
            var requestId = (string)payload?["requestId"];
            var rpcId = payload?["jsonRpc"]?["id"];
            if (string.IsNullOrEmpty(correlationId) || string.IsNullOrEmpty(requestId))
                return;

            var errorResponse = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = rpcId?.DeepClone() ?? JValue.CreateNull(),
                ["error"] = new JObject
                {
                    ["code"] = -32603,
                    ["message"] = "Unity bridge failed to process the command.",
                    ["data"] = new JObject
                    {
                        ["code"] = "UNITY_BRIDGE_ERROR",
                        ["detail"] = exception.Message,
                    },
                },
            };
            try
            {
                await SendCurrentAsync(BrokerProtocol.Envelope(
                    "result",
                    new JObject
                    {
                        ["requestId"] = requestId,
                        ["jsonRpc"] = errorResponse,
                    },
                    _projectKey,
                    _processId,
                    correlationId), cancellationToken);
            }
            catch (Exception sendException)
            {
                SafeLog("failed to report bridge command error: " + sendException.Message);
            }
        }

        private async Task SendCurrentAsync(JObject message, CancellationToken cancellationToken)
        {
            ClientWebSocket socket;
            lock (_socketLock) socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
                throw new WebSocketException("Broker bridge is not connected.");
            var bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            if (bytes.Length > _maxMessageBytes)
                throw new WebSocketException("Bridge message exceeds the configured size limit.");
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }

        private void SetState(bool connected, string state, string error, bool activeProject)
        {
            var snapshot = new BrokerBridgeSnapshot(
                connected,
                activeProject,
                state,
                error,
                _projectKey,
                _processId,
                BrokerProtocol.GenerationId,
                _registryHash,
                DateTimeOffset.UtcNow);
            Volatile.Write(ref _snapshot, snapshot);
            try { StateChanged?.Invoke(snapshot); }
            catch { }
        }

        private void SafeLog(string message)
        {
            try { Log?.Invoke(message); }
            catch { }
        }
    }
}
