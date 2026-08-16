using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    internal enum Ai3dGatewayProcessState
    {
        Stopped,
        Starting,
        Running,
        External,
        Failed,
    }

    internal sealed class Ai3dGatewayProcessSnapshot
    {
        public Ai3dGatewayProcessState State;
        public string Message;
        public string Error;
        public string Version;
        public string PythonPath;
        public int ProcessId;
        public bool EndpointReady;
        public bool CanStart;
        public bool CanStop;
        public bool IsExternal;
    }

    internal sealed class Ai3dGatewayLaunchOptions
    {
        public string GatewayUrl;
        public string GatewayToken;
        public string SourcePath;
        public string ProjectRoot;
        public string ConfiguredPython;
        public int MaxCreditsPerJob;
    }

    internal static class Ai3dGatewayProcess
    {
        internal sealed class PythonCommand
        {
            public string FileName;
            public string PrefixArguments;
        }

        private sealed class EndpointProbe
        {
            public bool Occupied;
            public bool Healthy;
            public string Version;
            public string Error;
        }

        private static readonly object ProcessLock = new object();
        private static readonly object ProbeLock = new object();
        private static EndpointProbe _cachedProbe;
        private static string _cachedProbeUrl;
        private static DateTimeOffset _cachedProbeUtc;
        private const int EndpointTimeoutMilliseconds = 500;
        private const int ProcessProbeTimeoutMilliseconds = 5000;
        private const string ProcessMutexName = "UnityMcp.Ai3dGateway.Process.v1";

        public static string RootPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Maicon", "UnitySceneAnalyzer", "TripoGateway");

        public static string LogPath => Path.Combine(RootPath, "logs", "gateway.log");
        public static string DataPath => Path.Combine(RootPath, "data");
        internal static string ProcessStatePath => Path.Combine(RootPath, "runtime", "gateway-process.json");

        public static Ai3dGatewayLaunchOptions CreateLaunchOptions(
            string gatewayUrl, string gatewayToken, int maxCreditsPerJob)
        {
            if (!Ai3dGatewaySettings.TryNormalizeGatewayUrl(gatewayUrl, out var normalized, out var error))
                throw new InvalidOperationException(error);
            if (string.IsNullOrWhiteSpace(gatewayToken))
                throw new InvalidOperationException("Gere ou informe o token local antes de iniciar o gateway.");

            var package = PackageInfo.FindForAssembly(typeof(Ai3dGatewayProcess).Assembly);
            var packageRoot = package?.resolvedPath;
            if (string.IsNullOrWhiteSpace(packageRoot))
                throw new InvalidOperationException("Não foi possível localizar o pacote Unity MCP.");
            var sourcePath = Path.Combine(packageRoot, "TripoGateway~");
            if (!File.Exists(Path.Combine(sourcePath, "main.py")))
                throw new InvalidOperationException("TripoGateway~/main.py não foi encontrado no pacote.");
            var python = DiscoverPythonWithGatewayDependencies(Settings.BrokerPythonPath);

            return new Ai3dGatewayLaunchOptions
            {
                GatewayUrl = normalized,
                GatewayToken = gatewayToken.Trim(),
                SourcePath = sourcePath,
                ProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
                ConfiguredPython = python.FileName,
                MaxCreditsPerJob = Math.Max(1, maxCreditsPerJob),
            };
        }

        public static Ai3dGatewayProcessSnapshot Inspect(string gatewayUrl, bool forceProbe = false)
        {
            var endpoint = ProbeEndpointCached(gatewayUrl, forceProbe);
            var state = ReadObject(ProcessStatePath);
            var processRunning = TryGetOwnedProcess(state, out var process, out var processError);
            process?.Dispose();

            var persistedState = (string)state?["state"];
            var snapshot = new Ai3dGatewayProcessSnapshot
            {
                Version = endpoint.Version,
                PythonPath = (string)state?["pythonPath"],
                ProcessId = processRunning ? (int?)state?["pid"] ?? 0 : 0,
                EndpointReady = endpoint.Healthy,
                CanStop = processRunning,
                CanStart = !endpoint.Healthy && !processRunning,
                Error = processError ?? (endpoint.Healthy ? null : (string)state?["error"]),
            };

            if (endpoint.Healthy && processRunning)
            {
                snapshot.State = Ai3dGatewayProcessState.Running;
                snapshot.Message = "Gateway gerenciado pronto";
            }
            else if (endpoint.Healthy)
            {
                snapshot.State = Ai3dGatewayProcessState.External;
                snapshot.IsExternal = true;
                snapshot.CanStart = false;
                snapshot.Message = "Gateway externo pronto; este editor não controla o processo";
            }
            else if (processRunning)
            {
                snapshot.State = Ai3dGatewayProcessState.Starting;
                snapshot.Message = "Processo iniciado; aguardando o endpoint de saúde";
                snapshot.Error = endpoint.Error;
            }
            else if (string.Equals(persistedState, "failed", StringComparison.Ordinal))
            {
                snapshot.State = Ai3dGatewayProcessState.Failed;
                snapshot.Message = "Falha ao iniciar o gateway";
                snapshot.Error = (string)state?["error"] ?? endpoint.Error;
            }
            else
            {
                snapshot.State = Ai3dGatewayProcessState.Stopped;
                snapshot.Message = "Gateway parado";
                snapshot.Error = null;
            }
            return snapshot;
        }

        public static void Start(Ai3dGatewayLaunchOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            WithProcessLock(() => StartCore(options));
        }

        public static void Stop()
        {
            WithProcessLock(StopCore);
            InvalidateProbeCache();
        }

        public static void Restart(Ai3dGatewayLaunchOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            WithProcessLock(() =>
            {
                StopCore();
                StartCore(options);
            });
        }

        public static void RevealLogs()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            if (!File.Exists(LogPath)) File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
            UnityEditor.EditorUtility.RevealInFinder(LogPath);
        }

        private static void StartCore(Ai3dGatewayLaunchOptions options)
        {
            var endpoint = ProbeEndpoint(options.GatewayUrl);
            if (endpoint.Healthy) return;
            if (endpoint.Occupied)
                throw new InvalidOperationException(
                    "A porta do gateway está ocupada por outro serviço não saudável. " + endpoint.Error);

            var previous = ReadObject(ProcessStatePath);
            if (TryGetOwnedProcess(previous, out var existing, out var ownershipError))
            {
                existing.Dispose();
                return;
            }
            if (!string.IsNullOrEmpty(ownershipError))
                throw new InvalidOperationException(ownershipError);

            var python = new PythonCommand { FileName = options.ConfiguredPython };
            var uri = new Uri(options.GatewayUrl);
            Directory.CreateDirectory(DataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            Directory.CreateDirectory(Path.GetDirectoryName(ProcessStatePath));

            var arguments = new List<string>();
            if (!string.IsNullOrWhiteSpace(python.PrefixArguments)) arguments.Add(python.PrefixArguments);
            arguments.Add(QuoteArgument(Path.Combine(options.SourcePath, "main.py")));
            var startInfo = new ProcessStartInfo
            {
                FileName = python.FileName,
                Arguments = string.Join(" ", arguments),
                WorkingDirectory = options.SourcePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.EnvironmentVariables["TRIPO_GATEWAY_HOST"] = uri.Host;
            startInfo.EnvironmentVariables["TRIPO_GATEWAY_PORT"] = uri.Port.ToString(CultureInfo.InvariantCulture);
            startInfo.EnvironmentVariables["TRIPO_GATEWAY_TOKEN"] = options.GatewayToken;
            startInfo.EnvironmentVariables["TRIPO_GATEWAY_DATA_DIR"] = DataPath;
            startInfo.EnvironmentVariables["TRIPO_GATEWAY_UPLOAD_ROOTS"] = options.ProjectRoot;
            startInfo.EnvironmentVariables["TRIPO_GATEWAY_MAX_CREDITS_PER_JOB"] =
                options.MaxCreditsPerJob.ToString(CultureInfo.InvariantCulture);
            startInfo.EnvironmentVariables["TRIPO_GATEWAY_LOG"] = LogPath;

            var instanceId = Guid.NewGuid().ToString("N");
            var initialState = new JObject
            {
                ["schemaVersion"] = 1,
                ["state"] = "starting",
                ["instanceId"] = instanceId,
                ["gatewayUrl"] = options.GatewayUrl,
                ["pythonPath"] = python.FileName,
                ["sourcePath"] = options.SourcePath,
                ["logPath"] = LogPath,
                ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            AtomicWriteObject(ProcessStatePath, initialState);

            Process process = null;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("O Python não iniciou o gateway AI 3D.");
                initialState["pid"] = process.Id;
                initialState["processStartUtc"] = process.StartTime.ToUniversalTime().ToString("O");
                initialState["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                AtomicWriteObject(ProcessStatePath, initialState);
                InvalidateProbeCache();

                var deadline = DateTime.UtcNow.AddSeconds(12);
                EndpointProbe ready = null;
                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException(
                            "O gateway encerrou durante a inicialização (código " + process.ExitCode + "). Abra o log para detalhes.");
                    ready = ProbeEndpoint(options.GatewayUrl);
                    if (ready.Healthy) break;
                    Thread.Sleep(150);
                }
                if (ready == null || !ready.Healthy)
                    throw new TimeoutException("O gateway iniciou, mas não ficou pronto em 12 segundos. Abra o log para detalhes.");

                initialState["state"] = "running";
                initialState["version"] = ready.Version;
                initialState["readyUtc"] = DateTimeOffset.UtcNow.ToString("O");
                initialState["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                AtomicWriteObject(ProcessStatePath, initialState);
                InvalidateProbeCache();
            }
            catch (Exception exception)
            {
                try { if (process != null && !process.HasExited) process.Kill(); }
                catch { }
                initialState["state"] = "failed";
                initialState["error"] = exception.Message;
                initialState["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
                AtomicWriteObject(ProcessStatePath, initialState);
                InvalidateProbeCache();
                throw;
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static void StopCore()
        {
            var state = ReadObject(ProcessStatePath);
            if (!TryGetOwnedProcess(state, out var process, out var error))
            {
                process?.Dispose();
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                return;
            }
            using (process)
            {
                process.Kill();
                if (!process.WaitForExit(5000))
                    throw new TimeoutException("O gateway não encerrou dentro de cinco segundos.");
            }
            state["state"] = "stopped";
            state["stoppedUtc"] = DateTimeOffset.UtcNow.ToString("O");
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
            AtomicWriteObject(ProcessStatePath, state);
        }

        private static PythonCommand DiscoverPythonWithGatewayDependencies(string configuredPython)
        {
            var candidates = BuildPythonCandidates(
                configuredPython,
                Environment.GetEnvironmentVariable("UNITY_MCP_PYTHON"),
                ReadManagedBrokerPython());
            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                if (TryProbePython(candidate, out var detail)) return candidate;
                failures.Add(candidate.FileName + ": " + detail);
            }
            throw new InvalidOperationException(
                "Python 3.10+ com fastapi, uvicorn, pydantic e httpx não foi encontrado. " +
                "Instale TripoGateway~/requirements.txt no Python configurado em Unity MCP Transport. " +
                string.Join("; ", failures));
        }

        internal static IReadOnlyList<PythonCommand> BuildPythonCandidates(
            string configuredPython, string environmentPython, string managedBrokerPython)
        {
            var values = new[]
            {
                configuredPython,
                environmentPython,
                managedBrokerPython,
                "python3",
                "python",
            };
            var result = new List<PythonCommand>();
            foreach (var value in values)
            {
                var normalized = value?.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (result.Any(item => string.Equals(item.FileName, normalized, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Add(new PythonCommand { FileName = normalized });
            }
            return result;
        }

        private static bool TryProbePython(PythonCommand command, out string detail)
        {
            detail = null;
            try
            {
                var code = "import sys; assert sys.version_info >= (3, 10); import fastapi,httpx,pydantic,uvicorn; print(sys.executable)";
                var prefix = string.IsNullOrWhiteSpace(command.PrefixArguments)
                    ? string.Empty : command.PrefixArguments + " ";
                var startInfo = new ProcessStartInfo
                {
                    FileName = command.FileName,
                    Arguments = prefix + "-c " + QuoteArgument(code),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process == null) { detail = "processo não iniciou"; return false; }
                    if (!process.WaitForExit(ProcessProbeTimeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        detail = "verificação excedeu o tempo limite";
                        return false;
                    }
                    var output = (process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd()).Trim();
                    if (process.ExitCode == 0)
                    {
                        var executable = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => line.Trim())
                            .FirstOrDefault(line => Path.IsPathRooted(line) && File.Exists(line));
                        if (!string.IsNullOrWhiteSpace(executable)) command.FileName = executable;
                        return true;
                    }
                    detail = string.IsNullOrWhiteSpace(output) ? "dependências ausentes" : LastLine(output);
                    return false;
                }
            }
            catch (Exception exception)
            {
                detail = exception.Message;
                return false;
            }
        }

        private static string ReadManagedBrokerPython()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UnityMcp", "Broker", "current.json");
                return (string)ReadObject(path)?["pythonPath"];
            }
            catch { return null; }
        }

        private static EndpointProbe ProbeEndpointCached(string gatewayUrl, bool force)
        {
            lock (ProbeLock)
            {
                if (!force && _cachedProbe != null
                    && string.Equals(_cachedProbeUrl, gatewayUrl, StringComparison.Ordinal)
                    && DateTimeOffset.UtcNow - _cachedProbeUtc < TimeSpan.FromSeconds(2))
                    return _cachedProbe;
                _cachedProbe = ProbeEndpoint(gatewayUrl);
                _cachedProbeUrl = gatewayUrl;
                _cachedProbeUtc = DateTimeOffset.UtcNow;
                return _cachedProbe;
            }
        }

        private static EndpointProbe ProbeEndpoint(string gatewayUrl)
        {
            var result = new EndpointProbe();
            if (!Ai3dGatewaySettings.TryNormalizeGatewayUrl(gatewayUrl, out var normalized, out var validationError))
            {
                result.Error = validationError;
                return result;
            }
            var uri = new Uri(normalized);
            if (!CanConnectTcp(uri.Host, uri.Port)) return result;
            result.Occupied = true;
            try
            {
#pragma warning disable SYSLIB0014
                var request = (HttpWebRequest)WebRequest.Create(normalized + "/health");
#pragma warning restore SYSLIB0014
                request.Method = "GET";
                request.Timeout = EndpointTimeoutMilliseconds;
                request.ReadWriteTimeout = EndpointTimeoutMilliseconds;
                request.Proxy = null;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var payload = JObject.Parse(reader.ReadToEnd());
                    result.Version = (string)payload["version"];
                    result.Healthy = response.StatusCode == HttpStatusCode.OK
                        && string.Equals((string)payload["service"], "unity-tripo-gateway", StringComparison.Ordinal)
                        && (bool?)payload["ready"] == true;
                    if (!result.Healthy) result.Error = "A porta respondeu, mas não contém um Unity AI 3D Gateway saudável.";
                }
            }
            catch (WebException exception)
            {
                result.Error = "Health check falhou: " + exception.Message;
            }
            catch (Exception exception)
            {
                result.Error = "Health check falhou: " + exception.Message;
            }
            return result;
        }

        private static bool CanConnectTcp(string host, int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync(host, port);
                    return task.Wait(EndpointTimeoutMilliseconds) && client.Connected;
                }
            }
            catch { return false; }
        }

        private static bool TryGetOwnedProcess(JObject state, out Process process, out string error)
        {
            process = null;
            error = null;
            var pid = (int?)state?["pid"] ?? 0;
            if (pid <= 0) return false;
            if (!TryReadUtcTimestamp(state?["processStartUtc"], out var expectedStart))
            {
                error = "Metadados do processo do gateway são inválidos; o plugin recusou controlar o PID.";
                return false;
            }
            try
            {
                process = Process.GetProcessById(pid);
                var actualStart = process.StartTime.ToUniversalTime();
                if (Math.Abs((actualStart - expectedStart.UtcDateTime).TotalSeconds) > 2)
                {
                    process.Dispose();
                    process = null;
                    error = "O PID persistido foi reutilizado; o plugin recusou controlar um processo não relacionado.";
                    return false;
                }
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                process?.Dispose();
                process = null;
                return false;
            }
            catch (Exception exception)
            {
                process?.Dispose();
                process = null;
                error = exception.Message;
                return false;
            }
        }

        internal static bool TryReadUtcTimestamp(JToken token, out DateTimeOffset timestamp)
        {
            timestamp = default;
            if (token == null || token.Type == JTokenType.Null) return false;

            // Json.NET normally converts ISO-8601 values into DateTime JValues while
            // parsing. Casting that JValue to string loses the original UTC suffix and
            // makes DateTimeOffset interpret it as local time. Preserve the instant
            // regardless of whether the token arrived as a Date or a String.
            if (token.Type == JTokenType.Date)
            {
                var value = token.Value<DateTime>();
                timestamp = new DateTimeOffset(value.ToUniversalTime());
                return true;
            }

            return DateTimeOffset.TryParse(
                token.Value<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }

        private static void WithProcessLock(Action action)
        {
            lock (ProcessLock)
            using (var mutex = new Mutex(false, ProcessMutexName))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new TimeoutException("Outro editor ainda está alterando o processo do gateway.");
                    action();
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        private static void InvalidateProbeCache()
        {
            lock (ProbeLock)
            {
                _cachedProbe = null;
                _cachedProbeUrl = null;
                _cachedProbeUtc = default;
            }
        }

        private static JObject ReadObject(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var stringReader = new StringReader(File.ReadAllText(path, Encoding.UTF8)))
                using (var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
                    return JObject.Load(jsonReader);
            }
            catch { return null; }
        }

        private static void AtomicWriteObject(string path, JObject value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, value.ToString(Formatting.Indented), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (value.All(character => !char.IsWhiteSpace(character) && character != '"')) return value;

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
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
            builder.Append('"');
            return builder.ToString();
        }

        private static string LastLine(string value)
        {
            var lines = (value ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? value : lines[lines.Length - 1].Trim();
        }
    }
}
