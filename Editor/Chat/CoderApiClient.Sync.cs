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
                if (session != null && session.Id > 0) list.Add(session);
            }
            return list.OrderByDescending(session => session.UpdatedAt ?? session.CreatedAt).ToList();
        }

        public async Task<CoderSessionSnapshot> GetSessionAsync(
            string serverUrl, string token, int sessionId, CancellationToken ct)
        {
            if (sessionId <= 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
            var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}";
            using var request = CreateRequest(HttpMethod.Get, url, token);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
            return CoderSessionSnapshot.FromJson(JObject.Parse(body));
        }

        public void AttachSession(string serverUrl, string token, int sessionId, bool replaceConversation = true)
        {
            StopSessionSync();
            if (sessionId <= 0)
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
            string serverUrl, string token, int sessionId, bool replaceConversation, CancellationToken ct)
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
            string serverUrl, string token, int sessionId, bool replaceConversation, CancellationToken ct)
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
                    var delay = string.Equals(snapshot.Session?.Status, "running", StringComparison.OrdinalIgnoreCase)
                        ? 1500
                        : 3000;
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

        private async Task PostInteractionAsync(
            string serverUrl,
            string token,
            int sessionId,
            string endpoint,
            string requestedKey,
            JObject body,
            string successMessage)
        {
            var key = ResolveInteractionKey(requestedKey);
            if (sessionId <= 0)
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
