using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class McpRequestCoordinator : IDisposable
    {
        private readonly SemaphoreSlim _concurrency;
        private readonly SemaphoreSlim _mutation = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly object _lifecycleLock = new object();
        private bool _disposed;
        private int _activeOperations;
        private int _resourcesDisposed;

        public McpRequestCoordinator(int maxConcurrency)
        {
            _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        }

        public int InFlightCount => _requests.Count;

        public async Task<Lease> BeginAsync(string requestId, bool isWrite, CancellationToken serverToken, TimeSpan timeout)
        {
            lock (_lifecycleLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(McpRequestCoordinator));
                _activeOperations++;
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _shutdown.Token);
            if (timeout > TimeSpan.Zero) linked.CancelAfter(timeout);
            bool concurrencyHeld = false;
            bool mutationHeld = false;
            bool requestRegistered = false;
            try
            {
                await _concurrency.WaitAsync(linked.Token).ConfigureAwait(false);
                concurrencyHeld = true;
                if (!string.IsNullOrEmpty(requestId))
                {
                    if (!_requests.TryAdd(requestId, linked))
                        throw new InvalidOperationException($"Duplicate MCP request id '{requestId}'.");
                    requestRegistered = true;
                }
                if (isWrite)
                {
                    await _mutation.WaitAsync(linked.Token).ConfigureAwait(false);
                    mutationHeld = true;
                }
                return new Lease(this, requestId, linked, mutationHeld);
            }
            catch
            {
                if (requestRegistered) _requests.TryRemove(requestId, out _);
                if (mutationHeld) _mutation.Release();
                if (concurrencyHeld) _concurrency.Release();
                linked.Dispose();
                ReleaseOperation();
                throw;
            }
        }

        public bool Cancel(string requestId)
        {
            if (string.IsNullOrEmpty(requestId) || !_requests.TryGetValue(requestId, out var cts)) return false;
            try { cts.Cancel(); return true; }
            catch { return false; }
        }

        public void CancelAll()
        {
            foreach (var pair in _requests) { try { pair.Value.Cancel(); } catch { } }
        }

        private void End(string requestId, CancellationTokenSource cts, bool mutationHeld)
        {
            if (!string.IsNullOrEmpty(requestId)) _requests.TryRemove(requestId, out _);
            if (mutationHeld) _mutation.Release();
            _concurrency.Release();
            cts.Dispose();
            ReleaseOperation();
        }

        private void ReleaseOperation()
        {
            bool disposeResources;
            lock (_lifecycleLock)
            {
                _activeOperations--;
                disposeResources = _disposed && _activeOperations == 0;
            }
            if (disposeResources) DisposeResources();
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
            _shutdown.Dispose();
            _concurrency.Dispose();
            _mutation.Dispose();
        }

        public void Dispose()
        {
            bool disposeResources;
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                _disposed = true;
                disposeResources = _activeOperations == 0;
            }
            try { _shutdown.Cancel(); } catch { }
            if (disposeResources) DisposeResources();
        }

        public sealed class Lease : IDisposable
        {
            private McpRequestCoordinator _owner;
            private readonly string _requestId;
            private readonly CancellationTokenSource _cts;
            private readonly bool _mutationHeld;
            internal Lease(McpRequestCoordinator owner, string requestId, CancellationTokenSource cts, bool mutationHeld)
            { _owner = owner; _requestId = requestId; _cts = cts; _mutationHeld = mutationHeld; }
            public CancellationToken CancellationToken => _cts.Token;
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.End(_requestId, _cts, _mutationHeld);
            }
        }
    }
}
