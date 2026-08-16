using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp.Broker;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class ManagedBrokerProcessTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "unity-mcp-broker-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Test]
        public void PythonCandidates_PreferConfiguredAndEnvironmentThenDeduplicateFallbacks()
        {
            var candidates = ManagedBrokerProcess.BuildPythonCandidates(
                "  \"C:/Python/python.exe\"  ",
                "python3",
                true);

            CollectionAssert.AreEqual(
                new[] { "C:/Python/python.exe", "python3", "py.exe", "python" },
                candidates.Select(candidate => candidate.FileName).ToArray());
            Assert.That(candidates.Single(candidate => candidate.FileName == "py.exe").PrefixArguments, Is.EqualTo("-3"));
        }

        [TestCase("Python 3.10.0", 3, 10, 0)]
        [TestCase("Python 3.12.7\r\n", 3, 12, 7)]
        [TestCase("launcher output Python 3.14.0rc2", 3, 14, 0)]
        public void PythonVersion_ParsesSupportedInterpreterOutput(
            string output,
            int major,
            int minor,
            int build)
        {
            Assert.That(ManagedBrokerProcess.TryParsePythonVersion(output, out var version), Is.True);
            Assert.That(version, Is.EqualTo(new Version(major, minor, build)));
        }

        [TestCase("")]
        [TestCase("not python")]
        public void PythonVersion_RejectsUnrecognizedOutput(string output)
        {
            Assert.That(ManagedBrokerProcess.TryParsePythonVersion(output, out _), Is.False);
        }

        [Test]
        public void ManagedEndpoint_AcceptsOnlyLoopbackBridgeWebSockets()
        {
            Assert.That(
                ManagedBrokerProcess.TryParseManagedEndpoint(
                    "ws://localhost:8765/bridge",
                    out var host,
                    out var port,
                    out var error),
                Is.True,
                error);
            Assert.That(host, Is.EqualTo("127.0.0.1"));
            Assert.That(port, Is.EqualTo(8765));

            Assert.That(
                ManagedBrokerProcess.TryParseManagedEndpoint(
                    "wss://127.0.0.1:8765/bridge",
                    out _,
                    out _,
                    out error),
                Is.False);
            Assert.That(error, Does.Contain("ws://"));
            Assert.That(
                ManagedBrokerProcess.TryParseManagedEndpoint(
                    "ws://example.com:8765/bridge",
                    out _,
                    out _,
                    out _),
                Is.False);
            Assert.That(
                ManagedBrokerProcess.TryParseManagedEndpoint(
                    "ws://127.0.0.1:8765/not-bridge",
                    out _,
                    out _,
                    out _),
                Is.False);
        }

        [Test]
        public void PersistedState_RestoresInstalledRunningBrokerWithoutStaticMemory()
        {
            var installPath = Path.Combine(_root, "installations", "1.2.3-test");
            var pythonPath = Path.Combine(installPath, "venv", "python.exe");
            var mainPath = Path.Combine(installPath, "source", "main.py");
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath));
            Directory.CreateDirectory(Path.GetDirectoryName(mainPath));
            File.WriteAllText(pythonPath, string.Empty);
            File.WriteAllText(mainPath, string.Empty);
            File.WriteAllText(Path.Combine(_root, "current.json"), new JObject
            {
                ["version"] = "1.2.3",
                ["installPath"] = installPath,
                ["pythonPath"] = pythonPath,
                ["mainPath"] = mainPath,
            }.ToString());
            var runtime = Path.Combine(_root, "runtime");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "broker-process.json"), new JObject
            {
                ["state"] = "running",
                ["pid"] = 4242,
                ["installPath"] = installPath,
                ["logPath"] = Path.Combine(_root, "logs", "broker.log"),
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            }.ToString());

            var snapshot = ManagedBrokerProcess.InspectPersistedState(
                _root,
                "1.2.3",
                _ => new ManagedBrokerProcess.ProcessProbe { Running = true });

            Assert.That(snapshot.State, Is.EqualTo(ManagedBrokerState.Running));
            Assert.That(snapshot.IsInstalled, Is.True);
            Assert.That(snapshot.IsCurrentVersion, Is.True);
            Assert.That(snapshot.IsProcessRunning, Is.True);
            Assert.That(snapshot.ProcessId, Is.EqualTo(4242));
            Assert.That(snapshot.PythonPath, Is.EqualTo(pythonPath));
        }

        [Test]
        public void PersistedState_ReportsFailedProcessAndVersionDrift()
        {
            var installPath = Path.Combine(_root, "installations", "old");
            var pythonPath = Path.Combine(installPath, "venv", "python.exe");
            var mainPath = Path.Combine(installPath, "source", "main.py");
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath));
            Directory.CreateDirectory(Path.GetDirectoryName(mainPath));
            File.WriteAllText(pythonPath, string.Empty);
            File.WriteAllText(mainPath, string.Empty);
            File.WriteAllText(Path.Combine(_root, "current.json"), new JObject
            {
                ["version"] = "1.0.0",
                ["installPath"] = installPath,
                ["pythonPath"] = pythonPath,
                ["mainPath"] = mainPath,
            }.ToString());
            var runtime = Path.Combine(_root, "runtime");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "broker-process.json"), new JObject
            {
                ["state"] = "failed",
                ["error"] = "port already in use",
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            }.ToString());

            var snapshot = ManagedBrokerProcess.InspectPersistedState(
                _root,
                "2.0.0",
                _ => new ManagedBrokerProcess.ProcessProbe());

            Assert.That(snapshot.State, Is.EqualTo(ManagedBrokerState.Failed));
            Assert.That(snapshot.IsInstalled, Is.True);
            Assert.That(snapshot.IsCurrentVersion, Is.False);
            Assert.That(snapshot.Error, Is.EqualTo("port already in use"));
        }

        [Test]
        public void PersistedState_HealthyExternalEndpointOverridesStaleManagedFailure()
        {
            var installPath = Path.Combine(_root, "installations", "1.2.3-test");
            var pythonPath = Path.Combine(installPath, "venv", "python.exe");
            var mainPath = Path.Combine(installPath, "source", "main.py");
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath));
            Directory.CreateDirectory(Path.GetDirectoryName(mainPath));
            File.WriteAllText(pythonPath, string.Empty);
            File.WriteAllText(mainPath, string.Empty);
            File.WriteAllText(Path.Combine(_root, "current.json"), new JObject
            {
                ["version"] = "1.2.3",
                ["installPath"] = installPath,
                ["pythonPath"] = pythonPath,
                ["mainPath"] = mainPath,
            }.ToString());
            var runtime = Path.Combine(_root, "runtime");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "broker-process.json"), new JObject
            {
                ["state"] = "failed",
                ["error"] = "SystemExit: 3",
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            }.ToString());

            var snapshot = ManagedBrokerProcess.InspectPersistedState(
                _root,
                "1.2.3",
                _ => new ManagedBrokerProcess.ProcessProbe(),
                () => new ManagedBrokerProcess.EndpointProbe
                {
                    Occupied = true,
                    Healthy = true,
                    Version = "1.2.3",
                });

            Assert.That(snapshot.State, Is.EqualTo(ManagedBrokerState.Running));
            Assert.That(snapshot.IsEndpointReady, Is.True);
            Assert.That(snapshot.IsExternalProcess, Is.True);
            Assert.That(snapshot.IsProcessActive, Is.True);
            Assert.That(snapshot.CanStopProcess, Is.False);
            Assert.That(snapshot.EndpointVersion, Is.EqualTo("1.2.3"));
            Assert.That(snapshot.Error, Is.Null);
            Assert.That(snapshot.Message, Does.Contain("external process"));
        }

        [Test]
        public void EndpointProbe_RecognizesUnityBrokerHealthPayload()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var responder = Task.Run(() =>
            {
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                {
                    var request = new byte[2048];
                    stream.Read(request, 0, request.Length);
                    var body = "{\"service\":\"unity-mcp-broker\",\"version\":\"9.8.7\"}";
                    var response = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                        + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
                    stream.Write(response, 0, response.Length);
                }
            });

            try
            {
                var probe = ManagedBrokerProcess.ProbeBrokerEndpoint("127.0.0.1", port);
                Assert.That(probe.Occupied, Is.True);
                Assert.That(probe.Healthy, Is.True, probe.Error);
                Assert.That(probe.Version, Is.EqualTo("9.8.7"));
                Assert.That(responder.Wait(TimeSpan.FromSeconds(2)), Is.True);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void EndpointProbe_DoesNotTreatClosedPortTimeoutAsOccupied()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var probe = ManagedBrokerProcess.ProbeBrokerEndpoint("127.0.0.1", port);

            Assert.That(probe.Occupied, Is.False, probe.Error);
            Assert.That(probe.Healthy, Is.False);
            Assert.That(probe.Error, Is.Null);
        }

        [Test]
        public void EndpointProbe_KeepsListeningButUnhealthyPortOccupied()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var probe = ManagedBrokerProcess.ProbeBrokerEndpoint("127.0.0.1", port);

                Assert.That(probe.Occupied, Is.True);
                Assert.That(probe.Healthy, Is.False);
                Assert.That(probe.Error, Does.Contain("health check failed"));
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void PersistedState_RecognizesInstallerOwnedByCurrentProcess()
        {
            var runtime = Path.Combine(_root, "runtime");
            Directory.CreateDirectory(runtime);
            var installStatus = new JObject
            {
                ["state"] = "installing",
                ["pid"] = Process.GetCurrentProcess().Id,
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["message"] = "Installing dependencies",
            };
            File.WriteAllText(Path.Combine(runtime, "install-status.json"), installStatus.ToString());

            Assert.That(
                ManagedBrokerProcess.IsInstallOperationActive(installStatus),
                Is.True,
                "The current Unity process must be recognized as the live installer owner.");

            var snapshot = ManagedBrokerProcess.InspectPersistedState(
                _root,
                "1.0.0",
                _ => new ManagedBrokerProcess.ProcessProbe());

            Assert.That(snapshot.State, Is.EqualTo(ManagedBrokerState.Installing));
            Assert.That(snapshot.IsInstallInProgress, Is.True);
            Assert.That(snapshot.Message, Is.EqualTo("Installing dependencies"));
        }

        [Test]
        public void ProcessOwnership_IgnoresIncompleteMetadataAfterProcessExited()
        {
            var state = new JObject { ["pid"] = int.MaxValue };

            Assert.That(
                ManagedBrokerProcess.TryGetOwnedProcess(state, out var process, out var error),
                Is.False);
            Assert.That(process, Is.Null);
            Assert.That(error, Is.Null);
        }

        [Test]
        public void ProcessOwnership_RejectsIncompleteMetadataForLiveProcess()
        {
            using (var current = Process.GetCurrentProcess())
            {
                var state = new JObject { ["pid"] = current.Id };

                Assert.That(
                    ManagedBrokerProcess.TryGetOwnedProcess(state, out var owned, out var error),
                    Is.False);
                Assert.That(owned, Is.Null);
                Assert.That(error, Does.Contain("metadata is invalid"));
            }
        }

        [Test]
        public void ProcessOwnership_RejectsMismatchedStartTimeForLiveProcess()
        {
            using (var current = Process.GetCurrentProcess())
            {
                var state = new JObject
                {
                    ["pid"] = current.Id,
                    ["processStartUtc"] = current.StartTime.ToUniversalTime()
                        .AddMinutes(-1)
                        .ToString("O", CultureInfo.InvariantCulture),
                };

                Assert.That(
                    ManagedBrokerProcess.TryGetOwnedProcess(state, out var owned, out var error),
                    Is.False);
                Assert.That(owned, Is.Null);
                Assert.That(error, Does.Contain("PID was reused"));
            }
        }

        [Test]
        public void PersistedState_ValidatesPythonTimestampUnderNonUsCulture()
        {
            var installPath = Path.Combine(_root, "installations", "1.2.3-test");
            var pythonPath = Path.Combine(installPath, "venv", "python.exe");
            var mainPath = Path.Combine(installPath, "source", "main.py");
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath));
            Directory.CreateDirectory(Path.GetDirectoryName(mainPath));
            File.WriteAllText(pythonPath, string.Empty);
            File.WriteAllText(mainPath, string.Empty);
            File.WriteAllText(Path.Combine(_root, "current.json"), new JObject
            {
                ["version"] = "1.2.3",
                ["installPath"] = installPath,
                ["pythonPath"] = pythonPath,
                ["mainPath"] = mainPath,
            }.ToString());
            var runtime = Path.Combine(_root, "runtime");
            Directory.CreateDirectory(runtime);

            using (var current = Process.GetCurrentProcess())
            {
                var processStartUtc = new DateTimeOffset(current.StartTime.ToUniversalTime())
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
                File.WriteAllText(Path.Combine(runtime, "broker-process.json"), new JObject
                {
                    ["state"] = "running",
                    ["pid"] = current.Id,
                    ["processStartUtc"] = processStartUtc,
                    ["installPath"] = installPath,
                    ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                }.ToString());

                var previousCulture = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
                    var snapshot = ManagedBrokerProcess.InspectPersistedState(
                        _root,
                        "1.2.3",
                        state =>
                        {
                            var running = ManagedBrokerProcess.TryGetOwnedProcess(
                                state,
                                out var process,
                                out var error);
                            process?.Dispose();
                            return new ManagedBrokerProcess.ProcessProbe
                            {
                                Running = running,
                                Exists = running || !string.IsNullOrEmpty(error),
                                Error = error,
                            };
                        });

                    Assert.That(snapshot.State, Is.EqualTo(ManagedBrokerState.Running), snapshot.Error);
                    Assert.That(snapshot.IsProcessRunning, Is.True);
                    Assert.That(snapshot.Error, Is.Null);
                }
                finally
                {
                    CultureInfo.CurrentCulture = previousCulture;
                }
            }
        }

        [Test]
        public void UnityOwnership_RequiresMatchingPidAndStartTime()
        {
            using (var current = Process.GetCurrentProcess())
            {
                var startUtc = current.StartTime.ToUniversalTime();
                var state = new JObject
                {
                    ["ownerPid"] = current.Id,
                    ["ownerStartUtc"] = startUtc.ToString("O"),
                };

                Assert.That(
                    ManagedBrokerProcess.IsOwnedByProcess(state, current.Id, startUtc),
                    Is.True);
                Assert.That(
                    ManagedBrokerProcess.IsOwnedByProcess(state, current.Id + 1, startUtc),
                    Is.False);
                state.Remove("ownerStartUtc");
                Assert.That(
                    ManagedBrokerProcess.IsOwnedByProcess(state, current.Id, startUtc),
                    Is.False);
            }
        }
    }
}
