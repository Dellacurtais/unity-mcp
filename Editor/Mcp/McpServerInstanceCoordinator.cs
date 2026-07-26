using System;
using Process = System.Diagnostics.Process;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    internal enum McpServerCoordinationCommandKind
    {
        ReleaseForHandover,
        AcquireAfterHandover,
        RestoreAfterFailedHandover,
    }

    internal sealed class McpServerCoordinationCommand
    {
        public McpServerCoordinationCommandKind Kind;
        public string RequestId;
        public string SourceInstanceId;
        public string SourceProjectName;
        public string DestinationInstanceId;
        public string DestinationProjectName;
        public int Port;
    }

    internal sealed class McpServerAcquireResult
    {
        public bool Acquired;
        public string LeaseId;
        public int Port;
        public string Message;

        public static McpServerAcquireResult Success(string leaseId, int port)
            => new McpServerAcquireResult { Acquired = true, LeaseId = leaseId, Port = port };

        public static McpServerAcquireResult Rejected(int port, string message)
            => new McpServerAcquireResult { Port = port, Message = message ?? "MCP server is unavailable." };
    }

    internal sealed class McpServerInstanceSnapshot
    {
        public static readonly McpServerInstanceSnapshot Empty = new McpServerInstanceSnapshot();

        public bool HasOwner;
        public bool IsLocalOwner;
        public bool HasRemoteOwner => HasOwner && !IsLocalOwner;
        public bool IsTransitioning;
        public bool CanBringHere;
        public string OwnerInstanceId;
        public string OwnerProjectPath;
        public string OwnerProjectName;
        public string LeaseStatus;
        public int Port;
        public string HandoverStatus;
        public string HandoverMessage;
        public string Error;

        public bool SemanticallyEquals(McpServerInstanceSnapshot other)
        {
            if (other == null) return false;
            return HasOwner == other.HasOwner
                && IsLocalOwner == other.IsLocalOwner
                && IsTransitioning == other.IsTransitioning
                && CanBringHere == other.CanBringHere
                && string.Equals(OwnerInstanceId, other.OwnerInstanceId, StringComparison.Ordinal)
                && string.Equals(OwnerProjectPath, other.OwnerProjectPath, StringComparison.Ordinal)
                && string.Equals(OwnerProjectName, other.OwnerProjectName, StringComparison.Ordinal)
                && string.Equals(LeaseStatus, other.LeaseStatus, StringComparison.Ordinal)
                && Port == other.Port
                && string.Equals(HandoverStatus, other.HandoverStatus, StringComparison.Ordinal)
                && string.Equals(HandoverMessage, other.HandoverMessage, StringComparison.Ordinal)
                && string.Equals(Error, other.Error, StringComparison.Ordinal);
        }
    }

    internal sealed class McpServerInstanceCoordinatorOptions
    {
        public string RootPath;
        public string InstanceId;
        public int ProcessId;
        public string ProjectPath;
        public string ProjectName;
        public string PackageVersion;
        public Func<DateTimeOffset> UtcNow;
        public Func<int, bool> IsProcessAlive;
        public bool SubscribeToEditorUpdate = true;
        public TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
        public TimeSpan StaleAfter = TimeSpan.FromSeconds(12);
        public TimeSpan HandoverTimeout = TimeSpan.FromSeconds(12);
        public int LockTimeoutMilliseconds = 1000;
    }

    /// <summary>
    /// Coordinates the single loopback MCP endpoint across independent Unity Editor
    /// processes. Coordination uses per-user files and never stores the bearer token.
    /// </summary>
    internal sealed class McpServerInstanceCoordinator : IDisposable
    {
        private const int FormatVersion = 1;
        private const string Requested = "requested";
        private const string Released = "released";
        private const string Completed = "completed";
        private const string Failed = "failed";
        private const string Reserved = "reserved";
        private const string Running = "running";
        private const string Reloading = "reloading";

        private sealed class PresenceRecord
        {
            public int Version = FormatVersion;
            public string InstanceId;
            public int ProcessId;
            public string ProjectPath;
            public string ProjectName;
            public string PackageVersion;
            public long HeartbeatUtcTicks;
        }

        private sealed class LeaseRecord
        {
            public string LeaseId;
            public string OwnerInstanceId;
            public int OwnerProcessId;
            public string OwnerProjectPath;
            public string OwnerProjectName;
            public int Port;
            public string TokenFingerprint;
            public string Status;
            public long UpdatedUtcTicks;
        }

        private sealed class HandoverRecord
        {
            public string RequestId;
            public string SourceLeaseId;
            public string SourceInstanceId;
            public int SourceProcessId;
            public string SourceProjectPath;
            public string SourceProjectName;
            public string DestinationInstanceId;
            public int DestinationProcessId;
            public string DestinationProjectPath;
            public string DestinationProjectName;
            public int Port;
            public string TokenFingerprint;
            public string Status;
            public string Message;
            public long UpdatedUtcTicks;
        }

        private sealed class CoordinationState
        {
            public int Version = FormatVersion;
            public string PreferredInstanceId;
            public LeaseRecord Lease;
            public HandoverRecord Handover;
        }

        private readonly McpServerInstanceCoordinatorOptions _options;
        private readonly string _rootPath;
        private readonly string _statePath;
        private readonly string _lockPath;
        private readonly string _presencePath;
        private DateTimeOffset _nextHeartbeatUtc;
        private string _lastCommandKey;
        private string _lastWarning;
        private bool _disposed;

        public string InstanceId => _options.InstanceId;
        public string ProjectPath => _options.ProjectPath;
        public McpServerInstanceSnapshot Snapshot { get; private set; } = McpServerInstanceSnapshot.Empty;

        public event Action<McpServerInstanceSnapshot> StateChanged;
        public event Action<McpServerCoordinationCommand> CommandRequested;

        public static McpServerInstanceCoordinator CreateForCurrentProject(string projectPath, string packageVersion)
        {
            const string sessionKey = "Maicon.SceneAnalyzer.McpServerInstanceId";
            var instanceId = SessionState.GetString(sessionKey, string.Empty);
            if (string.IsNullOrEmpty(instanceId))
            {
                instanceId = Guid.NewGuid().ToString("N");
                SessionState.SetString(sessionKey, instanceId);
            }

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Maicon", "SceneAnalyzer", "coordination", "v1");
            var normalizedProject = NormalizePath(projectPath);
            return new McpServerInstanceCoordinator(new McpServerInstanceCoordinatorOptions
            {
                RootPath = root,
                InstanceId = instanceId,
                ProcessId = Process.GetCurrentProcess().Id,
                ProjectPath = normalizedProject,
                ProjectName = ProjectNameFromPath(normalizedProject),
                PackageVersion = packageVersion ?? string.Empty,
            });
        }

        internal McpServerInstanceCoordinator(McpServerInstanceCoordinatorOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.RootPath)) throw new ArgumentException("Coordination root is required.", nameof(options));
            if (string.IsNullOrWhiteSpace(options.InstanceId)) throw new ArgumentException("Instance id is required.", nameof(options));
            if (options.ProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Process id must be positive.");
            if (string.IsNullOrWhiteSpace(options.ProjectPath)) throw new ArgumentException("Project path is required.", nameof(options));

            options.ProjectPath = NormalizePath(options.ProjectPath);
            options.ProjectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? ProjectNameFromPath(options.ProjectPath)
                : options.ProjectName;
            options.PackageVersion = options.PackageVersion ?? string.Empty;
            options.UtcNow ??= () => DateTimeOffset.UtcNow;
            options.IsProcessAlive ??= DefaultIsProcessAlive;
            if (options.HeartbeatInterval <= TimeSpan.Zero) options.HeartbeatInterval = TimeSpan.FromSeconds(1);
            if (options.StaleAfter <= options.HeartbeatInterval) options.StaleAfter = TimeSpan.FromSeconds(12);
            if (options.HandoverTimeout <= TimeSpan.Zero) options.HandoverTimeout = TimeSpan.FromSeconds(12);
            if (options.LockTimeoutMilliseconds < 50) options.LockTimeoutMilliseconds = 50;

            _rootPath = Path.GetFullPath(options.RootPath);
            _statePath = Path.Combine(_rootPath, "state.json");
            _lockPath = Path.Combine(_rootPath, "coordination.lock");
            _presencePath = PresencePath(options.InstanceId);
            Directory.CreateDirectory(_rootPath);

            if (options.SubscribeToEditorUpdate)
                EditorApplication.update += OnEditorUpdate;

            PollNow();
        }

        public McpServerAcquireResult TryAcquire(int requestedPort, string token)
        {
            ThrowIfDisposed();
            ValidatePortAndToken(requestedPort, token);
            WritePresence(_options.UtcNow());

            McpServerAcquireResult result;
            McpServerInstanceSnapshot snapshot;
            using (AcquireLock())
            {
                var now = _options.UtcNow();
                var state = ReadState();
                CleanupState(state, now);

                var handover = state.Handover;
                if (handover != null && IsActiveHandover(handover.Status))
                {
                    var thisInstanceIsSource = string.Equals(handover.SourceInstanceId, InstanceId, StringComparison.Ordinal);
                    result = McpServerAcquireResult.Rejected(
                        handover.Port,
                        thisInstanceIsSource
                            ? $"MCP server transfer to '{handover.DestinationProjectName}' is pending."
                            : $"MCP server transfer from '{handover.SourceProjectName}' to '{handover.DestinationProjectName}' is pending.");
                    snapshot = BuildSnapshot(state, null);
                }
                else if (state.Lease != null && !string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal))
                {
                    result = McpServerAcquireResult.Rejected(
                        state.Lease.Port,
                        $"MCP server is active in '{state.Lease.OwnerProjectName}' on port {state.Lease.Port}. Use Bring here to transfer it.");
                    snapshot = BuildSnapshot(state, null);
                }
                else
                {
                    var lease = state.Lease;
                    if (lease == null)
                    {
                        lease = NewLease(requestedPort, token, now);
                        state.Lease = lease;
                    }
                    else
                    {
                        lease.Port = requestedPort;
                        lease.TokenFingerprint = Fingerprint(token);
                        lease.Status = Reserved;
                        lease.UpdatedUtcTicks = now.UtcDateTime.Ticks;
                    }

                    state.PreferredInstanceId = InstanceId;
                    if (state.Handover != null && IsTerminalHandover(state.Handover.Status))
                        state.Handover = null;
                    WriteState(state);
                    result = McpServerAcquireResult.Success(lease.LeaseId, lease.Port);
                    snapshot = BuildSnapshot(state, null);
                }
            }

            ApplySnapshot(snapshot);
            return result;
        }

        public McpServerAcquireResult TryAcquireHandover(string requestId, string token)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("Request id is required.", nameof(requestId));
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Bearer token is required.");
            WritePresence(_options.UtcNow());

            McpServerAcquireResult result;
            McpServerInstanceSnapshot snapshot;
            using (AcquireLock())
            {
                var now = _options.UtcNow();
                var state = ReadState();
                CleanupState(state, now);
                var handover = state.Handover;

                if (handover == null
                    || !string.Equals(handover.RequestId, requestId, StringComparison.Ordinal)
                    || !string.Equals(handover.DestinationInstanceId, InstanceId, StringComparison.Ordinal)
                    || !string.Equals(handover.Status, Released, StringComparison.Ordinal))
                {
                    result = McpServerAcquireResult.Rejected(handover?.Port ?? 0, "MCP server transfer is no longer ready for this project.");
                }
                else if (state.Lease != null)
                {
                    result = McpServerAcquireResult.Rejected(state.Lease.Port, $"Port ownership was already claimed by '{state.Lease.OwnerProjectName}'.");
                }
                else if (!string.Equals(handover.TokenFingerprint, Fingerprint(token), StringComparison.Ordinal))
                {
                    FailHandoverInState(state, handover, "Bearer token changed during transfer.", now);
                    WriteState(state);
                    result = McpServerAcquireResult.Rejected(handover.Port, handover.Message);
                }
                else
                {
                    var lease = NewLease(handover.Port, token, now);
                    state.Lease = lease;
                    state.PreferredInstanceId = InstanceId;
                    WriteState(state);
                    result = McpServerAcquireResult.Success(lease.LeaseId, lease.Port);
                }

                snapshot = BuildSnapshot(state, null);
            }

            ApplySnapshot(snapshot);
            return result;
        }

        public void MarkRunning(string leaseId, string handoverRequestId = null)
        {
            ThrowIfDisposed();
            MutateState((state, now) =>
            {
                RequireLocalLease(state, leaseId);
                state.Lease.Status = Running;
                state.Lease.UpdatedUtcTicks = now.UtcDateTime.Ticks;
                state.PreferredInstanceId = InstanceId;

                if (!string.IsNullOrEmpty(handoverRequestId)
                    && state.Handover != null
                    && string.Equals(state.Handover.RequestId, handoverRequestId, StringComparison.Ordinal)
                    && string.Equals(state.Handover.DestinationInstanceId, InstanceId, StringComparison.Ordinal))
                {
                    state.Handover.Status = Completed;
                    state.Handover.Message = $"MCP server moved to '{_options.ProjectName}'.";
                    state.Handover.UpdatedUtcTicks = now.UtcDateTime.Ticks;
                }
            });
        }

        public void FailStart(string leaseId, string message, string handoverRequestId = null)
        {
            if (_disposed) return;
            MutateState((state, now) =>
            {
                if (state.Lease != null
                    && string.Equals(state.Lease.LeaseId, leaseId, StringComparison.Ordinal)
                    && string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal))
                    state.Lease = null;

                if (!string.IsNullOrEmpty(handoverRequestId)
                    && state.Handover != null
                    && string.Equals(state.Handover.RequestId, handoverRequestId, StringComparison.Ordinal))
                    FailHandoverInState(state, state.Handover, message, now);
                else if (string.Equals(state.PreferredInstanceId, InstanceId, StringComparison.Ordinal))
                    state.PreferredInstanceId = null;
            });
        }

        public string RequestHandover()
        {
            ThrowIfDisposed();
            WritePresence(_options.UtcNow());
            string requestId = null;
            MutateState((state, now) =>
            {
                if (state.Lease == null)
                    throw new InvalidOperationException("No active MCP server was found.");
                if (string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal))
                    throw new InvalidOperationException("This project already owns the MCP server.");

                if (state.Handover != null && IsActiveHandover(state.Handover.Status))
                {
                    if (string.Equals(state.Handover.DestinationInstanceId, InstanceId, StringComparison.Ordinal))
                    {
                        requestId = state.Handover.RequestId;
                        return;
                    }
                    throw new InvalidOperationException(
                        $"A transfer to '{state.Handover.DestinationProjectName}' is already in progress.");
                }

                var lease = state.Lease;
                var ownPresence = CurrentPresence(now);
                state.Handover = new HandoverRecord
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    SourceLeaseId = lease.LeaseId,
                    SourceInstanceId = lease.OwnerInstanceId,
                    SourceProcessId = lease.OwnerProcessId,
                    SourceProjectPath = lease.OwnerProjectPath,
                    SourceProjectName = lease.OwnerProjectName,
                    DestinationInstanceId = InstanceId,
                    DestinationProcessId = _options.ProcessId,
                    DestinationProjectPath = _options.ProjectPath,
                    DestinationProjectName = _options.ProjectName,
                    Port = lease.Port,
                    TokenFingerprint = lease.TokenFingerprint,
                    Status = Requested,
                    Message = $"Transfer requested by '{_options.ProjectName}'.",
                    UpdatedUtcTicks = now.UtcDateTime.Ticks,
                };
                WritePresenceRecord(ownPresence);
                state.PreferredInstanceId = InstanceId;
                requestId = state.Handover.RequestId;
            });
            return requestId;
        }

        public void ReleaseForHandover(string requestId)
        {
            ThrowIfDisposed();
            MutateState((state, now) =>
            {
                var handover = RequireHandover(state, requestId, Requested);
                if (!string.Equals(handover.SourceInstanceId, InstanceId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Only the current owner can release this transfer.");
                if (state.Lease != null
                    && (!string.Equals(state.Lease.LeaseId, handover.SourceLeaseId, StringComparison.Ordinal)
                        || !string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal)))
                    throw new InvalidOperationException("The MCP server lease changed before transfer release.");

                state.Lease = null;
                state.PreferredInstanceId = handover.DestinationInstanceId;
                handover.Status = Released;
                handover.Message = $"Port {handover.Port} released for '{handover.DestinationProjectName}'.";
                handover.UpdatedUtcTicks = now.UtcDateTime.Ticks;
            });
        }

        public void FailHandover(string requestId, string message)
        {
            if (_disposed) return;
            MutateState((state, now) =>
            {
                if (state.Handover == null || !string.Equals(state.Handover.RequestId, requestId, StringComparison.Ordinal))
                    return;
                if (state.Lease != null
                    && string.Equals(state.Lease.OwnerInstanceId, state.Handover.DestinationInstanceId, StringComparison.Ordinal))
                    state.Lease = null;
                FailHandoverInState(state, state.Handover, message, now);
            });
        }

        public void ReleaseExplicit()
        {
            if (_disposed) return;
            MutateState((state, now) =>
            {
                if (state.Lease != null && string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal))
                    state.Lease = null;
                if (state.Handover != null
                    && (string.Equals(state.Handover.SourceInstanceId, InstanceId, StringComparison.Ordinal)
                        || string.Equals(state.Handover.DestinationInstanceId, InstanceId, StringComparison.Ordinal)))
                    state.Handover = null;
                state.PreferredInstanceId = null;
            });
        }

        public void PrepareForReload()
        {
            if (_disposed) return;
            MutateState((state, now) =>
            {
                if (state.Lease != null && string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal))
                {
                    state.Lease.Status = Reloading;
                    state.Lease.UpdatedUtcTicks = now.UtcDateTime.Ticks;
                }
            });
        }

        public void ReleaseForShutdown()
        {
            if (_disposed) return;
            MutateState((state, now) =>
            {
                if (state.Handover != null
                    && string.Equals(state.Handover.SourceInstanceId, InstanceId, StringComparison.Ordinal)
                    && string.Equals(state.Handover.Status, Requested, StringComparison.Ordinal))
                {
                    state.Lease = null;
                    state.PreferredInstanceId = state.Handover.DestinationInstanceId;
                    state.Handover.Status = Released;
                    state.Handover.Message = "Owner exited while completing the transfer.";
                    state.Handover.UpdatedUtcTicks = now.UtcDateTime.Ticks;
                    return;
                }

                if (state.Lease != null && string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal))
                    state.Lease = null;
                if (string.Equals(state.PreferredInstanceId, InstanceId, StringComparison.Ordinal))
                    state.PreferredInstanceId = null;
            });
        }

        public void PollNow()
        {
            if (_disposed) return;
            McpServerInstanceSnapshot snapshot;
            McpServerCoordinationCommand command;
            try
            {
                var now = _options.UtcNow();
                WritePresence(now);
                using (AcquireLock())
                {
                    var state = ReadState();
                    var dirty = CleanupState(state, now);

                    if (state.Lease != null && string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal))
                    {
                        state.Lease.UpdatedUtcTicks = now.UtcDateTime.Ticks;
                        dirty = true;
                    }

                    dirty |= EvaluateParticipants(state, now);
                    dirty |= EvaluateTimeouts(state, now);
                    if (dirty) WriteState(state);
                    snapshot = BuildSnapshot(state, null);
                    command = BuildCommand(state);
                }
                _lastWarning = null;
            }
            catch (Exception exception)
            {
                snapshot = CopySnapshotWithError(Snapshot, exception.Message);
                command = null;
                WarnOnce(exception.Message);
            }

            ApplySnapshot(snapshot);
            DispatchCommand(command);
        }

        private void OnEditorUpdate()
        {
            var now = _options.UtcNow();
            if (now < _nextHeartbeatUtc) return;
            _nextHeartbeatUtc = now + _options.HeartbeatInterval;
            PollNow();
        }

        private void MutateState(Action<CoordinationState, DateTimeOffset> mutation)
        {
            McpServerInstanceSnapshot snapshot;
            using (AcquireLock())
            {
                var now = _options.UtcNow();
                WritePresence(now);
                var state = ReadState();
                CleanupState(state, now);
                mutation(state, now);
                WriteState(state);
                snapshot = BuildSnapshot(state, null);
            }
            ApplySnapshot(snapshot);
        }

        private bool CleanupState(CoordinationState state, DateTimeOffset now)
        {
            var dirty = false;
            foreach (var path in Directory.GetFiles(_rootPath, "presence-*.json"))
            {
                var presence = ReadPresence(path);
                if (presence == null)
                {
                    TryDelete(path);
                    continue;
                }

                if (IsExpired(presence.HeartbeatUtcTicks, _options.StaleAfter, now)
                    && !_options.IsProcessAlive(presence.ProcessId))
                {
                    TryDelete(path);
                    dirty = true;
                }
            }

            if (state.Lease != null
                && IsExpired(state.Lease.UpdatedUtcTicks, _options.StaleAfter, now)
                && !_options.IsProcessAlive(state.Lease.OwnerProcessId))
            {
                if (state.Handover != null
                    && string.Equals(state.Handover.Status, Requested, StringComparison.Ordinal)
                    && string.Equals(state.Handover.SourceInstanceId, state.Lease.OwnerInstanceId, StringComparison.Ordinal)
                    && IsInstanceAlive(state.Handover.DestinationInstanceId, state.Handover.DestinationProcessId, now))
                {
                    state.Handover.Status = Released;
                    state.Handover.Message = "Previous owner exited; stale lease released for transfer.";
                    state.Handover.UpdatedUtcTicks = now.UtcDateTime.Ticks;
                }
                state.Lease = null;
                dirty = true;
            }

            if (state.Handover != null
                && IsTerminalHandover(state.Handover.Status)
                && IsExpired(state.Handover.UpdatedUtcTicks, TimeSpan.FromMinutes(1), now))
            {
                state.Handover = null;
                dirty = true;
            }

            return dirty;
        }

        private bool EvaluateParticipants(CoordinationState state, DateTimeOffset now)
        {
            var handover = state.Handover;
            if (handover == null || !IsActiveHandover(handover.Status)) return false;
            if (HasFreshPresence(handover.DestinationInstanceId, handover.DestinationProcessId, now)) return false;

            if (state.Lease != null
                && string.Equals(state.Lease.OwnerInstanceId, handover.DestinationInstanceId, StringComparison.Ordinal))
                state.Lease = null;
            FailHandoverInState(state, handover, "Destination project is no longer available for transfer.", now);
            return true;
        }

        private bool EvaluateTimeouts(CoordinationState state, DateTimeOffset now)
        {
            var handover = state.Handover;
            if (handover == null || !IsExpired(handover.UpdatedUtcTicks, _options.HandoverTimeout, now)) return false;

            if (string.Equals(handover.Status, Requested, StringComparison.Ordinal))
            {
                if (!IsInstanceAlive(handover.SourceInstanceId, handover.SourceProcessId, now))
                {
                    state.Lease = null;
                    state.PreferredInstanceId = handover.DestinationInstanceId;
                    handover.Status = Released;
                    handover.Message = "Previous owner exited; port released for transfer.";
                }
                else
                {
                    FailHandoverInState(state, handover, "Current owner did not answer the transfer request in time.", now);
                    return true;
                }
                handover.UpdatedUtcTicks = now.UtcDateTime.Ticks;
                return true;
            }

            if (string.Equals(handover.Status, Released, StringComparison.Ordinal))
            {
                if (state.Lease != null && string.Equals(state.Lease.OwnerInstanceId, handover.DestinationInstanceId, StringComparison.Ordinal))
                    state.Lease = null;
                FailHandoverInState(state, handover, "Destination did not start the MCP server in time.", now);
                return true;
            }

            return false;
        }

        private McpServerCoordinationCommand BuildCommand(CoordinationState state)
        {
            var handover = state.Handover;
            if (handover == null) return null;

            McpServerCoordinationCommandKind kind;
            if (string.Equals(handover.Status, Requested, StringComparison.Ordinal)
                && string.Equals(handover.SourceInstanceId, InstanceId, StringComparison.Ordinal))
                kind = McpServerCoordinationCommandKind.ReleaseForHandover;
            else if (string.Equals(handover.Status, Released, StringComparison.Ordinal)
                && string.Equals(handover.DestinationInstanceId, InstanceId, StringComparison.Ordinal))
                kind = McpServerCoordinationCommandKind.AcquireAfterHandover;
            else if (string.Equals(handover.Status, Failed, StringComparison.Ordinal)
                && string.Equals(handover.SourceInstanceId, InstanceId, StringComparison.Ordinal)
                && state.Lease == null)
                kind = McpServerCoordinationCommandKind.RestoreAfterFailedHandover;
            else
                return null;

            return new McpServerCoordinationCommand
            {
                Kind = kind,
                RequestId = handover.RequestId,
                SourceInstanceId = handover.SourceInstanceId,
                SourceProjectName = handover.SourceProjectName,
                DestinationInstanceId = handover.DestinationInstanceId,
                DestinationProjectName = handover.DestinationProjectName,
                Port = handover.Port,
            };
        }

        private void DispatchCommand(McpServerCoordinationCommand command)
        {
            if (command == null)
            {
                _lastCommandKey = null;
                return;
            }

            var handler = CommandRequested;
            if (handler == null)
            {
                _lastCommandKey = null;
                return;
            }

            var key = $"{command.RequestId}:{command.Kind}";
            if (string.Equals(key, _lastCommandKey, StringComparison.Ordinal)) return;
            _lastCommandKey = key;
            try { handler(command); }
            catch (Exception exception)
            {
                _lastCommandKey = null;
                Debug.LogException(exception);
            }
        }

        private LeaseRecord NewLease(int port, string token, DateTimeOffset now)
        {
            return new LeaseRecord
            {
                LeaseId = Guid.NewGuid().ToString("N"),
                OwnerInstanceId = InstanceId,
                OwnerProcessId = _options.ProcessId,
                OwnerProjectPath = _options.ProjectPath,
                OwnerProjectName = _options.ProjectName,
                Port = port,
                TokenFingerprint = Fingerprint(token),
                Status = Reserved,
                UpdatedUtcTicks = now.UtcDateTime.Ticks,
            };
        }

        private static void FailHandoverInState(
            CoordinationState state, HandoverRecord handover, string message, DateTimeOffset now)
        {
            handover.Status = Failed;
            handover.Message = string.IsNullOrWhiteSpace(message) ? "MCP server transfer failed." : message;
            handover.UpdatedUtcTicks = now.UtcDateTime.Ticks;
            state.PreferredInstanceId = handover.SourceInstanceId;
        }

        private static HandoverRecord RequireHandover(CoordinationState state, string requestId, string status)
        {
            var handover = state.Handover;
            if (handover == null
                || !string.Equals(handover.RequestId, requestId, StringComparison.Ordinal)
                || !string.Equals(handover.Status, status, StringComparison.Ordinal))
                throw new InvalidOperationException("MCP server transfer state changed; retry from the current owner state.");
            return handover;
        }

        private void RequireLocalLease(CoordinationState state, string leaseId)
        {
            if (state.Lease == null
                || !string.Equals(state.Lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(state.Lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException("MCP server lease was lost before startup completed.");
        }

        private McpServerInstanceSnapshot BuildSnapshot(CoordinationState state, string error)
        {
            var lease = state.Lease;
            var handover = state.Handover;
            var activeTransfer = handover != null && IsActiveHandover(handover.Status);
            return new McpServerInstanceSnapshot
            {
                HasOwner = lease != null,
                IsLocalOwner = lease != null && string.Equals(lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal),
                IsTransitioning = activeTransfer,
                CanBringHere = lease != null
                    && !string.Equals(lease.OwnerInstanceId, InstanceId, StringComparison.Ordinal)
                    && !activeTransfer,
                OwnerInstanceId = lease?.OwnerInstanceId,
                OwnerProjectPath = lease?.OwnerProjectPath,
                OwnerProjectName = lease?.OwnerProjectName,
                LeaseStatus = lease?.Status,
                Port = lease?.Port ?? handover?.Port ?? 0,
                HandoverStatus = handover?.Status,
                HandoverMessage = handover?.Message,
                Error = error,
            };
        }

        private static McpServerInstanceSnapshot CopySnapshotWithError(McpServerInstanceSnapshot source, string error)
        {
            source ??= McpServerInstanceSnapshot.Empty;
            return new McpServerInstanceSnapshot
            {
                HasOwner = source.HasOwner,
                IsLocalOwner = source.IsLocalOwner,
                IsTransitioning = source.IsTransitioning,
                CanBringHere = source.CanBringHere,
                OwnerInstanceId = source.OwnerInstanceId,
                OwnerProjectPath = source.OwnerProjectPath,
                OwnerProjectName = source.OwnerProjectName,
                LeaseStatus = source.LeaseStatus,
                Port = source.Port,
                HandoverStatus = source.HandoverStatus,
                HandoverMessage = source.HandoverMessage,
                Error = error,
            };
        }

        private void ApplySnapshot(McpServerInstanceSnapshot snapshot)
        {
            snapshot ??= McpServerInstanceSnapshot.Empty;
            var changed = !snapshot.SemanticallyEquals(Snapshot);
            Snapshot = snapshot;
            if (!changed) return;
            try { StateChanged?.Invoke(snapshot); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private PresenceRecord CurrentPresence(DateTimeOffset now)
        {
            return new PresenceRecord
            {
                InstanceId = InstanceId,
                ProcessId = _options.ProcessId,
                ProjectPath = _options.ProjectPath,
                ProjectName = _options.ProjectName,
                PackageVersion = _options.PackageVersion,
                HeartbeatUtcTicks = now.UtcDateTime.Ticks,
            };
        }

        private void WritePresence(DateTimeOffset now) => WritePresenceRecord(CurrentPresence(now));

        private void WritePresenceRecord(PresenceRecord presence)
        {
            Directory.CreateDirectory(_rootPath);
            WriteJsonAtomic(_presencePath, presence);
        }

        private PresenceRecord ReadPresence(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<PresenceRecord>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { return null; }
        }

        private bool HasFreshPresence(string instanceId, int processId, DateTimeOffset now)
        {
            var presence = ReadPresence(PresencePath(instanceId));
            return presence != null
                && presence.ProcessId == processId
                && string.Equals(presence.InstanceId, instanceId, StringComparison.Ordinal)
                && !IsExpired(presence.HeartbeatUtcTicks, _options.StaleAfter, now);
        }

        private bool IsInstanceAlive(string instanceId, int processId, DateTimeOffset now)
        {
            return HasFreshPresence(instanceId, processId, now) || _options.IsProcessAlive(processId);
        }

        private CoordinationState ReadState()
        {
            if (!File.Exists(_statePath)) return new CoordinationState();

            CoordinationState state;
            try
            {
                state = JsonConvert.DeserializeObject<CoordinationState>(File.ReadAllText(_statePath, Encoding.UTF8));
            }
            catch (JsonException)
            {
                QuarantineCorruptState();
                return new CoordinationState();
            }

            if (state == null)
            {
                QuarantineCorruptState();
                return new CoordinationState();
            }
            if (state.Version != FormatVersion)
                throw new InvalidDataException(
                    $"Unsupported MCP coordination state version {state.Version}; expected {FormatVersion}. " +
                    "Upgrade all open Unity projects before starting the server.");
            return state;
        }

        private void QuarantineCorruptState()
        {
            var corruptPath = _statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(_statePath, corruptPath); }
            catch { TryDelete(_statePath); }
        }

        private void WriteState(CoordinationState state) => WriteJsonAtomic(_statePath, state);

        private void WriteJsonAtomic(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp-" + InstanceId + "-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.None), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(path);
                        File.Move(temporary, path);
                    }
                    catch (IOException)
                    {
                        File.Delete(path);
                        File.Move(temporary, path);
                    }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        private FileStream AcquireLock()
        {
            Directory.CreateDirectory(_rootPath);
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (stopwatch.ElapsedMilliseconds >= _options.LockTimeoutMilliseconds)
                        throw new TimeoutException("Timed out waiting for the MCP server coordination lock.");
                    Thread.Sleep(10);
                }
            }
        }

        private string PresencePath(string instanceId)
        {
            var safe = new string((instanceId ?? string.Empty)
                .Where(character => char.IsLetterOrDigit(character) || character == '-' || character == '_')
                .ToArray());
            if (string.IsNullOrEmpty(safe)) safe = "unknown";
            return Path.Combine(_rootPath, "presence-" + safe + ".json");
        }

        private void ValidatePortAndToken(int port, string token)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Bearer token is required.");
        }

        private static string Fingerprint(string token)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? string.Empty));
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool IsExpired(long utcTicks, TimeSpan threshold, DateTimeOffset now)
        {
            if (utcTicks <= 0) return true;
            return now - new DateTimeOffset(utcTicks, TimeSpan.Zero) > threshold;
        }

        private static bool IsActiveHandover(string status)
            => string.Equals(status, Requested, StringComparison.Ordinal)
               || string.Equals(status, Released, StringComparison.Ordinal);

        private static bool IsTerminalHandover(string status)
            => string.Equals(status, Completed, StringComparison.Ordinal)
               || string.Equals(status, Failed, StringComparison.Ordinal);

        private static bool DefaultIsProcessAlive(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch { return false; }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { path = Path.GetFullPath(path); } catch { }
            return path.Replace('\\', '/').TrimEnd('/');
        }

        private static string ProjectNameFromPath(string path)
        {
            try { return Path.GetFileName((path ?? string.Empty).TrimEnd('/', '\\')); }
            catch { return path ?? string.Empty; }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private void WarnOnce(string message)
        {
            if (string.Equals(_lastWarning, message, StringComparison.Ordinal)) return;
            _lastWarning = message;
            Debug.LogWarning($"[unity-mcp] MCP instance coordination failed: {message}");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(McpServerInstanceCoordinator));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_options.SubscribeToEditorUpdate)
                EditorApplication.update -= OnEditorUpdate;
            TryDelete(_presencePath);
        }
    }
}
