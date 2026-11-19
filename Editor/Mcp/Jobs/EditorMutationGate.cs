using System.Threading;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Jobs
{
    // Shared by request coordinators and long-running jobs. Do not dispose while
    // the Editor domain is alive: broker/embedded processors have independent lifetimes.
    internal static class EditorMutationGate
    {
        internal static readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);

        private static readonly object OwnerSync = new object();
        private static string _ownerJobId;
        private static string _ownerKind;

        /// Long-running jobs (tests, builds, reimports, refresh/compile) hold the gate for
        /// minutes. They register here so MCP writes can answer RESOURCE_BUSY at once
        /// instead of parking a request (and a concurrency slot) behind the job.
        internal static void SetJobOwner(string jobId, string kind)
        {
            lock (OwnerSync)
            {
                _ownerJobId = jobId;
                _ownerKind = kind;
            }
        }

        internal static void ClearJobOwner(string jobId)
        {
            lock (OwnerSync)
            {
                if (_ownerJobId == jobId)
                {
                    _ownerJobId = null;
                    _ownerKind = null;
                }
            }
        }

        internal static bool TryGetJobOwner(out string jobId, out string kind)
        {
            lock (OwnerSync)
            {
                jobId = _ownerJobId;
                kind = _ownerKind;
                return !string.IsNullOrEmpty(jobId);
            }
        }
    }
}
