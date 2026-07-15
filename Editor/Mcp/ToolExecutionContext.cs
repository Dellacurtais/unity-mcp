using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class ToolExecutionContext
    {
        private static readonly AsyncLocal<ToolExecutionContext> Slot = new AsyncLocal<ToolExecutionContext>();

        public static ToolExecutionContext Current => Slot.Value;

        public string ToolName { get; set; }
        public string RequestId { get; set; }
        public string ProtocolVersion { get; set; }
        public string Origin { get; set; }
        public string RequestPath { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public DateTimeOffset DeadlineUtc { get; set; }
        public bool IsNotification { get; set; }
        public JObject RequestMeta { get; set; }

        public static IDisposable Push(ToolExecutionContext context)
        {
            var previous = Slot.Value;
            Slot.Value = context;
            return new Scope(() => Slot.Value = previous);
        }

        private sealed class Scope : IDisposable
        {
            private Action _dispose;
            public Scope(Action dispose) { _dispose = dispose; }
            public void Dispose() { Interlocked.Exchange(ref _dispose, null)?.Invoke(); }
        }
    }
}
