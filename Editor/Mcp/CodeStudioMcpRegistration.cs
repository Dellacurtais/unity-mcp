using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    [InitializeOnLoad]
    internal static class CodeStudioMcpRegistration
    {
        internal sealed class RegistrationSnapshot
        {
            public string State { get; }
            public string RuntimeUrl { get; }
            public string Message { get; }
            public string Error { get; }

            public RegistrationSnapshot(string state, string runtimeUrl = null, string message = null, string error = null)
            {
                State = state ?? "Unknown";
                RuntimeUrl = runtimeUrl;
                Message = message;
                Error = error;
            }
        }

        private sealed class SyncResult
        {
            public bool Found;
            public bool Registered;
            public bool ToolsReady;
            public string RuntimeUrl;
            public string Fingerprint;
            public string Message;
            public string Error;
        }

        private sealed class BrokerProbeResult
        {
            public bool Ready;
            public string Message;
            public string Error;
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private static Task<SyncResult> _syncTask;
        private static RegistrationSnapshot _snapshot = new RegistrationSnapshot("Waiting", message: "Looking for CodeStudio automatically.");
        private static string _registeredFingerprint;
        private static double _nextAttempt;
        private static bool _forceSync;

        static CodeStudioMcpRegistration()
        {
            EditorApplication.update += Tick;
            EditorApplication.delayCall += RequestSync;
        }

        internal static RegistrationSnapshot Snapshot => _snapshot;

        internal static void RequestSync()
        {
            _forceSync = true;
            _nextAttempt = 0d;
        }

        internal static IReadOnlyList<string> BuildRuntimeCandidates(string configuredUrl, params string[] discoveredUrls)
        {
            var candidates = new List<string>();
            AddCandidate(candidates, configuredUrl);
            if (discoveredUrls != null)
            {
                foreach (var discoveredUrl in discoveredUrls) AddCandidate(candidates, discoveredUrl);
            }
            AddCandidate(candidates, "http://127.0.0.1:7333");
            AddCandidate(candidates, "http://localhost:7333");
            return candidates;
        }

        internal static string BuildUnityMcpUrl(McpTransportMode mode, int embeddedPort, string brokerUrl)
        {
            if (mode == McpTransportMode.Broker
                && Uri.TryCreate(brokerUrl, UriKind.Absolute, out var brokerUri)
                && (brokerUri.Scheme == "ws" || brokerUri.Scheme == "wss")
                && brokerUri.Port > 0)
            {
                var builder = new UriBuilder(
                    brokerUri.Scheme == "wss" ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                    brokerUri.Host,
                    brokerUri.Port,
                    "/mcp/");
                return builder.Uri.AbsoluteUri;
            }
            return $"http://127.0.0.1:{embeddedPort}/mcp/";
        }

        internal static string BuildBrokerHealthUrl(string mcpUrl)
        {
            if (!Uri.TryCreate(mcpUrl, UriKind.Absolute, out var uri)) return null;
            return new UriBuilder(uri.Scheme, uri.Host, uri.Port, "/health").Uri.AbsoluteUri;
        }

        internal static JObject BuildRegistrationPayload(string mcpUrl, string token)
        {
            return new JObject
            {
                ["id"] = "unity",
                ["name"] = "unity-editor",
                ["transport"] = "streamable_http",
                ["enabled"] = true,
                ["url"] = mcpUrl,
                ["args"] = new JArray(),
                ["env"] = new JObject(),
                ["auth_type"] = "bearer",
                ["auth_token"] = token ?? string.Empty,
                ["default_tier"] = "unity",
                ["config"] = new JObject(),
            };
        }

        private static void Tick()
        {
            if (_syncTask != null)
            {
                if (!_syncTask.IsCompleted) return;
                ApplyCompletedSync();
            }

            var now = EditorApplication.timeSinceStartup;
            if (now < _nextAttempt) return;

            var token = Settings.Token;
            var runtimeUrl = Settings.CoderUrl;
            var mcpUrl = BuildUnityMcpUrl(Settings.TransportMode, Settings.Port, Settings.BrokerUrl);
            var brokerMode = Settings.TransportMode == McpTransportMode.Broker;
            var projectKey = brokerMode
                ? Broker.BrokerProtocol.ProjectKey(Bootstrap.UnityProjectRoot)
                : null;
            var fingerprint = BuildFingerprint(runtimeUrl, mcpUrl, token);
            if (!_forceSync && string.Equals(fingerprint, _registeredFingerprint, StringComparison.Ordinal)) return;

            _forceSync = false;
            if (string.IsNullOrEmpty(token))
            {
                _snapshot = new RegistrationSnapshot("Waiting", message: "Start the Unity MCP transport to generate its token.");
                _nextAttempt = now + 2d;
                return;
            }

            var candidates = BuildRuntimeCandidates(
                runtimeUrl,
                Environment.GetEnvironmentVariable("CODESTUDIO_URL"),
                Environment.GetEnvironmentVariable("MCP_API_URL"));
            _snapshot = new RegistrationSnapshot("Searching", message: "Detecting the local CodeStudio runtime.");
            _nextAttempt = now + 5d;
            _syncTask = Task.Run(() => SyncAsync(
                candidates,
                mcpUrl,
                token,
                brokerMode,
                projectKey));
        }

        private static void ApplyCompletedSync()
        {
            SyncResult result;
            try
            {
                result = _syncTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                result = new SyncResult { Error = exception.GetBaseException().Message };
            }
            _syncTask = null;

            if (result.Found && !string.IsNullOrEmpty(result.RuntimeUrl))
                Settings.CoderUrl = result.RuntimeUrl;

            if (result.Registered && result.ToolsReady)
            {
                _registeredFingerprint = result.Fingerprint;
                _snapshot = new RegistrationSnapshot("Registered", result.RuntimeUrl, result.Message);
                return;
            }

            var state = result.Registered ? "Registered" : result.Found ? "Found" : "Searching";
            _snapshot = new RegistrationSnapshot(state, result.RuntimeUrl, result.Message, result.Error);
            _nextAttempt = EditorApplication.timeSinceStartup + 5d;
        }

        private static async Task<SyncResult> SyncAsync(
            IReadOnlyList<string> candidates,
            string mcpUrl,
            string token,
            bool brokerMode,
            string expectedProjectKey)
        {
            var errors = new List<string>();
            foreach (var runtimeUrl in candidates)
            {
                try
                {
                    if (!await IsCodeStudioAsync(runtimeUrl).ConfigureAwait(false)) continue;

                    var payload = BuildRegistrationPayload(mcpUrl, token);
                    using (var registerCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                    using (var response = await Http.PostAsync(
                        runtimeUrl + "/api/admin/mcp-servers",
                        content,
                        registerCts.Token).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            return new SyncResult
                            {
                                Found = true,
                                RuntimeUrl = runtimeUrl,
                                Error = $"CodeStudio registration failed (HTTP {(int)response.StatusCode}): {Truncate(body)}",
                            };
                        }
                    }

                    using (var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    using (var response = await Http.PostAsync(
                        runtimeUrl + "/api/admin/mcp-servers/unity/test",
                        new StringContent(string.Empty),
                        testCts.Token).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var tested = response.IsSuccessStatusCode ? JObject.Parse(body) : null;
                        var toolsReady = (bool?)tested?["ok"] ?? false;
                        var toolCount = (int?)tested?["toolCount"] ?? 0;
                        var error = (string)tested?["error"];
                        BrokerProbeResult brokerProbe = null;
                        if (toolsReady && brokerMode)
                        {
                            brokerProbe = await ProbeBrokerRoundTripAsync(
                                mcpUrl,
                                token,
                                expectedProjectKey).ConfigureAwait(false);
                            toolsReady = brokerProbe.Ready;
                            if (!toolsReady) error = brokerProbe.Error;
                        }
                        return new SyncResult
                        {
                            Found = true,
                            Registered = true,
                            ToolsReady = toolsReady,
                            RuntimeUrl = runtimeUrl,
                            Fingerprint = BuildFingerprint(runtimeUrl, mcpUrl, token),
                            Message = toolsReady
                                ? brokerProbe?.Message
                                    ?? $"CodeStudio found automatically; Unity registered with {toolCount} tools."
                                : brokerProbe?.Message
                                    ?? "CodeStudio found and Unity registered; waiting for the MCP endpoint to become ready.",
                            Error = toolsReady ? null : error ?? $"Tool discovery returned HTTP {(int)response.StatusCode}: {Truncate(body)}",
                        };
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException
                    || exception is TaskCanceledException
                    || exception is OperationCanceledException
                    || exception is JsonException)
                {
                    errors.Add(runtimeUrl + ": " + exception.GetBaseException().Message);
                }
            }

            return new SyncResult
            {
                Error = "CodeStudio runtime not found. Tried: " + string.Join(", ", candidates)
                    + (errors.Count > 0 ? ". " + Truncate(string.Join(" | ", errors)) : string.Empty),
            };
        }

        private static async Task<BrokerProbeResult> ProbeBrokerRoundTripAsync(
            string mcpUrl,
            string token,
            string expectedProjectKey)
        {
            try
            {
                var healthUrl = BuildBrokerHealthUrl(mcpUrl);
                if (string.IsNullOrEmpty(healthUrl))
                    return new BrokerProbeResult { Error = "The broker MCP endpoint is invalid." };

                JObject health;
                using (var healthCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (var response = await Http.GetAsync(healthUrl, healthCts.Token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return new BrokerProbeResult
                        {
                            Error = $"Broker health failed (HTTP {(int)response.StatusCode}): {Truncate(body)}",
                        };
                    health = JObject.Parse(body);
                }

                if (!((bool?)health["bridgeConnected"] ?? false))
                    return new BrokerProbeResult
                    {
                        Message = "CodeStudio is configured, but no Unity bridge is connected to the broker.",
                        Error = "Broker is online, but Unity is offline.",
                    };
                var activeProjectKey = (string)health["activeProjectKey"];
                if (!string.IsNullOrEmpty(expectedProjectKey)
                    && !string.Equals(activeProjectKey, expectedProjectKey, StringComparison.Ordinal))
                    return new BrokerProbeResult
                    {
                        Message = "CodeStudio is configured, but this Unity project is not the broker's active route.",
                        Error = "Activate this Unity project before using its tools.",
                    };
                if (health["routeReady"] != null && !((bool?)health["routeReady"] ?? false))
                    return new BrokerProbeResult
                    {
                        Message = "CodeStudio is configured, but the broker route is not ready.",
                        Error = (string)health["routeError"] ?? "Unity route is unavailable.",
                    };

                var requestBody = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "codestudio-unity-health",
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "get_server_health",
                        ["arguments"] = new JObject(),
                    },
                };
                using (var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                using (var request = new HttpRequestMessage(HttpMethod.Post, mcpUrl))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    request.Headers.TryAddWithoutValidation(
                        "MCP-Protocol-Version",
                        McpProtocolNegotiator.Preferred);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Content = new StringContent(
                        requestBody.ToString(Formatting.None),
                        Encoding.UTF8,
                        "application/json");
                    using (var response = await Http.SendAsync(request, probeCts.Token).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            return new BrokerProbeResult
                            {
                                Error = $"Unity round-trip failed (HTTP {(int)response.StatusCode}): {Truncate(body)}",
                            };
                        var rpc = JObject.Parse(body);
                        var rpcError = (string)rpc["error"]?["message"];
                        var toolError = (bool?)rpc["result"]?["isError"] ?? false;
                        if (!string.IsNullOrEmpty(rpcError) || toolError)
                            return new BrokerProbeResult
                            {
                                Error = rpcError
                                    ?? (string)rpc["result"]?["structuredContent"]?["message"]
                                    ?? "Unity health tool returned an error.",
                            };
                    }
                }

                return new BrokerProbeResult
                {
                    Ready = true,
                    Message = "CodeStudio registered; broker → Unity tool round-trip passed.",
                };
            }
            catch (Exception exception) when (exception is HttpRequestException
                || exception is TaskCanceledException
                || exception is OperationCanceledException
                || exception is JsonException)
            {
                return new BrokerProbeResult
                {
                    Error = "Broker → Unity readiness probe failed: " + exception.GetBaseException().Message,
                };
            }
        }

        private static async Task<bool> IsCodeStudioAsync(string runtimeUrl)
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await Http.GetAsync(
                runtimeUrl + "/api/admin/mcp-servers",
                probeCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JObject.Parse(body)["servers"] is JArray;
        }

        private static void AddCandidate(ICollection<string> candidates, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsLoopback)
                return;
            var normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)) return;
            }
            candidates.Add(normalized);
        }

        private static string BuildFingerprint(string runtimeUrl, string mcpUrl, string token)
            => (runtimeUrl ?? string.Empty).TrimEnd('/') + "|" + mcpUrl + "|" + (token ?? string.Empty);

        private static string Truncate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "no response body";
            value = value.Trim();
            return value.Length <= 240 ? value : value.Substring(0, 240) + "…";
        }
    }
}
