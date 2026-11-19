using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class McpExecutionSafetyTests
    {
        private const string Root = "Assets/__SceneAnalyzerExecutionSafety";
        private bool _allowWrites;

        [SetUp]
        public void SetUp()
        {
            _allowWrites = Settings.AllowWrites;
            Settings.AllowWrites = true;
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerExecutionSafety");
            SaveScene();
        }

        [TearDown]
        public void TearDown()
        {
            Settings.AllowWrites = _allowWrites;
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void Catalog_ModuleMetadataMatchesOperationalInventory()
        {
            var registry = Bootstrap.ToolRegistry;
            using var processor = new McpRequestProcessor(registry, 1, 10);
            var inventory = OperationalReport.Inventory(registry, new JObject { ["limit"] = 1000 });
            var modules = inventory["tools"].OfType<JObject>().ToDictionary(
                item => (string)item["name"], item => (string)item["module"], StringComparer.Ordinal);
            var catalog = processor.ListTools();
            foreach (var tool in catalog["tools"].OfType<JObject>())
            {
                var name = (string)tool["name"];
                Assert.That((string)tool["_meta"]?["module"], Is.EqualTo(modules[name]), name);
            }
        }

        [Test]
        public void StableIdentity_DoesNotRebindToReplacementAtSameHierarchyPath()
        {
            var original = new GameObject("Original");
            SaveScene();
            var target = ToolTargetRef.Capture(original);
            Object.DestroyImmediate(original);
            var replacement = new GameObject("Original");
            SaveScene();
            TestContext.WriteLine($"original={target.ToJson()} replacement={ToolTargetRef.Capture(replacement).ToJson()} nativeOld={EditorUtility.InstanceIDToObject(target.InstanceId)}");

            Assert.That(ToolTargetResolver.Resolve(target, out var error), Is.Null);
            StringAssert.StartsWith("TARGET_IDENTITY_STALE", error);
            Assert.That(replacement, Is.Not.Null);
        }

        [Test]
        public void StableIdentity_PreservesExactComponentAfterEarlierComponentIsRemoved()
        {
            var owner = new GameObject("Components");
            var first = owner.AddComponent<BoxCollider>();
            var expected = owner.AddComponent<BoxCollider>();
            owner.AddComponent<BoxCollider>();
            SaveScene();
            var target = ToolTargetRef.Capture(expected);
            Object.DestroyImmediate(first);

            Assert.That(ToolTargetResolver.Resolve(target, out var error), Is.SameAs(expected), error);
        }

        [Test]
        public void StableIdentity_AssetGuidCannotFallBackToReplacementPath()
        {
            var asset = CreateAsset("Original.asset");
            var target = ToolTargetRef.Capture(asset);
            target.GlobalObjectId = null; // Exercise the GUID-only identity contract too.
            AssetDatabase.DeleteAsset(Root + "/Original.asset");
            var replacement = CreateAsset("Original.asset");
            TestContext.WriteLine($"assetOriginal={target.ToJson()} replacement={ToolTargetRef.Capture(replacement).ToJson()} nativeOld={EditorUtility.InstanceIDToObject(target.InstanceId)}");

            Assert.That(ToolTargetResolver.Resolve(target, out var error), Is.Null);
            StringAssert.StartsWith("TARGET_IDENTITY_STALE", error);
        }

        [Test]
        public void TransientIdentity_UsesLiveInstanceButNeverRebindsByName()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var original = new GameObject("Transient");
            var target = ToolTargetRef.Capture(original);
            TestContext.WriteLine($"transient={target.ToJson()} rawGlobal={GlobalObjectId.GetGlobalObjectIdSlow(original)}");
            Assert.That(target.GlobalObjectId, Is.Null, "An unsaved object's null global ID must not be advertised as durable identity.");
            Assert.That(ToolTargetResolver.Resolve(target, out var liveError), Is.SameAs(original), liveError);
            Object.DestroyImmediate(original);
            new GameObject("Transient");
            Assert.That(ToolTargetResolver.Resolve(target, out _), Is.Null);
        }

        [Test]
        public void CapturedIdentity_DurableTargetResolvesAcrossDomainBoundary()
        {
            var original = new GameObject("Durable");
            SaveScene();
            var target = ToolTargetRef.FromJson(ToolTargetRef.Capture(original).ToJson());
            target.CaptureDomainId = "previous-domain";
            target.InstanceId = int.MaxValue;
            Assert.That(ToolTargetResolver.Resolve(target, out var error), Is.SameAs(original), error);
        }

        [Test]
        public void CapturedIdentity_TransientTargetExpiresAcrossDomainBoundary()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var original = new GameObject("Transient");
            var target = ToolTargetRef.FromJson(ToolTargetRef.Capture(original).ToJson());
            target.CaptureDomainId = "previous-domain";
            Assert.That(ToolTargetResolver.Resolve(target, out var error), Is.Null);
            StringAssert.StartsWith("TARGET_SESSION_EXPIRED", error);
        }

        [UnityTest]
        public IEnumerator StableTargetResults_RemainReusableAndLegacyObjectsAreEnriched()
        {
            var original = new GameObject("ReusableTarget");
            var component = original.AddComponent<BoxCollider>();
            SaveScene();
            var expectedRoot = ToolTargetRef.Capture(original).ToJson();
            var expectedComponent = ToolTargetRef.Capture(component).ToJson();
            var producer = new TargetResultTool("target_result_nested", new JObject
            {
                ["model"] = new JObject { ["editableRoot"] = expectedRoot.DeepClone() },
                ["references"] = new JArray(expectedComponent.DeepClone()),
                ["legacy"] = new JObject { ["instanceId"] = original.GetInstanceID(), ["name"] = original.name },
            });
            var directProducer = new TargetResultTool("target_result_direct", expectedRoot);
            var consumer = new TargetConsumerTool();
            using var processor = Processor(producer, directProducer, consumer);
            var produced = Call(processor, producer.Name, 1);
            yield return Await(produced);
            var response = Response(produced);
            Assert.That((bool)response["isError"], Is.False, response.ToString());
            var json = response["structuredContent"];
            var reusable = (JObject)json["model"]["editableRoot"];
            Assert.That(JToken.DeepEquals(reusable, expectedRoot), Is.True, reusable.ToString());
            Assert.That(JToken.DeepEquals(json["references"][0], expectedComponent), Is.True);
            Assert.That(reusable["target"], Is.Null, "A full TargetRef must not receive a nested target field.");
            Assert.That(JToken.DeepEquals(json["legacy"]["target"], expectedRoot), Is.True,
                "Legacy objects with instanceId must still receive a stable target.");

            // Reuse the complete processor result, without removing or adapting any fields.
            var consumed = Call(processor, consumer.Name, 2, new JObject { ["target"] = reusable.DeepClone() });
            yield return Await(consumed);
            Assert.That((bool)Response(consumed)["isError"], Is.False, Response(consumed).ToString());
            Assert.That(consumer.LastTarget, Is.SameAs(original));
            Assert.That(consumer.Calls, Is.EqualTo(1));

            var legacyConsumed = Call(processor, consumer.Name, 3,
                new JObject { ["target"] = json["legacy"]["target"].DeepClone() });
            yield return Await(legacyConsumed);
            Assert.That((bool)Response(legacyConsumed)["isError"], Is.False, Response(legacyConsumed).ToString());
            Assert.That(consumer.LastTarget, Is.SameAs(original));
            Assert.That(consumer.Calls, Is.EqualTo(2));

            var direct = Call(processor, directProducer.Name, 4);
            yield return Await(direct);
            Assert.That((bool)Response(direct)["isError"], Is.False, Response(direct).ToString());
            Assert.That(JToken.DeepEquals(Response(direct)["structuredContent"], expectedRoot), Is.True);
        }

        [UnityTest]
        public IEnumerator QueuedWrite_RevalidatesRevokedWritePermission()
        {
            var blocker = new BlockingTool();
            var probe = new ProbeTool();
            using var processor = Processor(blocker, probe);
            var first = Call(processor, blocker.Name, 1);
            try
            {
                Assert.That(blocker.Started, Is.True);
                var queued = Call(processor, probe.Name, 2);
                Assert.That(processor.InFlightCount, Is.EqualTo(2));
                Settings.AllowWrites = false;
                blocker.Complete();
                yield return Await(first);
                yield return Await(queued);
                Assert.That(probe.Calls, Is.Zero);
                Assert.That((string)Response(queued)["structuredContent"]?["code"], Is.EqualTo("WRITE_DISABLED"));
            }
            finally { blocker.Complete(); }
        }

        [UnityTest]
        public IEnumerator QueuedWrite_RevalidatesOriginalTargetIdentity()
        {
            var original = new GameObject("QueuedTarget");
            SaveScene();
            var target = ToolTargetRef.Capture(original);
            var blocker = new BlockingTool();
            var probe = new ProbeTool();
            using var processor = Processor(blocker, probe);
            var first = Call(processor, blocker.Name, 1);
            try
            {
                var queued = Call(processor, probe.Name, 2, new JObject { ["target"] = target.ToJson() });
                Assert.That(processor.InFlightCount, Is.EqualTo(2));
                Object.DestroyImmediate(original);
                new GameObject("QueuedTarget");
                SaveScene();
                blocker.Complete();
                yield return Await(first);
                yield return Await(queued);
                Assert.That(probe.Calls, Is.Zero);
                Assert.That((string)Response(queued)["structuredContent"]?["code"], Is.EqualTo("TARGET_NOT_FOUND"));
            }
            finally { blocker.Complete(); }
        }

        [UnityTest]
        public IEnumerator CompletedWrite_PreservesKnownResultWhenCancellationArrivesDuringInvocation()
        {
            using var cancellation = new CancellationTokenSource();
            var probe = new ProbeTool { OnExecute = cancellation.Cancel };
            using var processor = Processor(probe);
            var call = Call(processor, probe.Name, 1, token: cancellation.Token);
            yield return Await(call);

            var result = Response(call);
            Assert.That(probe.Calls, Is.EqualTo(1));
            Assert.That((bool)result["isError"], Is.False, result.ToString());
            Assert.That((string)result["_meta"]?["executionReceipt"]?["status"], Is.EqualTo("completed"));
            Assert.That((bool?)result["_meta"]?["executionReceipt"]?["finalizationCompleted"], Is.True);
        }

        [UnityTest]
        public IEnumerator InterruptedWrite_ReportsUnknownOutcomeInsteadOfSafeCancellation()
        {
            var probe = new ProbeTool { OnExecute = () => throw new OperationCanceledException() };
            using var processor = Processor(probe);
            var call = Call(processor, probe.Name, 1);
            yield return Await(call);

            var result = Response(call);
            Assert.That(probe.Calls, Is.EqualTo(1));
            Assert.That((string)result["structuredContent"]?["code"], Is.EqualTo("OUTCOME_UNKNOWN"));
            Assert.That((bool?)result["_meta"]?["executionReceipt"]?["resultKnown"], Is.False);
        }

        [TestCase("set_so_property")]
        [TestCase("modify_array_property")]
        public void FailedChangeset_RestoresExistingAssetInMemoryAndLeavesDiskUnchanged(string kind)
        {
            var asset = CreateAsset("Existing.asset");
            var path = AssetDatabase.GetAssetPath(asset);
            var before = File.ReadAllBytes(path);
            // Schema-valid, live target; the second operation fails inside its handler,
            // after the first operation actually mutates the ScriptableObject.
            var missingComponent = new GameObject("NoAudioSource");
            var firstArgs = kind == "set_so_property"
                ? new JObject { ["assetGuidOrPath"] = path, ["propertyPath"] = "integer", ["value"] = 42 }
                : new JObject { ["assetGuidOrPath"] = path, ["propertyPath"] = "values", ["action"] = "append", ["value"] = 42 };
            var result = new ApplyChangesetTool().Execute(new JObject
            {
                ["ops"] = new JArray
                {
                    new JObject { ["kind"] = kind, ["args"] = firstArgs },
                    new JObject { ["kind"] = "set_property", ["args"] = new JObject
                    {
                        ["instanceId"] = missingComponent.GetInstanceID(), ["componentType"] = "AudioSource",
                        ["propertyPath"] = "m_Volume", ["value"] = 0.5,
                    } },
                },
            });

            Assert.That(result.IsError, Is.True, result.Text);
            Assert.That((int)result.Json["opsApplied"], Is.EqualTo(1), "The failure must occur after the SO mutation, not during preflight.");
            Assert.That((string)result.Json["receipt"]?["outcome"], Is.EqualTo("rolledBack"));
            Assert.That((bool?)result.Json["receipt"]?["rollbackComplete"], Is.True);
            Assert.That(asset.integer, Is.EqualTo(7));
            CollectionAssert.AreEqual(new[] { 1, 2 }, asset.values);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var persisted = AssetDatabase.LoadAssetAtPath<McpSafetyTestAsset>(path);
            Assert.That(persisted.integer, Is.EqualTo(7));
            CollectionAssert.AreEqual(new[] { 1, 2 }, persisted.values);
        }

        [Test]
        public void SuccessfulChangeset_CommitsAssetAndStandaloneToolStillSaves()
        {
            var asset = CreateAsset("Commit.asset");
            var path = AssetDatabase.GetAssetPath(asset);
            var args = new JObject { ["assetGuidOrPath"] = path, ["propertyPath"] = "integer", ["value"] = 42 };
            var result = new ApplyChangesetTool().Execute(new JObject
            {
                ["ops"] = new JArray { new JObject { ["kind"] = "set_so_property", ["args"] = args } },
            });
            Assert.That(result.IsError, Is.False, result.Text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Assert.That(AssetDatabase.LoadAssetAtPath<McpSafetyTestAsset>(path).integer, Is.EqualTo(42));

            args["value"] = 81;
            var standalone = ScriptableObjectAuthor.SetProperty(args);
            Assert.That(standalone.Ok, Is.True, standalone.Error);
            Assert.That((bool)standalone.Data["savedAssets"], Is.True);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Assert.That(AssetDatabase.LoadAssetAtPath<McpSafetyTestAsset>(path).integer, Is.EqualTo(81));
        }

        private static McpSafetyTestAsset CreateAsset(string filename)
        {
            var asset = ScriptableObject.CreateInstance<McpSafetyTestAsset>();
            asset.integer = 7;
            AssetDatabase.CreateAsset(asset, Root + "/" + filename);
            AssetDatabase.SaveAssetIfDirty(asset);
            return asset;
        }

        private static void SaveScene() => Assert.That(
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), Root + "/Host.unity"), Is.True);

        private static McpRequestProcessor Processor(params IMcpTool[] tools)
        {
            var registry = new McpToolRegistry();
            foreach (var tool in tools) registry.Register(tool);
            // A run_tests job started over MCP owns the Editor-wide gate for the whole run.
            return new McpRequestProcessor(registry, 4, 10, new SemaphoreSlim(1, 1));
        }

        private static Task<McpProcessOutcome> Call(McpRequestProcessor processor, string name, int id,
            JObject arguments = null, CancellationToken token = default) => processor.ProcessAsync(new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() },
        }.ToString(), McpProtocolNegotiator.Preferred, "test", token);

        private static JObject Response(Task<McpProcessOutcome> call) => (JObject)JObject.Parse(call.Result.Body)["result"];

        private static IEnumerator Await(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "MCP test request did not settle.");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            Assert.That(task.IsCanceled, Is.False);
        }

        private sealed class BlockingTool : IMcpTool
        {
            private readonly TaskCompletionSource<ToolResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public string Name => "safety_blocker";
            public string Description => Name;
            public JObject InputSchema => new JObject { ["type"] = "object" };
            public bool IsWrite => true;
            public bool Started { get; private set; }
            public ToolResult Execute(JObject args) => throw new NotSupportedException();
            public Task<ToolResult> ExecuteAsync(JObject args) { Started = true; return _completion.Task; }
            public void Complete() => _completion.TrySetResult(ToolResult.Ok("released"));
        }

        private sealed class TargetResultTool : IMcpTool
        {
            private readonly JToken _result;
            public TargetResultTool(string name, JToken result) { Name = name; _result = result.DeepClone(); }
            public string Name { get; }
            public string Description => Name;
            public JObject InputSchema => new JObject { ["type"] = "object" };
            public bool IsWrite => false;
            public ToolResult Execute(JObject args) => ToolResult.Ok("captured", _result.DeepClone());
        }

        private sealed class TargetConsumerTool : IMcpTool
        {
            public string Name => "target_result_consumer";
            public string Description => Name;
            public JObject InputSchema => new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["target"] = SchemaHelpers.TargetRefSchema },
                ["required"] = new JArray("target"), ["additionalProperties"] = false,
            };
            public bool IsWrite => false;
            public int Calls;
            public Object LastTarget;
            public ToolResult Execute(JObject args)
            {
                Calls++;
                LastTarget = ToolTargetResolver.Resolve(ToolTargetRef.FromJson(args["target"]), out var error);
                return LastTarget != null ? ToolResult.Ok("resolved") : ToolResult.Error("TARGET_NOT_FOUND", error);
            }
        }

        private sealed class ProbeTool : IMcpTool
        {
            public string Name => "safety_probe";
            public string Description => Name;
            public JObject InputSchema => new JObject
            {
                ["type"] = "object", ["properties"] = new JObject { ["instanceId"] = new JObject { ["type"] = "integer" } },
                ["additionalProperties"] = false,
            };
            public bool IsWrite => true;
            public int Calls;
            public Action OnExecute;
            public ToolResult Execute(JObject args)
            {
                Calls++;
                OnExecute?.Invoke();
                return ToolResult.Ok("effect completed", new JObject { ["calls"] = Calls });
            }
        }
    }
}
