using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Broker
{
    public enum ManagedBrokerState
    {
        NotInstalled,
        LocatingPython,
        Installing,
        Installed,
        Starting,
        Running,
        Failed,
    }

    public sealed class ManagedBrokerSnapshot
    {
        public ManagedBrokerState State { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public string InstalledVersion { get; set; }
        public string InstallPath { get; set; }
        public string PythonPath { get; set; }
        public string LogPath { get; set; }
        public int ProcessId { get; set; }
        public bool IsInstalled { get; set; }
        public bool IsCurrentVersion { get; set; }
        public bool IsProcessRunning { get; set; }
        public bool IsProcessActive { get; set; }
        public bool IsEndpointReady { get; set; }
        public bool IsExternalProcess { get; set; }
        public bool CanStopProcess { get; set; }
        public string EndpointVersion { get; set; }
        public bool IsInstallInProgress { get; set; }
    }

    public static class ManagedBrokerProcess
    {
        internal sealed class PythonCommand
        {
            public string FileName;
            public string PrefixArguments;
        }

        internal sealed class ProcessProbe
        {
            public bool Running;
            public bool Exists;
            public string Error;
        }

        internal sealed class EndpointProbe
        {
            public bool Occupied;
            public bool Healthy;
            public string Version;
            public string Error;
        }

        private static readonly object OperationLock = new object();
        private static Task _installLaunchTask;
        private static EndpointProbe _cachedEndpointProbe;
        private static string _cachedEndpointKey;
        private static DateTimeOffset _cachedEndpointUtc;
        private const int ProbeTimeoutMilliseconds = 5000;
        private const int EndpointProbeTimeoutMilliseconds = 500;
        private const string ProcessMutexName = "UnityMcp.ManagedBroker.Process.v1";

        public static string RootPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnityMcp",
            "Broker");

        public static string InstallLogPath => Path.Combine(RootPath, "logs", "install.log");
        public static string BrokerLogPath => Path.Combine(RootPath, "logs", "broker.log");
        public static string DataPath => Path.Combine(RootPath, "data", "broker.sqlite3");
        internal static string CurrentManifestPath => Path.Combine(RootPath, "current.json");
        internal static string InstallStatusPath => Path.Combine(RootPath, "runtime", "install-status.json");
        internal static string ProcessStatePath => Path.Combine(RootPath, "runtime", "broker-process.json");

        public static ManagedBrokerSnapshot Inspect()
        {
            var endpoint = ProbeConfiguredEndpoint(false);
            return InspectPersistedState(RootPath, SceneAnalyzerVersion.Current, state =>
            {
                var running = TryGetOwnedProcess(state, out var process, out var error);
                process?.Dispose();
                if (!running && TryGetSupervisor(out var supervisor, out _))
                {
                    // The supervisor is alive while it restarts its broker.
                    supervisor.Dispose();
                    running = true;
                    error = null;
                }
                return new ProcessProbe
                {
                    Running = running,
                    Exists = running || !string.IsNullOrEmpty(error),
                    Error = error,
                };
            }, () => endpoint);
        }

        internal static ManagedBrokerSnapshot InspectPersistedState(
            string rootPath,
            string currentVersion,
            Func<JObject, ProcessProbe> probeProcess,
            Func<EndpointProbe> probeEndpoint = null)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("Root path is required.", nameof(rootPath));
            if (probeProcess == null) throw new ArgumentNullException(nameof(probeProcess));
            var manifest = ReadObject(Path.Combine(rootPath, "current.json"));
            var installStatus = ReadObject(Path.Combine(rootPath, "runtime", "install-status.json"));
            var processState = ReadObject(Path.Combine(rootPath, "runtime", "broker-process.json"));
            var installed = ValidateManifest(manifest, out var manifestError);
            var probe = probeProcess(processState) ?? new ProcessProbe();
            var endpoint = probeEndpoint?.Invoke() ?? new EndpointProbe();
            var processRunning = probe.Running;
            var ownershipError = probe.Error;
            var processExists = probe.Exists || processRunning || !string.IsNullOrEmpty(ownershipError);

            var installState = (string)installStatus?["state"];
            var installing = IsInstallOperationActive(installStatus);
            var staleInstall = (string.Equals(installState, "locating_python", StringComparison.Ordinal)
                    || string.Equals(installState, "installing", StringComparison.Ordinal))
                && !installing;
            var installFailed = string.Equals(installState, "failed", StringComparison.Ordinal) || staleInstall;
            var processStatus = (string)processState?["state"];
            var processReady = processRunning && string.Equals(processStatus, "running", StringComparison.Ordinal);
            var endpointReady = endpoint.Healthy;
            var recentProcessLaunch = string.Equals(processStatus, "starting", StringComparison.Ordinal)
                && IsRecentState(processState, TimeSpan.FromSeconds(15));
            var processStarting = string.Equals(processStatus, "starting", StringComparison.Ordinal)
                && (processRunning || recentProcessLaunch);
            var processFailed = string.Equals(processStatus, "failed", StringComparison.Ordinal)
                || string.Equals(processStatus, "starting", StringComparison.Ordinal) && !processStarting;

            var state = ManagedBrokerState.NotInstalled;
            if (installing) state = string.Equals(installState, "locating_python", StringComparison.Ordinal)
                ? ManagedBrokerState.LocatingPython
                : ManagedBrokerState.Installing;
            else if (processReady || endpointReady) state = ManagedBrokerState.Running;
            else if (processRunning || processStarting) state = ManagedBrokerState.Starting;
            else if (processFailed || !string.IsNullOrEmpty(ownershipError) || installFailed && !installed)
                state = ManagedBrokerState.Failed;
            else if (installed) state = ManagedBrokerState.Installed;

            var message = (string)installStatus?["message"];
            if (endpointReady && !processRunning)
                message = "A compatible broker is already healthy at this endpoint (external process)";
            else if (processReady || endpointReady) message = "Managed broker health endpoint is ready";
            else if (processRunning) message = "Managed broker process is starting";
            else if (processFailed) message = "Managed broker process failed";
            else if (staleInstall) message = "Managed broker installer stopped before completion";
            else if (installed && !installing) message = "Managed broker is installed";
            else if (string.IsNullOrEmpty(message)) message = "Managed broker is not installed";

            var error = endpointReady ? null : (string)processState?["error"]
                ?? ownershipError
                ?? endpoint.Error
                ?? (string)installStatus?["error"]
                ?? (staleInstall ? "The installer process is no longer running. Open the install log and retry." : null)
                ?? manifestError;

            return new ManagedBrokerSnapshot
            {
                State = state,
                Message = message,
                Error = error,
                InstalledVersion = (string)manifest?["version"],
                InstallPath = (string)manifest?["installPath"],
                PythonPath = (string)manifest?["pythonPath"],
                LogPath = processRunning ? (string)processState?["logPath"] : (string)installStatus?["logPath"],
                ProcessId = processRunning ? (int?)processState?["pid"] ?? 0 : 0,
                IsInstalled = installed,
                IsCurrentVersion = installed && string.Equals(
                    (string)manifest["version"], currentVersion, StringComparison.Ordinal),
                IsProcessRunning = processRunning,
                IsProcessActive = processExists || processStarting || endpointReady,
                IsEndpointReady = endpointReady,
                IsExternalProcess = endpointReady && !processRunning,
                CanStopProcess = processRunning,
                EndpointVersion = endpoint.Version,
                IsInstallInProgress = installing,
            };
        }

        public static void BeginInstall(bool repair)
        {
            string sourcePath = ResolveBrokerSourcePath();
            string version = SceneAnalyzerVersion.Current;
            string configuredPython = Settings.BrokerPythonPath;
            Directory.CreateDirectory(Path.Combine(RootPath, "runtime"));
            Directory.CreateDirectory(Path.Combine(RootPath, "logs"));

            lock (OperationLock)
            {
                if (_installLaunchTask != null && !_installLaunchTask.IsCompleted)
                    throw new InvalidOperationException("A managed broker installation is already being launched.");
                WithProcessLock(() =>
                {
                    var snapshot = Inspect();
                    if (snapshot.IsInstallInProgress)
                        throw new InvalidOperationException("A managed broker installation is already running.");
                    if (snapshot.IsProcessActive)
                        throw new InvalidOperationException("Stop the managed broker before installing or repairing it.");

                    WriteInstallLaunchStatus("locating_python", "Locating a compatible Python 3 installation", null);
                    _installLaunchTask = Task.Run(() =>
                    {
                        try
                        {
                            var command = DiscoverPython(configuredPython);
                            LaunchInstaller(command, sourcePath, version, repair);
                        }
                        catch (Exception exception)
                        {
                            WriteInstallLaunchStatus("failed", "Could not launch the managed broker installer", exception.Message);
                        }
                    });
                });
            }
        }

        /// EditorPrefs may only be read on the main thread; everything the launch needs is
        /// captured here first so the rest can run on a background thread.
        internal sealed class LaunchSettings
        {
            public string Host;
            public int Port;
            public string Token;
            public int ToolTimeoutSeconds;
            public int MaxRequestBodyBytes;
            public bool FocusAssist;
        }

        internal static string SupervisorStatePath => Path.Combine(RootPath, "runtime", "supervisor.json");
        internal static string SupervisorStopPath => Path.Combine(RootPath, "runtime", "supervisor.stop");
        private static int _ensureActive;

        internal static LaunchSettings CaptureLaunchSettings()
        {
            var token = BrokerTokenSync.Converge();
            if (!TryParseManagedEndpoint(Settings.BrokerUrl, out var host, out var port, out var error))
                throw new InvalidOperationException(error);
            return new LaunchSettings
            {
                Host = host,
                Port = port,
                Token = token,
                ToolTimeoutSeconds = Settings.ToolTimeoutSeconds,
                MaxRequestBodyBytes = Settings.MaxRequestBodyBytes,
                FocusAssist = Settings.AutoActivateRequiredWindows,
            };
        }

        public static void Start()
        {
            var launch = CaptureLaunchSettings();
            WithProcessLock(() => StartCore(launch));
        }

        /// Called when the Editor loads (main thread). Starts the persistent broker when none
        /// answers, installing the package's broker version first when it is missing or old.
        public static void EnsureRunningInBackground(Action<string> log)
        {
            if (Interlocked.CompareExchange(ref _ensureActive, 1, 0) != 0) return;
            LaunchSettings launch;
            try { launch = CaptureLaunchSettings(); }
            catch (Exception exception)
            {
                Volatile.Write(ref _ensureActive, 0);
                log?.Invoke("managed broker not started: " + exception.Message);
                return;
            }
            var installStarted = false;
            try
            {
                var manifest = ReadObject(CurrentManifestPath);
                var current = ValidateManifest(manifest, out _)
                    && string.Equals((string)manifest["version"], SceneAnalyzerVersion.Current, StringComparison.Ordinal);
                if (!current && !IsInstallOperationActive(ReadObject(InstallStatusPath))
                    && !ProbeBrokerEndpoint(launch.Host, launch.Port).Healthy)
                {
                    BeginInstall(false);
                    installStarted = true;
                    log?.Invoke("installing managed broker " + SceneAnalyzerVersion.Current);
                }
            }
            catch (Exception exception)
            {
                log?.Invoke("managed broker install not started: " + exception.Message);
            }

            Task.Run(() =>
            {
                try
                {
                    var deadline = DateTime.UtcNow + (installStarted ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(20));
                    while (true)
                    {
                        if (ProbeBrokerEndpoint(launch.Host, launch.Port).Healthy) return;
                        var manifest = ReadObject(CurrentManifestPath);
                        if (ValidateManifest(manifest, out _)
                            && string.Equals((string)manifest["version"], SceneAnalyzerVersion.Current, StringComparison.Ordinal)
                            && !IsInstallOperationActive(ReadObject(InstallStatusPath)))
                        {
                            WithProcessLock(() => StartCore(launch));
                            log?.Invoke("managed broker started");
                            return;
                        }
                        if (DateTime.UtcNow > deadline)
                        {
                            log?.Invoke("managed broker not started: the installation is missing or does not match this package");
                            return;
                        }
                        Thread.Sleep(2000);
                    }
                }
                catch (Exception exception)
                {
                    log?.Invoke("managed broker not started: " + exception.Message);
                }
                finally
                {
                    Volatile.Write(ref _ensureActive, 0);
                }
            });
        }

        private static void ApplyBrokerEnvironment(ProcessStartInfo startInfo, LaunchSettings launch)
        {
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_HOST"] = launch.Host;
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_PORT"] = launch.Port.ToString(CultureInfo.InvariantCulture);
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_TOKEN"] = launch.Token;
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_DB"] = DataPath;
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_TIMEOUT"] = launch.ToolTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            // A Domain Reload of a large project, or reopening the Editor, takes minutes.
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_RECONNECT_GRACE"] = "300";
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_MAX_REQUEST_BYTES"] = launch.MaxRequestBodyBytes.ToString(CultureInfo.InvariantCulture);
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_FOCUS_ASSIST"] = launch.FocusAssist ? "1" : "0";
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_FOCUS_DELAY"] = "2";
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_AUTO_WAIT_JOBS"] = "1";
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_LOG"] = BrokerLogPath;
        }

        internal static bool TryGetSupervisor(out Process process, out JObject state)
        {
            state = ReadObject(SupervisorStatePath);
            process = null;
            var status = (string)state?["state"];
            if (state == null || status == "stopped" || status == "handover") return false;
            return TryGetOwnedProcess(state, out process, out _);
        }

        private static void StartSupervisorCore(JObject manifest, string supervisorPath, LaunchSettings launch)
        {
            if (TryGetSupervisor(out var running, out _))
            {
                running.Dispose();
                return;
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = (string)manifest["pythonPath"],
                Arguments = QuoteArgument(supervisorPath) + " --detach",
                WorkingDirectory = Path.GetDirectoryName(supervisorPath),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            ApplyBrokerEnvironment(startInfo, launch);
            foreach (var name in new[] { "UNITY_MCP_BROKER_PARENT_PID", "UNITY_MCP_BROKER_PARENT_START_UTC",
                         "UNITY_MCP_BROKER_INSTANCE_ID", "UNITY_MCP_BROKER_STATE" })
                startInfo.EnvironmentVariables.Remove(name);
            var launchedUtc = DateTimeOffset.UtcNow;
            using (var launcher = Process.Start(startInfo)
                       ?? throw new InvalidOperationException("Python did not start the broker supervisor."))
                launcher.WaitForExit(15000);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                var state = ReadObject(SupervisorStatePath);
                if (TryParseUtc((string)state?["startedUtc"], out var started) && started >= launchedUtc.AddSeconds(-2)
                    && TryGetOwnedProcess(state, out var supervisor, out _))
                {
                    supervisor.Dispose();
                    return;
                }
                Thread.Sleep(250);
            }
            throw new TimeoutException("The broker supervisor did not report within 20 seconds; see " +
                                       Path.Combine(RootPath, "logs", "supervisor.log"));
        }

        private static void StartCore(LaunchSettings launch)
        {
            if (IsInstallOperationActive(ReadObject(InstallStatusPath)))
                throw new InvalidOperationException("Wait for the managed broker installation to finish before starting it.");
            var manifest = ReadObject(CurrentManifestPath);
            if (!ValidateManifest(manifest, out var error))
                throw new InvalidOperationException(error ?? "Managed broker is not installed.");
            if (!string.Equals(
                (string)manifest["version"],
                SceneAnalyzerVersion.Current,
                StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Managed broker installation does not match this package version. Install or repair it before starting.");

            var existingState = ReadObject(ProcessStatePath);
            if (TryGetOwnedProcess(existingState, out var existing, out var ownershipError))
            {
                using (existing)
                {
                    if (!PathsEqual(
                        (string)existingState?["installPath"],
                        (string)manifest["installPath"]))
                        throw new InvalidOperationException(
                            "A managed broker from a different installation is still running. Stop it before starting the current installation.");
                    return;
                }
            }
            if (!string.IsNullOrEmpty(ownershipError))
                throw new InvalidOperationException(ownershipError);

            var host = launch.Host;
            var port = launch.Port;
            var endpoint = ProbeBrokerEndpoint(host, port);
            if (endpoint.Healthy)
            {
                if (!string.IsNullOrEmpty(endpoint.Version)
                    && !string.Equals(endpoint.Version, SceneAnalyzerVersion.Current, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Unity MCP broker {endpoint.Version} is already running at {host}:{port}. " +
                        $"Stop its owner before starting package {SceneAnalyzerVersion.Current}.");
                return;
            }
            if (endpoint.Occupied)
                throw new InvalidOperationException(
                    $"Port {host}:{port} is already occupied, but its Unity MCP health check failed. " +
                    (endpoint.Error ?? "Stop the conflicting process or choose another broker endpoint."));

            Directory.CreateDirectory(Path.GetDirectoryName(BrokerLogPath));
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath));
            Directory.CreateDirectory(Path.GetDirectoryName(ProcessStatePath));

            // Installations that ship the supervisor run a persistent broker: it survives
            // Editor restarts, restarts itself when it stops answering and cleans orphans.
            var supervisorPath = Path.Combine(Path.GetDirectoryName((string)manifest["mainPath"]) ?? string.Empty, "supervisor.py");
            if (File.Exists(supervisorPath))
            {
                StartSupervisorCore(manifest, supervisorPath, launch);
                return;
            }

            var instanceId = Guid.NewGuid().ToString("N");
            var startInfo = new ProcessStartInfo
            {
                FileName = (string)manifest["pythonPath"],
                Arguments = QuoteArgument((string)manifest["mainPath"]),
                WorkingDirectory = Path.GetDirectoryName((string)manifest["mainPath"]),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            ApplyBrokerEnvironment(startInfo, launch);
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_INSTANCE_ID"] = instanceId;
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_STATE"] = ProcessStatePath;

            int ownerProcessId;
            DateTime ownerProcessStartUtc;
            using (var ownerProcess = Process.GetCurrentProcess())
            {
                ownerProcessId = ownerProcess.Id;
                ownerProcessStartUtc = ownerProcess.StartTime.ToUniversalTime();
            }
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_PARENT_PID"] = ownerProcessId.ToString();
            startInfo.EnvironmentVariables["UNITY_MCP_BROKER_PARENT_START_UTC"] =
                ownerProcessStartUtc.ToString("O", CultureInfo.InvariantCulture);

            var initialState = new JObject
            {
                ["schemaVersion"] = 1,
                ["state"] = "starting",
                ["instanceId"] = instanceId,
                ["host"] = host,
                ["port"] = port,
                ["installPath"] = (string)manifest["installPath"],
                ["logPath"] = BrokerLogPath,
                ["ownerPid"] = ownerProcessId,
                ["ownerStartUtc"] = ownerProcessStartUtc.ToString("O", CultureInfo.InvariantCulture),
                ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            AtomicWriteObject(ProcessStatePath, initialState);

            Process process = null;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Python did not start the managed broker process.");
                var persisted = ReadObject(ProcessStatePath) ?? initialState;
                if (!string.Equals((string)persisted["instanceId"], instanceId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Managed broker process state was replaced during startup.");
                persisted["pid"] = process.Id;
                persisted["processStartUtc"] = process.StartTime.ToUniversalTime().ToString("O");
                persisted["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                AtomicWriteObject(ProcessStatePath, persisted);
            }
            catch (Exception exception)
            {
                try
                {
                    if (process != null && !process.HasExited) process.Kill();
                }
                catch { }
                var failed = ReadObject(ProcessStatePath) ?? initialState;
                if (string.Equals((string)failed["instanceId"], instanceId, StringComparison.Ordinal))
                {
                    failed["state"] = "failed";
                    failed["error"] = exception.Message;
                    failed["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                    AtomicWriteObject(ProcessStatePath, failed);
                }
                throw;
            }
            finally
            {
                process?.Dispose();
            }
        }

        public static void Stop() => WithProcessLock(StopCore);

        public static void StopOwnedByCurrentUnity()
        {
            int processId;
            DateTime processStartUtc;
            using (var current = Process.GetCurrentProcess())
            {
                processId = current.Id;
                processStartUtc = current.StartTime.ToUniversalTime();
            }
            WithProcessLock(() =>
            {
                var state = ReadObject(ProcessStatePath);
                if (IsOwnedByProcess(state, processId, processStartUtc)) StopCore();
            });
        }

        private static void StopCore()
        {
            if (TryGetSupervisor(out var supervisor, out _))
            {
                using (supervisor)
                {
                    // Ask first: the supervisor stops its broker and exits on its own.
                    File.WriteAllText(SupervisorStopPath, DateTimeOffset.UtcNow.ToString("O"));
                    if (!supervisor.WaitForExit(15000))
                    {
                        // Its kill-on-close job object takes the broker down with it.
                        try { supervisor.Kill(); } catch (InvalidOperationException) { }
                        if (!supervisor.WaitForExit(5000))
                            throw new TimeoutException("The broker supervisor did not exit; its state files were preserved.");
                    }
                }
                TryDelete(SupervisorStopPath);
                TryDelete(ProcessStatePath);
                return;
            }

            var state = ReadObject(ProcessStatePath);
            if (!TryGetOwnedProcess(state, out var process, out var ownershipError))
            {
                if (!string.IsNullOrEmpty(ownershipError))
                    throw new InvalidOperationException(ownershipError);
                TryDelete(ProcessStatePath);
                return;
            }

            using (process)
            {
                try
                {
                    process.Kill();
                    if (!process.WaitForExit(5000))
                        throw new TimeoutException("Managed broker did not exit within five seconds; its ownership metadata was preserved.");
                }
                catch (InvalidOperationException) { }
            }
            TryDelete(ProcessStatePath);
        }

        public static string PreferredLogPath()
        {
            var snapshot = Inspect();
            if (!string.IsNullOrEmpty(snapshot.LogPath) && File.Exists(snapshot.LogPath)) return snapshot.LogPath;
            if (File.Exists(BrokerLogPath)) return BrokerLogPath;
            if (File.Exists(InstallLogPath)) return InstallLogPath;
            return RootPath;
        }

        internal static bool TryParseManagedEndpoint(string brokerUrl, out string host, out int port, out string error)
        {
            host = null;
            port = 0;
            error = null;
            if (!Uri.TryCreate(brokerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "ws")
            {
                error = "Managed broker URL must be an absolute ws:// loopback URL.";
                return false;
            }
            if (!uri.IsLoopback)
            {
                error = "Managed broker must bind to a loopback host.";
                return false;
            }
            var normalizedHost = uri.Host.Trim('[', ']');
            if (string.Equals(normalizedHost, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedHost, "127.0.0.1", StringComparison.Ordinal))
                host = "127.0.0.1";
            else if (string.Equals(normalizedHost, "::1", StringComparison.Ordinal))
                host = "::1";
            else
            {
                error = "Managed broker URL must use localhost, 127.0.0.1, or ::1.";
                return false;
            }
            if (!string.Equals(uri.AbsolutePath.TrimEnd('/'), "/bridge", StringComparison.Ordinal))
            {
                error = "Managed broker URL path must be /bridge.";
                return false;
            }
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "Managed broker URL cannot contain credentials, query parameters, or a fragment.";
                return false;
            }
            port = uri.Port;
            if (port < 1 || port > 65535)
            {
                error = "Managed broker port must be between 1 and 65535.";
                return false;
            }
            return true;
        }

        private static EndpointProbe ProbeConfiguredEndpoint(bool force)
        {
            if (!TryParseManagedEndpoint(Settings.BrokerUrl, out var host, out var port, out _))
                return new EndpointProbe();
            var key = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            lock (OperationLock)
            {
                if (!force && _cachedEndpointProbe != null
                    && string.Equals(_cachedEndpointKey, key, StringComparison.Ordinal)
                    && DateTimeOffset.UtcNow - _cachedEndpointUtc < TimeSpan.FromSeconds(1))
                    return _cachedEndpointProbe;
            }
            var result = ProbeBrokerEndpoint(host, port);
            lock (OperationLock)
            {
                _cachedEndpointKey = key;
                _cachedEndpointProbe = result;
                _cachedEndpointUtc = DateTimeOffset.UtcNow;
            }
            return result;
        }

        internal static EndpointProbe ProbeBrokerEndpoint(string host, int port)
        {
            var result = new EndpointProbe();
            try
            {
                var uri = new UriBuilder("http", host, port, "/health").Uri;
                var request = WebRequest.CreateHttp(uri);
                request.Method = "GET";
                request.Proxy = null;
                request.KeepAlive = false;
                request.Timeout = EndpointProbeTimeoutMilliseconds;
                request.ReadWriteTimeout = EndpointProbeTimeoutMilliseconds;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    result.Occupied = true;
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        result.Error = "Health endpoint returned HTTP " + (int)response.StatusCode + ".";
                        return result;
                    }
                    var health = JObject.Parse(reader.ReadToEnd());
                    if (!string.Equals((string)health["service"], "unity-mcp-broker", StringComparison.Ordinal))
                    {
                        result.Error = "The listening service is not a Unity MCP broker.";
                        return result;
                    }
                    result.Healthy = true;
                    result.Version = (string)health["version"];
                    return result;
                }
            }
            catch (WebException exception)
            {
                result.Occupied = exception.Response != null || CanConnectTcp(host, port);
                if (result.Occupied)
                    result.Error = "Unity MCP health check failed: " + exception.Message;
                return result;
            }
            catch (Exception exception)
            {
                result.Occupied = true;
                result.Error = "Unity MCP health check failed: " + exception.Message;
                return result;
            }
        }

        internal static bool CanConnectTcp(string host, int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(host, port);
                    return connect.Wait(EndpointProbeTimeoutMilliseconds) && client.Connected;
                }
            }
            catch (AggregateException) { return false; }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        internal static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (value.All(character => !char.IsWhiteSpace(character) && character != '"')) return value;

            var builder = new System.Text.StringBuilder(value.Length + 2);
            builder.Append('\"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '\"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append(character);
                    backslashes = 0;
                    continue;
                }
                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }
            builder.Append('\\', backslashes * 2);
            builder.Append('\"');
            return builder.ToString();
        }

        private static string ResolveBrokerSourcePath()
        {
            var package = PackageInfo.FindForAssembly(typeof(ManagedBrokerProcess).Assembly);
            var root = package?.resolvedPath;
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Could not resolve the Unity MCP package path.");
            var source = Path.Combine(root, "Broker~");
            if (!File.Exists(Path.Combine(source, "installer.py")))
                throw new InvalidOperationException("Broker installer.py is missing from the package.");
            return source;
        }

        private static PythonCommand DiscoverPython(string configuredPython)
        {
            var candidates = BuildPythonCandidates(
                configuredPython,
                Environment.GetEnvironmentVariable("UNITY_MCP_PYTHON"),
                Environment.OSVersion.Platform == PlatformID.Win32NT);
            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                if (TryProbePython(candidate, out var detail)) return candidate;
                failures.Add(candidate.FileName + ": " + detail);
            }
            throw new InvalidOperationException(
                "Python 3.10 or newer was not found. Configure a Python executable in the transport window. " +
                string.Join("; ", failures));
        }

        internal static IReadOnlyList<PythonCommand> BuildPythonCandidates(
            string configuredPython,
            string environmentPython,
            bool windows)
        {
            var candidates = new List<PythonCommand>();
            AddCandidate(candidates, configuredPython, null);
            AddCandidate(candidates, environmentPython, null);
            if (windows) AddCandidate(candidates, "py.exe", "-3");
            AddCandidate(candidates, "python3", null);
            AddCandidate(candidates, "python", null);
            return candidates;
        }

        private static void AddCandidate(ICollection<PythonCommand> candidates, string fileName, string prefixArguments)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;
            var normalized = fileName.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalized)) return;
            if (candidates.Any(item => string.Equals(item.FileName, normalized, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.PrefixArguments, prefixArguments, StringComparison.Ordinal))) return;
            candidates.Add(new PythonCommand { FileName = normalized, PrefixArguments = prefixArguments });
        }

        private static bool TryProbePython(PythonCommand command, out string detail)
        {
            detail = null;
            try
            {
                var arguments = string.IsNullOrEmpty(command.PrefixArguments)
                    ? "--version"
                    : command.PrefixArguments + " --version";
                var startInfo = new ProcessStartInfo
                {
                    FileName = command.FileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process == null) { detail = "process did not start"; return false; }
                    if (!process.WaitForExit(ProbeTimeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        detail = "version probe timed out";
                        return false;
                    }
                    var output = (process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd()).Trim();
                    if (process.ExitCode != 0) { detail = output; return false; }
                    if (!TryParsePythonVersion(output, out var version)
                        || version.CompareTo(new Version(3, 10)) < 0)
                    {
                        detail = string.IsNullOrEmpty(output) ? "unrecognized Python version" : output;
                        return false;
                    }
                    detail = output;
                    return true;
                }
            }
            catch (Exception exception)
            {
                detail = exception.Message;
                return false;
            }
        }

        internal static bool TryParsePythonVersion(string output, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(output)) return false;
            var token = output.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(item => item.Length > 0 && char.IsDigit(item[0]));
            if (token == null) return false;
            var numeric = new string(token.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray());
            return Version.TryParse(numeric, out version);
        }

        private static void LaunchInstaller(PythonCommand command, string sourcePath, string version, bool repair)
        {
            var installerPath = Path.Combine(sourcePath, "installer.py");
            var arguments = new List<string>();
            if (!string.IsNullOrEmpty(command.PrefixArguments)) arguments.Add(command.PrefixArguments);
            arguments.Add(QuoteArgument(installerPath));
            arguments.Add("--source");
            arguments.Add(QuoteArgument(sourcePath));
            arguments.Add("--root");
            arguments.Add(QuoteArgument(RootPath));
            arguments.Add("--version");
            arguments.Add(QuoteArgument(version));
            if (repair) arguments.Add("--repair");

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = string.Join(" ", arguments),
                WorkingDirectory = sourcePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Python did not start the broker installer.");
            process.Dispose();
        }

        private static void WithProcessLock(Action action)
        {
            using (var mutex = new Mutex(false, ProcessMutexName))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new TimeoutException("Timed out waiting for another Unity Editor to finish a broker process operation.");
                    action();
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        private static bool IsRecentState(JObject value, TimeSpan maximumAge)
        {
            return TryParseUtc((string)value?["updatedUtc"], out var updatedUtc)
                && DateTimeOffset.UtcNow - updatedUtc <= maximumAge;
        }

        internal static bool IsInstallOperationActive(JObject status)
        {
            var state = (string)status?["state"];
            if (!string.Equals(state, "locating_python", StringComparison.Ordinal)
                && !string.Equals(state, "installing", StringComparison.Ordinal)) return false;
            var processId = (int?)status?["pid"] ?? 0;
            if (processId <= 0) return false;
            if (!TryParseUtc((string)status["updatedUtc"], out var updatedUtc)) return false;
            var maximumAge = string.Equals(state, "locating_python", StringComparison.Ordinal)
                ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromHours(2);
            if (DateTimeOffset.UtcNow - updatedUtc > maximumAge) return false;
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool ValidateManifest(JObject manifest, out string error)
        {
            error = null;
            if (manifest == null) return false;
            var pythonPath = (string)manifest["pythonPath"];
            var mainPath = (string)manifest["mainPath"];
            if (string.IsNullOrEmpty(pythonPath) || !File.Exists(pythonPath))
            {
                error = "Managed broker Python environment is missing. Repair the installation.";
                return false;
            }
            if (string.IsNullOrEmpty(mainPath) || !File.Exists(mainPath))
            {
                error = "Managed broker source is missing. Repair the installation.";
                return false;
            }
            return true;
        }

        internal static bool TryGetOwnedProcess(JObject state, out Process process, out string error)
        {
            process = null;
            error = null;
            var processId = (int?)state?["pid"] ?? 0;
            if (processId <= 0) return false;
            try
            {
                process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    process.Dispose();
                    process = null;
                    return false;
                }
                if (!TryParseUtc((string)state["processStartUtc"], out var expectedStart))
                {
                    process.Dispose();
                    process = null;
                    error = "Managed broker process metadata is invalid; refusing to stop an unverified process.";
                    return false;
                }
                var actualStart = process.StartTime.ToUniversalTime();
                if (Math.Abs((actualStart - expectedStart.UtcDateTime).TotalSeconds) > 2)
                {
                    process.Dispose();
                    process = null;
                    error = "Managed broker PID was reused; refusing to control the unrelated process.";
                    return false;
                }
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal static bool IsOwnedByProcess(JObject state, int processId, DateTime processStartUtc)
        {
            if (((int?)state?["ownerPid"] ?? 0) != processId) return false;
            if (!TryParseUtc((string)state["ownerStartUtc"], out var expectedStart)) return false;
            return Math.Abs((processStartUtc.ToUniversalTime() - expectedStart.UtcDateTime).TotalSeconds) <= 2;
        }

        private static bool TryParseUtc(string value, out DateTimeOffset parsed)
        {
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);
        }

        private static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Environment.OSVersion.Platform == PlatformID.Win32NT
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static JObject ReadObject(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var text = File.OpenText(path))
                using (var json = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
                    return JObject.Load(json);
            }
            catch { return null; }
        }

        private static void AtomicWriteObject(string path, JObject value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, value.ToString(Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        private static void WriteInstallLaunchStatus(string state, string message, string error)
        {
            try
            {
                var payload = new JObject
                {
                    ["schemaVersion"] = 1,
                    ["state"] = state,
                    ["mode"] = "install",
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["message"] = message,
                    ["logPath"] = InstallLogPath,
                };
                if (!string.IsNullOrEmpty(error)) payload["error"] = error;
                AtomicWriteObject(InstallStatusPath, payload);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
