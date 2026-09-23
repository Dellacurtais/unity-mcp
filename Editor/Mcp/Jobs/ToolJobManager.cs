using System;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

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

    public sealed class ToolJobFailureException : Exception
    {
        public ToolJobFailureException(string message, JToken result = null) : base(message)
        {
            Result = result;
        }

        public JToken Result { get; }
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
        private const int MaxPersistedJobs = 50;
        private const string PersistedJobsKey = "Maicon.SceneAnalyzer.ToolJobs.BuildPackages";
        private static readonly ConcurrentDictionary<string, ToolJobRecord> Jobs =
            new ConcurrentDictionary<string, ToolJobRecord>(StringComparer.Ordinal);
        private static readonly object CreateSync = new object();
        private static int _initialized;
        private static readonly object PersistSync = new object();
        private static string _journalPath;
        public static string PersistenceError { get; private set; }

        public static bool IsControlTool(string name) => name == "cancel_tool_job"
            || name == "cancel_build_job" || name == "cancel_reimport_job" || name == "cancel_test_job";

        private static bool UsesMutationResource(string kind) => kind == "build" || kind == "test"
            || kind == "asset_reimport" || kind == "addressables_build"
            || (kind != null && kind.StartsWith("package_", StringComparison.Ordinal));

        internal static void InitializeOnMainThread()
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
            try
            {
                _journalPath = Path.Combine(Path.GetDirectoryName(UnityEngine.Application.dataPath), "Library", "UnityMcp", "jobs.json");
                RestorePersistedJobs();
                AssemblyReloadEvents.beforeAssemblyReload -= PersistTrackedJobs;
                AssemblyReloadEvents.beforeAssemblyReload += PersistTrackedJobs;
            }
            catch
            {
                Volatile.Write(ref _initialized, 0);
                throw;
            }
        }

        public static ToolJobRecord Create(string kind, JObject input = null, bool canCancel = true)
        {
            lock (CreateSync) return CreateCore(kind, input, canCancel);
        }

        public static bool TryCreateExclusive(
            string kind, JObject input, bool canCancel, out ToolJobRecord record)
        {
            lock (CreateSync)
            {
                if (HasActive(kind)) { record = null; return false; }
                record = CreateCore(kind, input, canCancel);
                return true;
            }
        }

        private static ToolJobRecord CreateCore(string kind, JObject input, bool canCancel)
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
            PersistTrackedJobs();
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

        public static async Task<ToolJobRecord> WaitAsync(
            string jobId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return null;
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGet(jobId, out var record)) return null;
                lock (record.Sync)
                    if (IsTerminal(record.Status)) return record;
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return record;
                var delay = remaining < TimeSpan.FromMilliseconds(100)
                    ? remaining
                    : TimeSpan.FromMilliseconds(100);
                await Task.Delay(delay, cancellationToken);
            }
        }

        public static bool HasActive(string kind) => HasActivePrefix(kind, exact: true);

        public static bool HasActivePrefix(string kindPrefix, bool exact = false)
        {
            foreach (var record in Jobs.Values)
            {
                var matches = exact
                    ? string.Equals(record.Kind, kindPrefix, StringComparison.Ordinal)
                    : record.Kind.StartsWith(kindPrefix ?? string.Empty, StringComparison.Ordinal);
                if (!matches) continue;
                lock (record.Sync) if (!IsTerminal(record.Status)) return true;
            }
            return false;
        }

        public static bool Cancel(string jobId, out ToolJobRecord record)
        {
            if (!TryGet(jobId, out record)) return false;
            lock (record.Sync)
            {
                if (!record.CanCancel || IsTerminal(record.Status)) return false;
                record.Status = ToolJobStatus.CancelRequested;
                record.Message = "cancellation requested";
                record.Cancellation.Cancel();
            }
            PersistTrackedJobs();
            return true;
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
            bool mutationHeld = false;
            try
            {
                if (UsesMutationResource(record.Kind))
                {
                    lock (record.Sync) record.Message = "waiting for exclusive Editor mutation access";
                    await EditorMutationGate.Semaphore.WaitAsync(record.Cancellation.Token);
                    mutationHeld = true;
                }
                await ExecuteAsync(record, operation);
            }
            catch (OperationCanceledException)
            {
                lock (record.Sync)
                {
                    record.Status = ToolJobStatus.Cancelled;
                    record.Message = "cancelled before execution";
                    record.CompletedUtc = DateTimeOffset.UtcNow;
                }
            }
            finally
            {
                if (mutationHeld) EditorMutationGate.Semaphore.Release();
                PersistTrackedJobs();
            }
        }

        private static async Task ExecuteAsync(
            ToolJobRecord record,
            Func<ToolJobRecord, CancellationToken, Task<JToken>> operation)
        {
            var cancelledBeforeStart = false;
            lock (record.Sync)
            {
                if (record.Status == ToolJobStatus.CancelRequested)
                {
                    record.Status = ToolJobStatus.Cancelled;
                    record.Message = "cancelled";
                    record.CompletedUtc = DateTimeOffset.UtcNow;
                    cancelledBeforeStart = true;
                }
                else
                {
                    record.Status = ToolJobStatus.Running;
                    record.StartedUtc = DateTimeOffset.UtcNow;
                    record.Message = "running";
                }
            }
            if (cancelledBeforeStart)
            {
                PersistTrackedJobs();
                return;
            }

            try
            {
                var result = await operation(record, record.Cancellation.Token);
                lock (record.Sync)
                {
                    record.Result = result;
                    record.Progress = 1d;
                    // A returned result is known. A late cancellation cannot undo completed effects.
                    record.Status = ToolJobStatus.Completed;
                    record.Message = "completed";
                    record.CompletedUtc = DateTimeOffset.UtcNow;
                }
            }
            catch (ToolJobFailureException exception)
            {
                lock (record.Sync)
                {
                    record.Status = ToolJobStatus.Failed;
                    record.Error = exception.Message;
                    record.Result = exception.Result?.DeepClone();
                    record.Progress = 1d;
                    record.Message = "failed";
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
            finally
            {
                PersistTrackedJobs();
            }
        }

        private static bool ShouldPersist(ToolJobRecord record) => record != null;

        private static void PersistTrackedJobs()
        {
            if (string.IsNullOrEmpty(_journalPath)) return;
            lock (PersistSync)
            {
            try
            {
                var records = new System.Collections.Generic.List<ToolJobRecord>();
                foreach (var record in Jobs.Values) if (ShouldPersist(record)) records.Add(record);
                records.Sort((left, right) =>
                {
                    var terminal = IsTerminal(left.Status).CompareTo(IsTerminal(right.Status));
                    return terminal != 0 ? terminal : right.CreatedUtc.CompareTo(left.CreatedUtc);
                });
                var json = new JArray();
                foreach (var record in records)
                {
                    if (json.Count >= MaxPersistedJobs && IsTerminal(record.Status)) break;
                    json.Add(record.ToJson());
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_journalPath));
                var temporary = _journalPath + ".tmp";
                var bytes = System.Text.Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None));
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(_journalPath)) File.Replace(temporary, _journalPath, null);
                else File.Move(temporary, _journalPath);
                PersistenceError = null;
            }
            catch (Exception exception) { PersistenceError = exception.Message; }
            }
        }

        private static void RestorePersistedJobs()
        {
            try
            {
                var serialized = File.Exists(_journalPath) ? File.ReadAllText(_journalPath)
                    : SessionState.GetString(PersistedJobsKey, string.Empty);
                if (string.IsNullOrEmpty(serialized)) return;
                var now = DateTimeOffset.UtcNow;
                foreach (var item in JArray.Parse(serialized).OfType<JObject>())
                {
                    var id = (string)item["jobId"];
                    var kind = (string)item["kind"];
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(kind)) continue;
                    if (!Enum.TryParse((string)item["status"], out ToolJobStatus status)) status = ToolJobStatus.Failed;
                    var interrupted = !IsTerminal(status);
                    var record = new ToolJobRecord
                    {
                        Id = id,
                        Kind = kind,
                        Status = interrupted ? ToolJobStatus.Failed : status,
                        Progress = (double?)item["progress"] ?? 0d,
                        Message = interrupted ? "interrupted; verify effects before retrying" : (string)item["message"],
                        Error = interrupted ? InterruptionError(kind) : (string)item["error"],
                        Result = item["result"]?.DeepClone(),
                        Input = item["input"] as JObject != null ? (JObject)item["input"].DeepClone() : new JObject(),
                        CreatedUtc = DateTimeOffset.TryParse((string)item["createdUtc"], out var created) ? created : now,
                        StartedUtc = DateTimeOffset.TryParse((string)item["startedUtc"], out var started) ? started : (DateTimeOffset?)null,
                        CompletedUtc = interrupted ? now
                            : DateTimeOffset.TryParse((string)item["completedUtc"], out var completed) ? completed : (DateTimeOffset?)null,
                        CanCancel = false,
                    };
                    Jobs[id] = record;
                }
            }
            catch (Exception exception) { PersistenceError = exception.Message; }
        }

        private static string InterruptionError(string kind)
        {
            if (string.Equals(kind, "test", StringComparison.Ordinal))
                return "OUTCOME_UNKNOWN: Editor domain/process restarted; inspect Test Runner results before retrying";
            return "OUTCOME_UNKNOWN: Editor domain/process restarted; reconcile affected assets, packages or build reports before retrying";
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
