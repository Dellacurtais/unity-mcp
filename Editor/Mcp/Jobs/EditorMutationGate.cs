using System.Threading;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Jobs
{
    // Shared by request coordinators and long-running jobs. Do not dispose while
    // the Editor domain is alive: broker/embedded processors have independent lifetimes.
    internal static class EditorMutationGate
    {
        internal static readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
    }
}
