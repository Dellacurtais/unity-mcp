using System;
using System.Collections.Generic;
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
    public sealed partial class CoderApiClient
    {
        public void RefreshAgents(string serverUrl, string token)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var agents = await GetAgentsAsync(serverUrl, token, _lifetimeCts.Token).ConfigureAwait(false);
                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        Bootstrap.CoderAgents = agents;
                        Bootstrap.NotifyCoderAgentsChanged();
                    }, _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    await MainThreadDispatcher.RunAsync(() =>
                        _session.SetConnectionState(ChatConnectionState.Error, $"Falha ao carregar agentes: {exception.Message}"));
                }
            });
        }

        public async Task<List<CoderAgentInfo>> GetAgentsAsync(
            string serverUrl, string token, CancellationToken ct)
        {
            var url = $"{serverUrl.TrimEnd('/')}/api/coder/agents";
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");

            var root = JObject.Parse(body);
            var agents = new List<CoderAgentInfo>();
            foreach (var tokenItem in root["agents"] as JArray ?? new JArray())
            {
                var agent = CoderAgentInfo.FromJson(tokenItem);
                if (agent != null && !string.IsNullOrEmpty(agent.Id)) agents.Add(agent);
            }
            return agents;
        }

        public void RefreshSessions(string serverUrl, string token, int projectId)
        {
            if (projectId <= 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var sessions = await ListSessionsAsync(serverUrl, token, projectId, _lifetimeCts.Token)
                        .ConfigureAwait(false);
                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        Bootstrap.CoderSessions = sessions;
                        Bootstrap.NotifyCoderSessionsChanged();
                    }, _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    await MainThreadDispatcher.RunAsync(() =>
                        _session.SetConnectionState(ChatConnectionState.Error, $"Falha ao listar sessões: {exception.Message}"));
                }
            });
        }

        public async Task<List<CoderSessionInfo>> ListSessionsAsync(
            string serverUrl, string token, int projectId, CancellationToken ct)
        {
            var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions?project_id={projectId}";
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");

            var list = new List<CoderSessionInfo>();
            foreach (var item in JArray.Parse(body))
            {
                var session = CoderSessionInfo.FromJson(item);
                if (session != null && CoderSessionIdentity.IsValid(session.Id)) list.Add(session);
            }
            return list.OrderByDescending(session => session.UpdatedAt ?? session.CreatedAt).ToList();
        }

        public async Task<CoderSessionSnapshot> GetSessionAsync(
            string serverUrl, string token, string sessionId, CancellationToken ct)
        {
            sessionId = CoderSessionIdentity.Normalize(sessionId);
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session id must be a canonical UUID.", nameof(sessionId));
            var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}";
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
            return CoderSessionSnapshot.FromJson(JObject.Parse(body));
        }

        public void AttachSession(string serverUrl, string token, string sessionId, bool replaceConversation = true)
        {
            StopSessionSync();
            sessionId = CoderSessionIdentity.Normalize(sessionId);
            if (string.IsNullOrEmpty(sessionId))
            {
                _session.SetConnectionState(ChatConnectionState.Offline, "Nenhuma sessão selecionada");
                return;
            }

            Settings.CoderSessionId = sessionId;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _syncCts = linked;
            _session.SetConnectionState(ChatConnectionState.Syncing, $"Sincronizando sessão #{sessionId}");
            _ = Task.Run(() => SessionSyncLoopAsync(serverUrl, token, sessionId, replaceConversation, linked.Token));
        }

        public async Task<CoderSessionSnapshot> RefreshAttachedSessionAsync(
            string serverUrl, string token, string sessionId, bool replaceConversation, CancellationToken ct)
        {
            var snapshot = await GetSessionAsync(serverUrl, token, sessionId, ct).ConfigureAwait(false);
            await MainThreadDispatcher.RunAsync(() =>
            {
                Settings.CoderSessionId = sessionId;
                _session.MergeRemoteSnapshot(snapshot, replaceConversation);
                Bootstrap.UpdateCoderSession(snapshot.Session);
            }, ct).ConfigureAwait(false);
            return snapshot;
        }

        private async Task SessionSyncLoopAsync(
            string serverUrl, string token, string sessionId, bool replaceConversation, CancellationToken ct)
        {
            var failureCount = 0;
            var replace = replaceConversation;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (IsRunning)
                    {
                        await Task.Delay(750, ct).ConfigureAwait(false);
                        continue;
                    }

                    var snapshot = await RefreshAttachedSessionAsync(serverUrl, token, sessionId, replace, ct)
                        .ConfigureAwait(false);
                    replace = false;
                    failureCount = 0;
                    if (IsActiveSessionStatus(snapshot.Session?.Status))
                    {
                        var terminal = await AttachActiveRunAsync(serverUrl, token, sessionId, ct)
                            .ConfigureAwait(false);
                        if (!terminal)
                            await Task.Delay(1500, ct).ConfigureAwait(false);
                        continue;
                    }
                    var delay = 3000;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    failureCount++;
                    var delay = Math.Min(15000, 1000 * (1 << Math.Min(failureCount, 4)));
                    await MainThreadDispatcher.RunAsync(() =>
                        _session.SetConnectionState(ChatConnectionState.Error,
                            $"Sync temporariamente indisponível; nova tentativa em {delay / 1000}s: {exception.Message}"));
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }

        internal static bool IsActiveSessionStatus(string status)
            => string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);

        private async Task<bool> AttachActiveRunAsync(
            string serverUrl,
            string token,
            string sessionId,
            CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _attachedStreamActive, 1, 0) != 0) return false;
            var streamStarted = false;
            var terminalSeen = false;
            try
            {
                var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/attach";
                using var request = CreateRequest(HttpMethod.Get, url, token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (!string.IsNullOrEmpty(_session.LastEventId))
                    request.Headers.TryAddWithoutValidation("Last-Event-ID", _session.LastEventId);
                using var response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
                var responseBody = response.IsSuccessStatusCode
                    ? null
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {responseBody}");

                await MainThreadDispatcher.RunAsync(() =>
                {
                    if (string.IsNullOrEmpty(_session.LastEventId))
                        _session.PrepareForLiveReplay();
                    _session.StartAssistant();
                }, ct).ConfigureAwait(false);
                streamStarted = true;

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                terminalSeen = await ParseSseAsync(reader, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _attachedStreamActive, 0);
                if (streamStarted)
                {
                    if (terminalSeen || ct.IsCancellationRequested)
                        await MainThreadDispatcher.RunAsync(() => _session.FinishAssistant());
                    else
                        await MainThreadDispatcher.RunAsync(() =>
                            _session.SetConnectionState(
                                ChatConnectionState.Syncing,
                                "Stream interrompido; reconectando do último evento"));
                }
            }
            return terminalSeen;
        }

        private async Task PostInteractionAsync(
            string serverUrl,
            string token,
            string sessionId,
            string endpoint,
            string requestedKey,
            JObject body,
            string successMessage)
        {
            var key = ResolveInteractionKey(requestedKey);
            sessionId = CoderSessionIdentity.Normalize(sessionId);
            if (string.IsNullOrEmpty(sessionId))
            {
                _session.SetInteractionFailed(key, "Sessão não definida");
                return;
            }

            var pending = _session.FindInteraction(key);
            if (pending != null && pending.State == PendingInteractionState.Submitting) return;
            _session.SetInteractionSubmitting(key);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            timeout.CancelAfter(InteractionTimeout);
            try
            {
                var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/{endpoint}";
                using var request = CreateRequest(HttpMethod.Post, url, token,
                    new StringContent(body.ToString(FormattingNs.None), Encoding.UTF8, "application/json"));
                using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                await MainThreadDispatcher.RunAsync(() =>
                {
                    if (response.IsSuccessStatusCode)
                    {
                        _session.CompleteInteraction(key);
                        _session.AddSystemNote(successMessage);
                    }
                    else
                    {
                        _session.SetInteractionFailed(key,
                            $"HTTP {(int)response.StatusCode}: {responseBody}");
                    }
                }, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_lifetimeCts.IsCancellationRequested)
            {
                await MainThreadDispatcher.RunAsync(() =>
                    _session.SetInteractionFailed(key, "Tempo limite ao enviar a resposta"));
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                await MainThreadDispatcher.RunAsync(() =>
                    _session.SetInteractionFailed(key, exception.Message));
            }
        }

        private void MutateSession(
            string serverUrl,
            string token,
            string sessionId,
            HttpMethod method,
            string endpoint,
            JObject body,
            string successMessage = null)
        {
            sessionId = CoderSessionIdentity.Normalize(sessionId);
            if (string.IsNullOrEmpty(sessionId)) return;
            _ = MutateSessionAsync(serverUrl, token, sessionId, method, endpoint, body, successMessage);
        }

        private async Task MutateSessionAsync(
            string serverUrl,
            string token,
            string sessionId,
            HttpMethod method,
            string endpoint,
            JObject body,
            string successMessage)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            timeout.CancelAfter(InteractionTimeout);
            try
            {
                await SendJsonMutationAsync(
                    serverUrl, token, sessionId, method, endpoint, body, timeout.Token).ConfigureAwait(false);
                var snapshot = await GetSessionAsync(serverUrl, token, sessionId, timeout.Token).ConfigureAwait(false);
                await MainThreadDispatcher.RunAsync(() =>
                {
                    _session.UpdateRemoteSession(snapshot.Session);
                    Bootstrap.UpdateCoderSession(snapshot.Session);
                    if (!string.IsNullOrEmpty(successMessage)) _session.AddSystemNote(successMessage);
                }, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_lifetimeCts.IsCancellationRequested)
            {
                await MainThreadDispatcher.RunAsync(() =>
                    _session.AddSystemNote($"Tempo limite ao aplicar `{endpoint}`."));
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                await MainThreadDispatcher.RunAsync(() =>
                    _session.AddSystemNote($"Não foi possível aplicar `{endpoint}`: {exception.Message}"));
            }
        }

        private async Task ApplyPostCreationSettingsAsync(
            string serverUrl,
            string token,
            string sessionId,
            CoderSessionOptions options,
            CancellationToken ct)
        {
            await SendJsonMutationAsync(serverUrl, token, sessionId, new HttpMethod("PATCH"),
                "plan-depth", new JObject { ["depth"] = options.PlanDepth ?? "normal" }, ct).ConfigureAwait(false);
            await SendJsonMutationAsync(serverUrl, token, sessionId, new HttpMethod("PATCH"),
                "tool-output-cap-level",
                new JObject { ["level"] = options.ToolOutputCapLevel ?? "economic" }, ct).ConfigureAwait(false);
            await SendJsonMutationAsync(serverUrl, token, sessionId, new HttpMethod("PATCH"),
                "tool-permission-mode",
                new JObject { ["mode"] = options.ToolPermissionMode ?? "auto" }, ct).ConfigureAwait(false);
            await SendJsonMutationAsync(serverUrl, token, sessionId, new HttpMethod("PATCH"),
                "response-language",
                new JObject
                {
                    ["language"] = string.IsNullOrWhiteSpace(options.ResponseLanguage)
                        ? JValue.CreateNull()
                        : options.ResponseLanguage,
                }, ct).ConfigureAwait(false);
            await SendJsonMutationAsync(serverUrl, token, sessionId, new HttpMethod("PATCH"),
                "output-style",
                new JObject
                {
                    ["style"] = string.IsNullOrWhiteSpace(options.OutputStyle)
                        ? JValue.CreateNull()
                        : options.OutputStyle,
                }, ct).ConfigureAwait(false);
        }

        private static async Task<JObject> SendJsonMutationAsync(
            string serverUrl,
            string token,
            string sessionId,
            HttpMethod method,
            string endpoint,
            JObject body,
            CancellationToken ct)
        {
            var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/{endpoint}";
            using var request = CreateRequest(method, url, token,
                new StringContent((body ?? new JObject()).ToString(FormattingNs.None),
                    Encoding.UTF8, "application/json"));
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {responseBody}");
            if (string.IsNullOrWhiteSpace(responseBody)) return new JObject();
            try { return JObject.Parse(responseBody); }
            catch { return new JObject(); }
        }

        private string ResolveInteractionKey(string requestedKey)
        {
            if (!string.IsNullOrEmpty(requestedKey) && _session.FindInteraction(requestedKey) != null)
                return requestedKey;
            var prefix = (requestedKey ?? "").Split(':')[0];
            var match = _session.Interactions.FirstOrDefault(interaction =>
                interaction.Key != null && interaction.Key.StartsWith(prefix + ":", StringComparison.Ordinal));
            return match?.Key ?? requestedKey;
        }

        private static HttpRequestMessage CreateRequest(
            HttpMethod method, string url, string token, HttpContent content = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }
    }
}
