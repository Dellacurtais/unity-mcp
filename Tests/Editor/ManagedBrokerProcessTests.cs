using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        public void PersistedState_RecognizesInstallerOwnedByCurrentProcess()
        {
            var runtime = Path.Combine(_root, "runtime");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "install-status.json"), new JObject
            {
                ["state"] = "installing",
                ["pid"] = Process.GetCurrentProcess().Id,
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["message"] = "Installing dependencies",
            }.ToString());

            var snapshot = ManagedBrokerProcess.InspectPersistedState(
                _root,
                "1.0.0",
                _ => new ManagedBrokerProcess.ProcessProbe());

            Assert.That(snapshot.State, Is.EqualTo(ManagedBrokerState.Installing));
            Assert.That(snapshot.IsInstallInProgress, Is.True);
            Assert.That(snapshot.Message, Is.EqualTo("Installing dependencies"));
        }
    }
}
