using System;
using System.Collections;
using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Broker;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity63Ci
{
    [InitializeOnLoad]
    public static class LiveHost
    {
        private const string EnabledKey = "Unity63Ci.LiveHost.Enabled";
        private const string StartedKey = "Unity63Ci.LiveHost.StartedUtc";
        private static McpRequestProcessor _processor;
        private static BrokerBridgeClient _bridge;
        private static BrokerLifecycleMonitor _lifecycle;
        private static string _readyGeneration;

        static LiveHost()
        {
            if (SessionState.GetBool(EnabledKey, false)
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UNITY_MCP_CI_BROKER_URL")))
                EditorApplication.delayCall += Start;
        }

        // Launch with -batchmode -executeMethod Unity63Ci.LiveHost.Start and no
        // -quit. This owns only its isolated bridge, never global EditorPrefs.
        public static void Start()
        {
            if (_bridge != null) return;
            if (Application.dataPath.Replace('\\', '/').IndexOf("/.validation/unity63-ci/", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("LiveHost is restricted to the disposable Unity63 CI fixture.");
            var url = Environment.GetEnvironmentVariable("UNITY_MCP_CI_BROKER_URL");
            var token = Environment.GetEnvironmentVariable("UNITY_MCP_CI_BROKER_TOKEN");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
                throw new InvalidOperationException("UNITY_MCP_CI_BROKER_URL and UNITY_MCP_CI_BROKER_TOKEN are required.");
            var uri = new Uri(url);
            if (uri.Scheme != "ws" || !uri.IsLoopback)
                throw new InvalidOperationException("The CI broker must use a loopback ws:// URL.");
            SessionState.SetBool(EnabledKey, true);
            if (string.IsNullOrEmpty(SessionState.GetString(StartedKey, "")))
                SessionState.SetString(StartedKey, DateTime.UtcNow.ToString("O"));
            _processor = new McpRequestProcessor(Bootstrap.ToolRegistry, 8, 60);
            _bridge = new BrokerBridgeClient(_processor, Bootstrap.ToolRegistry,
                Path.GetDirectoryName(Application.dataPath), url, token, 8 * 1024 * 1024);
            _lifecycle = new BrokerLifecycleMonitor(_bridge, Bootstrap.ToolRegistry);
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += Stop;
            EditorApplication.update += Update;
            _bridge.Start();
        }

        private static void Update()
        {
            var snapshot = _bridge?.Snapshot;
            var readyPath = Environment.GetEnvironmentVariable("UNITY_MCP_CI_READY_FILE");
            if (snapshot != null && snapshot.IsConnected && snapshot.State == "ready"
                && snapshot.GenerationId != _readyGeneration && !string.IsNullOrEmpty(readyPath))
            {
                File.WriteAllText(readyPath, new JObject
                {
                    ["projectKey"] = snapshot.ProjectKey,
                    ["generationId"] = snapshot.GenerationId,
                    ["registryHash"] = Bootstrap.ToolRegistry.VersionHash,
                    ["registryCount"] = Bootstrap.ToolRegistry.Count,
                    ["unityVersion"] = Application.unityVersion,
                }.ToString());
                _readyGeneration = snapshot.GenerationId;
            }
            var stopPath = Environment.GetEnvironmentVariable("UNITY_MCP_CI_STOP_FILE");
            var started = DateTime.Parse(SessionState.GetString(StartedKey, DateTime.UtcNow.ToString("O")),
                null, System.Globalization.DateTimeStyles.RoundtripKind);
            if ((!string.IsNullOrEmpty(stopPath) && File.Exists(stopPath)) || DateTime.UtcNow - started > TimeSpan.FromMinutes(30))
            {
                Stop();
                EditorApplication.Exit(0);
            }
        }

        private static void BeforeReload()
        {
            // Reload preserves the per-Editor SessionState flag and environment;
            // InitializeOnLoad creates a fresh processor and bridge afterward.
            try { _lifecycle?.BeforeReloadAsync(System.Threading.CancellationToken.None).Wait(250); }
            catch { }
            Dispose();
        }

        public static void Stop()
        {
            SessionState.EraseBool(EnabledKey);
            SessionState.EraseString(StartedKey);
            Dispose();
        }

        private static void Dispose()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            EditorApplication.quitting -= Stop;
            EditorApplication.update -= Update;
            _lifecycle?.Dispose();
            _bridge?.Dispose();
            _processor?.Dispose();
            _lifecycle = null;
            _bridge = null;
            _processor = null;
        }
    }

    // Copied only to the disposable CI project; never imported by the package.
    public static class ImportGate
    {
        public static void Verify()
        {
            if (Application.unityVersion != "6000.3.5f2")
                throw new InvalidOperationException("Expected Unity 6000.3.5f2; running " + Application.unityVersion);
            if (EditorUtility.scriptCompilationFailed)
                throw new InvalidOperationException("Unity reported compilation errors.");
            var report = OperationalReport.SelfTest();
            report["selfTestValid"] = report["valid"].DeepClone();
            report["unityVersion"] = Application.unityVersion;
            var consoleErrors = ConsoleCapture.Snapshot(null, LogType.Error, null, 1000);
            report["consoleErrorCount"] = consoleErrors.Count;
            report["consoleErrors"] = new JArray(consoleErrors.Select(entry => new JObject
            {
                ["severity"] = entry.Severity.ToString(), ["message"] = entry.Message, ["stack"] = entry.Stack,
            }));
            report["valid"] = (bool)report["selfTestValid"] && consoleErrors.Count == 0;
            if (UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.unity.render-pipelines.universal") != null)
            {
                const string scriptPath = "Packages/com.unity.render-pipelines.universal/Runtime/UniversalRenderPipelineGlobalSettings.cs";
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                var compiledType = Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings, Unity.RenderPipelines.Universal.Runtime");
                report["urpScriptDiagnostics"] = new JObject
                {
                    ["scriptPath"] = scriptPath,
                    ["scriptGuid"] = AssetDatabase.AssetPathToGUID(scriptPath),
                    ["scriptAssetExists"] = script != null,
                    ["scriptClass"] = script?.GetClass()?.AssemblyQualifiedName,
                    ["compiledClass"] = compiledType?.AssemblyQualifiedName,
                    ["importedScriptCount"] = AssetDatabase.FindAssets("t:MonoScript", new[] { "Packages/com.unity.render-pipelines.universal" }).Length,
                };
            }
            var destination = Environment.GetEnvironmentVariable("UNITY_MCP_CI_REPORT");
            if (string.IsNullOrEmpty(destination)) throw new InvalidOperationException("UNITY_MCP_CI_REPORT is required.");
            File.WriteAllText(destination, report.ToString());
            if (!(bool)report["valid"]) throw new InvalidOperationException("Import/registry validation failed; inspect consoleErrors and selfTestValid in the import report.");
        }
    }

    public sealed class SmokeTests
    {
        private const string PlayOptionsSaved = "Unity63Ci.Smoke.PlayOptionsSaved";
        private const string PlayOptionsEnabled = "Unity63Ci.Smoke.PlayOptionsEnabled";
        private const string PlayOptionsValue = "Unity63Ci.Smoke.PlayOptionsValue";
        private const string DomainBeforePlay = "Unity63Ci.Smoke.DomainBeforePlay";
        [Test]
        public void VersionAndRegistryAreTheIntendedEditor()
        {
            Assert.That(Application.unityVersion, Is.EqualTo("6000.3.5f2"));
            Assert.That(Bootstrap.ToolRegistry, Is.Not.Null);
            Assert.That(Bootstrap.ToolRegistry.TryGet("read_hierarchy", out _), Is.True);
            Assert.That((bool)OperationalReport.SelfTest()["valid"], Is.True);
        }

        [UnityTest]
        public IEnumerator PlayModeWithDomainReloadRunsFramesAndReadTool() => CheckPlayMode(false);

        [UnityTest]
        public IEnumerator PlayModeWithoutDomainReloadRunsFramesAndReadTool() => CheckPlayMode(true);

        [UnityTest]
        public IEnumerator PlayModeWithoutSceneReloadRunsFramesAndReadTool() => CheckPlayMode(false, true);

        [UnityTest]
        public IEnumerator PlayModeWithoutDomainOrSceneReloadRunsFramesAndReadTool() => CheckPlayMode(true, true);

        private static IEnumerator CheckPlayMode(bool disableDomainReload, bool disableSceneReload = false)
        {
            if (!SessionState.GetBool(PlayOptionsSaved, false))
            {
                SessionState.SetBool(PlayOptionsSaved, true);
                SessionState.SetBool(PlayOptionsEnabled, EditorSettings.enterPlayModeOptionsEnabled);
                SessionState.SetInt(PlayOptionsValue, (int)EditorSettings.enterPlayModeOptions);
                SessionState.SetString(DomainBeforePlay, Bootstrap.DomainLoadedUtc.ToString("O"));
            }
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = (disableDomainReload
                ? EnterPlayModeOptions.DisableDomainReload : EnterPlayModeOptions.None)
                | (disableSceneReload ? EnterPlayModeOptions.DisableSceneReload : EnterPlayModeOptions.None);
            yield return new EnterPlayMode(expectDomainReload: !disableDomainReload);
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload), Is.EqualTo(disableDomainReload));
            Assert.That(EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableSceneReload), Is.EqualTo(disableSceneReload));
            var previousDomain = SessionState.GetString(DomainBeforePlay, "");
            Assert.That(previousDomain, Is.Not.Empty);
            if (disableDomainReload) Assert.That(Bootstrap.DomainLoadedUtc.ToString("O"), Is.EqualTo(previousDomain));
            else Assert.That(Bootstrap.DomainLoadedUtc.ToString("O"), Is.Not.EqualTo(previousDomain));
            var frame = Time.frameCount;
            yield return null;
            yield return null;
            Assert.That(Time.frameCount, Is.GreaterThan(frame));
            var read = new ReadHierarchyTool().Execute(new JObject { ["limit"] = 5, ["fields"] = new JArray() });
            Assert.That(read.IsError, Is.False, read.Text);
            Assert.That(read.Json["snapshotVersion"], Is.Not.Null);
            yield return new ExitPlayMode();
            Assert.That(Application.isPlaying, Is.False);
        }

        [UnityTest]
        public IEnumerator ScriptCompilationReloadRecreatesAUsableRegistry()
        {
            File.WriteAllText("Assets/AgentEnvironmentCompileProbe.cs",
                "public static class AgentEnvironmentCompileProbe { public const string Marker = \"" + Guid.NewGuid().ToString("N") + "\"; }");
            // UTF performs the refresh while preserving its own reload state.
            yield return new RecompileScripts();
            Assert.That(EditorApplication.isCompiling, Is.False);
            Assert.That(EditorUtility.scriptCompilationFailed, Is.False);
            Assert.That(Bootstrap.ToolRegistry, Is.Not.Null);
            var health = new GetServerHealthTool().Execute(new JObject { ["compact"] = true });
            Assert.That(health.IsError, Is.False, health.Text);
            Assert.That((bool)health.Json["selfTest"]["valid"], Is.True);
        }

        [UnityTearDown]
        public IEnumerator RestoreEditorMode()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
            if (SessionState.GetBool(PlayOptionsSaved, false))
            {
                EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(PlayOptionsEnabled, false);
                EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt(PlayOptionsValue, 0);
                SessionState.EraseBool(PlayOptionsSaved);
                SessionState.EraseBool(PlayOptionsEnabled);
                SessionState.EraseInt(PlayOptionsValue);
                SessionState.EraseString(DomainBeforePlay);
            }
        }
    }
}
