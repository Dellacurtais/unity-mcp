using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Jobs
{
    public enum ToolJobStatus
    {
        Queued,
        Running,
        Completed,
        Failed,
        CancelRequested,
        Cancelled,
    }

    public sealed class ToolJobRecord
    {
        internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        internal readonly object Sync = new object();

        public string Id { get; internal set; }
        public string Kind { get; internal set; }
        public ToolJobStatus Status { get; internal set; }
        public double Progress { get; internal set; }
        public string Message { get; internal set; }
        public string Error { get; internal set; }
        public JToken Result { get; internal set; }
        public JObject Input { get; internal set; }
        public DateTimeOffset CreatedUtc { get; internal set; }
        public DateTimeOffset? StartedUtc { get; internal set; }
        public DateTimeOffset? CompletedUtc { get; internal set; }
        public bool CanCancel { get; internal set; } = true;

        public JObject ToJson()
        {
            lock (Sync)
            {
                return new JObject
                {
                    ["jobId"] = Id,
                    ["kind"] = Kind,
                    ["status"] = Status.ToString(),
                    ["progress"] = Progress,
                    ["message"] = Message,
                    ["error"] = Error,
                    ["result"] = Result?.DeepClone(),
                    ["input"] = Input?.DeepClone(),
                    ["createdUtc"] = CreatedUtc.ToString("O"),
                    ["startedUtc"] = StartedUtc?.ToString("O"),
                    ["completedUtc"] = CompletedUtc?.ToString("O"),
                    ["canCancel"] = CanCancel && !IsTerminal(Status),
                };
            }
        }

        private static bool IsTerminal(ToolJobStatus status) =>
            status == ToolJobStatus.Completed || status == ToolJobStatus.Failed || status == ToolJobStatus.Cancelled;
    }

    public static class ToolJobManager
    {
        private const int MaxRetainedJobs = 200;
        private static readonly ConcurrentDictionary<string, ToolJobRecord> Jobs =
            new ConcurrentDictionary<string, ToolJobRecord>(StringComparer.Ordinal);

        public static ToolJobRecord Create(string kind, JObject input = null, bool canCancel = true)
        {
            Prune();
            var record = new ToolJobRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = string.IsNullOrEmpty(kind) ? "tool" : kind,
                Status = ToolJobStatus.Queued,
                Input = input != null ? (JObject)input.DeepClone() : new JObject(),
                CreatedUtc = DateTimeOffset.UtcNow,
                Message = "queued",
                CanCancel = canCancel,
            };
            Jobs[record.Id] = record;
            return record;
        }

        public static void Start(
            ToolJobRecord record,
            Func<ToolJobRecord, CancellationToken, Task<JToken>> operation)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _ = RunAsync(record, operation);
        }

        public static bool TryGet(string jobId, out ToolJobRecord record) =>
            Jobs.TryGetValue(jobId ?? string.Empty, out record);

        public static bool Cancel(string jobId, out ToolJobRecord record)
        {
            if (!TryGet(jobId, out record)) return false;
            lock (record.Sync)
            {
                if (!record.CanCancel || IsTerminal(record.Status)) return false;
                record.Status = ToolJobStatus.CancelRequested;
                record.Message = "cancellation requested";
                record.Cancellation.Cancel();
                return true;
            }
        }

        public static JArray List(string kind = null, int limit = 50)
        {
            var array = new JArray();
            var records = Jobs.Values;
            var ordered = new System.Collections.Generic.List<ToolJobRecord>(records);
            ordered.Sort((left, right) => right.CreatedUtc.CompareTo(left.CreatedUtc));
            foreach (var record in ordered)
            {
                if (!string.IsNullOrEmpty(kind) && !string.Equals(record.Kind, kind, StringComparison.Ordinal)) continue;
                array.Add(record.ToJson());
                if (array.Count >= Math.Max(1, limit)) break;
            }
            return array;
        }

        public static void Report(ToolJobRecord record, double progress, string message)
        {
            if (record == null) return;
            lock (record.Sync)
            {
                record.Progress = Math.Max(0d, Math.Min(1d, progress));
                if (!string.IsNullOrEmpty(message)) record.Message = message;
            }
        }

        private static async Task RunAsync(
            ToolJobRecord record,
            Func<ToolJobRecord, CancellationToken, Task<JToken>> operation)
        {
            lock (record.Sync)
            {
                if (record.Status == ToolJobStatus.CancelRequested)
                {
                    record.Status = ToolJobStatus.Cancelled;
                    record.CompletedUtc = DateTimeOffset.UtcNow;
                    return;
                }
                record.Status = ToolJobStatus.Running;
                record.StartedUtc = DateTimeOffset.UtcNow;
                record.Message = "running";
            }

            try
            {
                var result = await operation(record, record.Cancellation.Token);
                lock (record.Sync)
                {
                    record.Result = result;
                    record.Progress = 1d;
                    record.Status = record.Cancellation.IsCancellationRequested
                        ? ToolJobStatus.Cancelled
                        : ToolJobStatus.Completed;
                    record.Message = record.Status == ToolJobStatus.Completed ? "completed" : "cancelled";
                    record.CompletedUtc = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                lock (record.Sync)
                {
                    record.Status = ToolJobStatus.Cancelled;
                    record.Message = "cancelled";
                    record.CompletedUtc = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception exception)
            {
                lock (record.Sync)
                {
                    record.Status = ToolJobStatus.Failed;
                    record.Error = $"{exception.GetType().Name}: {exception.Message}";
                    record.Message = "failed";
                    record.CompletedUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        private static bool IsTerminal(ToolJobStatus status) =>
            status == ToolJobStatus.Completed || status == ToolJobStatus.Failed || status == ToolJobStatus.Cancelled;

        private static void Prune()
        {
            if (Jobs.Count < MaxRetainedJobs) return;
            var records = new System.Collections.Generic.List<ToolJobRecord>(Jobs.Values);
            records.Sort((left, right) => left.CreatedUtc.CompareTo(right.CreatedUtc));
            foreach (var record in records)
            {
                if (Jobs.Count < MaxRetainedJobs) break;
                lock (record.Sync)
                {
                    if (!IsTerminal(record.Status)) continue;
                }
                Jobs.TryRemove(record.Id, out _);
                record.Cancellation.Dispose();
            }
        }
    }
}
