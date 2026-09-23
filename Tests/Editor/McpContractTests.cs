using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Broker;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect;
using Maicon.SceneAnalyzer.Editor.PrefabTools;
using Maicon.SceneAnalyzer.ReflectionTestSurface;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class McpContractTests
    {
        private sealed class StubTool : IMcpTool
        {
            private readonly string _name;
            private readonly bool _write;
            private readonly JObject _schema;
            private readonly JObject _outputSchema;

            public StubTool(string name, bool write = false, JObject schema = null, JObject outputSchema = null)
            {
                _name = name;
                _write = write;
                _outputSchema = outputSchema;
                _schema = schema ?? new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject(),
                    ["additionalProperties"] = false,
                };
            }

            public string Name => _name;
            public string Description => _name;
            public JObject InputSchema => _schema;
            public JObject OutputSchema => _outputSchema;
            public bool IsWrite => _write;
            public ToolResult Execute(JObject arguments) => ToolResult.Ok("ok");
        }

        private sealed class DefaultOutputTool : IMcpTool
        {
            public DefaultOutputTool(string name) { Name = name; }
            public string Name { get; }
            public string Description => Name;
            public JObject InputSchema => new JObject { ["type"] = "object" };
            public bool IsWrite => false;
            public ToolResult Execute(JObject arguments) => ToolResult.Ok("ok");
        }

        private sealed class OneShotSchemaTool : IMcpTool
        {
            private int _inputReads;
            private int _outputReads;

            public string Name => "one_shot_schema";
            public string Description => Name;
            public int InputReads => Volatile.Read(ref _inputReads);
            public int OutputReads => Volatile.Read(ref _outputReads);
            public JObject InputSchema
            {
                get
                {
                    if (Interlocked.Increment(ref _inputReads) != 1)
                        throw new InvalidOperationException("InputSchema was evaluated after registration.");
                    return new JObject { ["type"] = "object" };
                }
            }
            public JObject OutputSchema
            {
                get
                {
                    if (Interlocked.Increment(ref _outputReads) != 1)
                        throw new InvalidOperationException("OutputSchema was evaluated after registration.");
                    return new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject { ["value"] = new JObject { ["type"] = "integer" } },
                        ["required"] = new JArray { "value" },
                        ["additionalProperties"] = false,
                    };
                }
            }
            public bool IsWrite => false;
            public ToolResult Execute(JObject arguments) => ToolResult.Ok("ok");
        }

        [Test]
        public void ProtocolNegotiator_SupportsLegacyAndModernVersions()
        {
            CollectionAssert.AreEqual(
                new[] { "2025-06-18", "2025-03-26", "2024-11-05" },
                McpProtocolNegotiator.Supported);
            Assert.That(McpProtocolNegotiator.Negotiate("2024-11-05"), Is.EqualTo("2024-11-05"));
            Assert.That(McpProtocolNegotiator.Negotiate("unsupported"), Is.EqualTo("2025-06-18"));
            Assert.That(McpProtocolNegotiator.IsModern("2025-06-18"), Is.True);
            Assert.That(McpProtocolNegotiator.IsModern("2024-11-05"), Is.False);
        }

        [Test]
        public async Task RequestCoordinator_RejectsDuplicateIdsWithoutLosingOriginalLease()
        {
            using (var coordinator = new McpRequestCoordinator(2))
            using (var first = await coordinator.BeginAsync("same-id", false, CancellationToken.None, TimeSpan.FromSeconds(1)))
            {
                Assert.That(coordinator.InFlightCount, Is.EqualTo(1));
                Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await coordinator.BeginAsync("same-id", false, CancellationToken.None, TimeSpan.FromSeconds(1)));
                Assert.That(coordinator.InFlightCount, Is.EqualTo(1));
                Assert.That(coordinator.Cancel("same-id"), Is.True);
                Assert.That(first.CancellationToken.IsCancellationRequested, Is.True);
            }
        }

        [Test]
        public async Task RequestCoordinator_TimeoutBeforeSlotDoesNotLeakCapacity()
        {
            using (var coordinator = new McpRequestCoordinator(1))
            {
                var first = await coordinator.BeginAsync("first", false, CancellationToken.None, TimeSpan.FromSeconds(1));
                try
                {
                    try
                    {
                        await coordinator.BeginAsync(
                            "timed-out",
                            false,
                            CancellationToken.None,
                            TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                        Assert.Fail("the queued request should time out before acquiring a concurrency slot");
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected: avoid NUnit's synchronous ThrowsAsync adapter,
                        // which blocks Unity's editor loop around an already-async test.
                    }
                    Assert.That(coordinator.InFlightCount, Is.EqualTo(1));
                }
                finally
                {
                    first.Dispose();
                }

                using (var next = await coordinator.BeginAsync("next", false, CancellationToken.None, TimeSpan.FromSeconds(1)))
                    Assert.That(coordinator.InFlightCount, Is.EqualTo(1));
                Assert.That(coordinator.InFlightCount, Is.Zero);
            }
        }

        [Test]
        public async Task RequestCoordinator_DisposeCancelsActiveLeaseAndRejectsNewRequests()
        {
            var coordinator = new McpRequestCoordinator(1);
            var lease = await coordinator.BeginAsync(null, true, CancellationToken.None, TimeSpan.FromSeconds(1));
            coordinator.Dispose();

            Assert.That(lease.CancellationToken.IsCancellationRequested, Is.True);
            Assert.DoesNotThrow(() => lease.Dispose());
            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await coordinator.BeginAsync("after-dispose", false, CancellationToken.None, TimeSpan.FromSeconds(1)));
        }

        [UnityTest]
        public IEnumerator HttpServer_ListsAndCallsToolsWithoutEditorPrefsThreadErrors()
        {
            var registry = new McpToolRegistry();
            registry.Register(new StubTool("thread_probe"));
            var server = new HttpMcpServer(registry);
            var port = FindFreePort();
            var token = Guid.NewGuid().ToString("N");

            try
            {
                server.Start(port, token);

                var initializeTask = PostMcpAsync(port, token, 1, "initialize", new JObject
                {
                    ["protocolVersion"] = McpProtocolNegotiator.Preferred,
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "unity-editmode-test", ["version"] = "1.0.0" },
                });
                while (!initializeTask.IsCompleted) yield return null;
                Assert.That(initializeTask.IsFaulted, Is.False, initializeTask.Exception?.ToString());
                var initializeResponse = initializeTask.Result;
                Assert.That(initializeResponse["error"], Is.Null, initializeResponse.ToString());
                Assert.That((string)initializeResponse["result"]?["protocolVersion"],
                    Is.EqualTo(McpProtocolNegotiator.Preferred));

                var listTask = PostMcpAsync(port, token, 2, "tools/list", new JObject());
                while (!listTask.IsCompleted) yield return null;
                Assert.That(listTask.IsFaulted, Is.False, listTask.Exception?.ToString());
                var listResponse = listTask.Result;
                Assert.That(listResponse["error"], Is.Null, listResponse.ToString());
                Assert.That((int?)listResponse["result"]?["_meta"]?["registryCount"], Is.EqualTo(1));
                Assert.That((string)listResponse["result"]?["tools"]?[0]?["name"], Is.EqualTo("thread_probe"));

                var callTask = PostMcpAsync(port, token, 3, "tools/call", new JObject
                {
                    ["name"] = "thread_probe",
                    ["arguments"] = new JObject(),
                });
                while (!callTask.IsCompleted) yield return null;
                Assert.That(callTask.IsFaulted, Is.False, callTask.Exception?.ToString());
                var callResponse = callTask.Result;
                Assert.That(callResponse["error"], Is.Null, callResponse.ToString());
                Assert.That(callResponse["result"], Is.Not.Null, callResponse.ToString());
            }
            finally
            {
                server.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator WaitForCompileClean_StartsSafelyFromWorkerThread()
        {
            var task = Task.Run(async () => await new WaitForCompileCleanTool().ExecuteAsync(new JObject
            {
                ["timeoutSeconds"] = 5,
                ["includeWarnings"] = false,
            }));

            while (!task.IsCompleted) yield return null;

            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            Assert.That(task.IsCanceled, Is.False);
            Assert.That(task.Result, Is.Not.Null);
            StringAssert.DoesNotContain("main thread", task.Result.Text ?? string.Empty);
        }

        [Test]
        public void BrokerBridgeClient_TracksToolsRegisteredAfterConstruction()
        {
            var registry = new McpToolRegistry();
            registry.Register(new StubTool("initial_tool"));
            using var processor = new McpRequestProcessor(registry, 1, 10);
            using var bridge = new BrokerBridgeClient(
                processor,
                registry,
                "C:/late-registry-test",
                "ws://127.0.0.1:8765/bridge",
                "test-token",
                1024 * 1024);
            var before = bridge.AdvertisedRegistryHash;

            registry.Register(new StubTool("late_tool"));

            Assert.That(bridge.AdvertisedRegistryHash, Is.EqualTo(registry.VersionHash));
            Assert.That(bridge.AdvertisedRegistryHash, Is.Not.EqualTo(before));
        }

        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static async Task<JObject> PostMcpAsync(
            int port, string token, int id, string method, JObject parameters)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpProtocolNegotiator.Preferred);
            request.Content = new StringContent(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JObject(),
            }.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            return JObject.Parse(body);
        }

        [Test]
        public void ConfirmationToken_IsCanonicalBoundAndOneShot()
        {
            var service = new ConfirmationTokenService();
            var issuedFor = new JObject
            {
                ["nested"] = new JObject { ["b"] = 2, ["a"] = 1 },
                ["value"] = "same",
            };
            var reordered = new JObject
            {
                ["value"] = "same",
                ["nested"] = new JObject { ["a"] = 1, ["b"] = 2 },
            };

            var token = service.Issue("danger", issuedFor, "project-a", TimeSpan.FromMinutes(1));
            Assert.That(service.TryConsume(token, "danger", reordered, "project-a"), Is.True);
            Assert.That(service.TryConsume(token, "danger", reordered, "project-a"), Is.False);

            var wrongProject = service.Issue("danger", issuedFor, "project-a", TimeSpan.FromMinutes(1));
            Assert.That(service.TryConsume(wrongProject, "danger", issuedFor, "project-b"), Is.False);
            var wrongArguments = service.Issue("danger", issuedFor, "project-a", TimeSpan.FromMinutes(1));
            issuedFor["value"] = "changed";
            Assert.That(service.TryConsume(wrongArguments, "danger", issuedFor, "project-a"), Is.False);
        }

        [Test]
        public void SchemaValidator_EnforcesShapeAndScalarConstraints()
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["name"] = new JObject { ["type"] = "string", ["minLength"] = 2, ["maxLength"] = 4 },
                    ["items"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                        ["minItems"] = 1,
                        ["maxItems"] = 2,
                    },
                },
                ["required"] = new JArray { "name", "items" },
                ["additionalProperties"] = false,
                ["minProperties"] = 2,
            };

            Assert.That(ToolSchemaValidator.ValidateSchema(schema, out var schemaError), Is.True, schemaError);
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["name"] = "abc", ["items"] = new JArray { 1, 2 } }, schema, out var validError),
                Is.True, validError);
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["name"] = "x", ["items"] = new JArray { 0, 2, 3 }, ["extra"] = true }, schema, out var invalidError),
                Is.False);
            StringAssert.Contains("minLength", invalidError);
            StringAssert.Contains("below minimum", invalidError);
            StringAssert.Contains("too many items", invalidError);
            StringAssert.Contains("unknown property", invalidError);
        }

        [Test]
        public void SchemaValidator_RejectsUnknownAndMalformedTypeDeclarations()
        {
            Assert.That(ToolSchemaValidator.ValidateSchema(new JObject
            {
                ["type"] = "strng",
            }, out var unknownError), Is.False);
            StringAssert.Contains("unsupported type", unknownError);

            Assert.That(ToolSchemaValidator.ValidateSchema(new JObject
            {
                ["type"] = new JArray { "string", 42 },
            }, out var malformedError), Is.False);
            StringAssert.Contains("type name must be a string", malformedError);

            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["value"] = "anything" },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["value"] = new JObject { ["type"] = "strng" },
                    },
                },
                out _),
                Is.False);
        }

        [Test]
        public void SchemaValidator_OneOfRequiresExactlyOneMatch()
        {
            var schema = new JObject
            {
                ["oneOf"] = new JArray
                {
                    new JObject { ["type"] = "number" },
                    new JObject { ["type"] = new JArray { "number", "string" } },
                },
            };
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject { ["value"] = 1 }, new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["value"] = schema },
                ["required"] = new JArray { "value" },
                ["additionalProperties"] = false,
            }, out var error), Is.False);
            StringAssert.Contains("exactly one", error);
        }

        [Test]
        public void Registry_RejectsDuplicatesAndInvalidSchemas()
        {
            var registry = new McpToolRegistry();
            registry.Register(new StubTool("first"));
            Assert.Throws<InvalidOperationException>(() => registry.Register(new StubTool("first")));
            Assert.Throws<InvalidOperationException>(() => registry.Register(new StubTool("bad", schema: new JObject
            {
                ["type"] = "array",
            })));
            Assert.Throws<InvalidOperationException>(() => registry.Register(new StubTool("bad-output", outputSchema: new JObject
            {
                ["type"] = "array",
            })));
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.VersionHash, Has.Length.EqualTo(64));
        }

        [Test]
        public void RegistryHash_ChangesWhenDescriptorChanges()
        {
            var registry = new McpToolRegistry();
            registry.Register(new StubTool("hash_probe"));
            var before = registry.VersionHash;
            registry.RegisterDescriptor(new ToolDescriptor
            {
                Name = "hash_probe",
                Category = "changed",
                Capabilities = McpCapability.Read,
                Risk = McpRiskLevel.Low,
                ConfirmationPolicy = McpConfirmationPolicy.None,
                EditorMode = McpEditorMode.Any,
                IsIdempotent = true,
            });
            Assert.That(registry.VersionHash, Is.Not.EqualTo(before));
        }

        [Test]
        public async Task RegistryHash_DoesNotReevaluateSchemasOnWorkerThread()
        {
            var registry = new McpToolRegistry();
            var tool = new OneShotSchemaTool();
            registry.Register(tool);

            var hash = await Task.Run(() => registry.VersionHash);

            Assert.That(hash, Has.Length.EqualTo(64));
            Assert.That(tool.InputReads, Is.EqualTo(1));
            Assert.That(tool.OutputReads, Is.EqualTo(1));
        }

        [Test]
        public void ToolList_UsesSchemasCachedDuringRegistration()
        {
            var registry = new McpToolRegistry();
            var tool = new OneShotSchemaTool();
            registry.Register(tool);
            using var processor = new McpRequestProcessor(registry, 1, 10);

            // tools/list is intentionally dispatched to Unity's main thread by
            // McpRequestProcessor because authorization reads EditorPrefs. The
            // contract here is that listing reuses schemas captured at registration.
            var list = processor.ListTools();

            Assert.That((string)list["tools"]?[0]?["name"], Is.EqualTo(tool.Name));
            Assert.That((string)list["tools"]?[0]?["inputSchema"]?["type"], Is.EqualTo("object"));
            Assert.That((string)list["tools"]?[0]?["outputSchema"]?["type"], Is.EqualTo("object"));
            Assert.That(tool.InputReads, Is.EqualTo(1));
            Assert.That(tool.OutputReads, Is.EqualTo(1));
        }

        [Test]
        public void HighRiskConfirmationToggle_ControlsAdvertisedGateAndInventory()
        {
            var previous = Settings.RequireHighRiskConfirmation;
            var registry = new McpToolRegistry();
            registry.Register(new StubTool("remove_component", true));
            using var processor = new McpRequestProcessor(registry, 1, 10);
            try
            {
                Settings.RequireHighRiskConfirmation = false;
                var direct = processor.ListTools();
                Assert.That(direct["tools"]?[0]?["inputSchema"]?["properties"]?["_confirmationToken"], Is.Null);
                Assert.That((bool)direct["tools"]?[0]?["_meta"]?["confirmationRequired"], Is.False);
                var directInventory = OperationalReport.Inventory(registry);
                Assert.That((bool)directInventory["tools"]?[0]?["confirmationRequired"], Is.False);
                Assert.That((string)directInventory["tools"]?[0]?["confirmationPolicy"], Is.EqualTo("Required"));

                Settings.RequireHighRiskConfirmation = true;
                var gated = processor.ListTools();
                Assert.That(gated["tools"]?[0]?["inputSchema"]?["properties"]?["_confirmationToken"], Is.Not.Null);
                Assert.That((bool)gated["tools"]?[0]?["_meta"]?["confirmationRequired"], Is.True);
                var gatedInventory = OperationalReport.Inventory(registry);
                Assert.That((bool)gatedInventory["tools"]?[0]?["confirmationRequired"], Is.True);
            }
            finally
            {
                Settings.RequireHighRiskConfirmation = previous;
            }
        }

        [Test]
        public void ReadPrefab_AdvertisedOutputSchemaAcceptsStructuredToolErrors()
        {
            var tool = new ReadPrefabTool();
            var registry = new McpToolRegistry();
            registry.Register(tool);
            var result = tool.Execute(new JObject { ["prefabPath"] = "Packages/Outside.prefab" });
            var advertised = McpRequestProcessor.BuildAdvertisedOutputSchema(registry.GetOutputSchema(tool.Name));

            Assert.That(result.IsError, Is.True);
            Assert.That((string)advertised["type"], Is.EqualTo("object"));
            Assert.That(ToolSchemaValidator.ValidateSchema(advertised, out var schemaError), Is.True, schemaError);
            Assert.That(ToolSchemaValidator.ValidateArguments((JObject)result.Json, advertised, out var resultError),
                Is.True, resultError);
        }

        [Test]
        public void Registry_RegisterRangeIsAtomic()
        {
            var registry = new McpToolRegistry();
            registry.Register(new StubTool("existing"));

            Assert.Throws<InvalidOperationException>(() => registry.RegisterRange(new IMcpTool[]
            {
                new StubTool("new-valid"),
                new StubTool("existing"),
            }));
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TryGet("new-valid", out _), Is.False);

            Assert.Throws<InvalidOperationException>(() => registry.RegisterRange(new IMcpTool[]
            {
                new StubTool("also-valid"),
                new StubTool("bad", schema: new JObject { ["type"] = "array" }),
            }));
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TryGet("also-valid", out _), Is.False);

            Assert.Throws<InvalidOperationException>(() => registry.RegisterRange(new IMcpTool[]
            {
                new StubTool("missing-root-type", schema: new JObject
                {
                    ["oneOf"] = new JArray { new JObject { ["type"] = "object" } },
                }),
            }));
            Assert.That(registry.Count, Is.EqualTo(1));
        }

        [TestCase("configure_addressable_entry")]
        [TestCase("create_localization_locale")]
        [TestCase("set_localized_string")]
        [TestCase("write_shader_graph_source")]
        public void OptionalAssetWrites_RequireAssetWriteCapability(string name)
        {
            var descriptor = ToolDescriptor.Infer(new StubTool(name, true));
            Assert.That((descriptor.Capabilities & McpCapability.AssetWrite) != 0, Is.True, name);
            Assert.That(descriptor.EditorMode, Is.EqualTo(McpEditorMode.EditModeOnly));
        }

        [Test]
        public void EcosystemTools_HaveValidStructuredSchemas()
        {
            var names = new[]
            {
                "get_input_system_context", "read_input_action_asset", "create_input_action_asset",
                "add_input_action_map", "add_input_action", "add_input_binding", "add_input_control_scheme",
                "configure_player_input", "validate_input_setup",
                "get_localization_context", "list_localization_tables", "create_localization_locale",
                "set_localized_string", "validate_localization",
                "get_addressables_context", "list_addressable_entries", "configure_addressable_entry",
                "validate_addressables", "preview_addressables_build", "start_addressables_build",
                "get_addressables_build_job", "add_xr_grab_interactable", "validate_xr_interaction_setup",
                "create_probuilder_shape", "refresh_probuilder_mesh", "validate_probuilder_setup",
                "get_shader_graph_context", "list_shader_graph_assets", "read_shader_graph_source",
                "validate_shader_graph_assets", "write_shader_graph_source",
            };

            Assert.That(names.Length, Is.EqualTo(31));
            foreach (var name in names)
            {
                IMcpTool tool = new DefaultOutputTool(name);
                Assert.That(tool.OutputSchema, Is.Not.Null, name);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var error), Is.True,
                    $"{name}: {error}");
                Assert.That(tool.OutputSchema["properties"], Is.TypeOf<JObject>(), name);
                Assert.That(((JObject)tool.OutputSchema["properties"]).Count, Is.GreaterThan(0), name);
            }
        }

        [Test]
        public void ScreenshotAndPerformanceContracts_AreStructuredAndReadOnly()
        {
            var names = new[]
            {
                "screenshot_scene_view",
                "screenshot_game_view",
                "screenshot_from_pov",
                "get_performance_snapshot",
            };

            foreach (var name in names)
            {
                var descriptor = ToolDescriptor.Infer(new StubTool(name));
                Assert.That(descriptor.Capabilities, Is.EqualTo(McpCapability.Read), name);
                Assert.That(descriptor.Risk, Is.EqualTo(McpRiskLevel.Low), name);
                Assert.That(descriptor.EditorMode, Is.EqualTo(McpEditorMode.Any), name);
                Assert.That(descriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.None), name);
            }

            Assert.That(Bootstrap.ToolRegistry.TryGet("screenshot_scene_view", out var sceneView), Is.True);
            Assert.That(sceneView.OutputSchema, Is.Null, "image content blocks must not advertise structuredContent");
            Assert.That(Bootstrap.ToolRegistry.TryGet("screenshot_game_view", out var gameView), Is.True);
            Assert.That(gameView.Description, Does.Contain("Screen Space - Overlay"));
            Assert.That(gameView.Description, Does.Contain("cameraInstanceId"));
            Assert.That(Bootstrap.ToolRegistry.TryGet("get_performance_snapshot", out var performance), Is.True);
            Assert.That(performance.OutputSchema, Is.Not.Null);
            Assert.That(ToolSchemaValidator.ValidateSchema(performance.OutputSchema, out var error), Is.True, error);
            Assert.That(((JObject)performance.OutputSchema["properties"])["frameTimingFeatureEnabled"], Is.Not.Null);
            Assert.That(((JObject)performance.OutputSchema["properties"])["capturedFrameTimingCount"], Is.Not.Null);
            Assert.That(Bootstrap.ToolRegistry.TryGet("analyze_performance", out _), Is.False);
            Assert.That(Bootstrap.ToolRegistry.TryGet("validate_scene", out _), Is.False);
        }

        [Test]
        public void ExecutionRequirements_DescribeViewsUpdatesAndBackgroundJobs()
        {
            var scene = ToolDescriptor.Infer(new StubTool("screenshot_scene_view"));
            Assert.That(scene.ExecutionRequirements.HasFlag(McpExecutionRequirement.MainThread), Is.True);
            Assert.That(scene.ExecutionRequirements.HasFlag(McpExecutionRequirement.EditorUpdates), Is.True);
            Assert.That(scene.ExecutionRequirements.HasFlag(McpExecutionRequirement.SceneView), Is.True);
            Assert.That(scene.HeadlessSafe, Is.False);

            var game = ToolDescriptor.Infer(new StubTool("screenshot_game_view"));
            Assert.That(game.ExecutionRequirements.HasFlag(McpExecutionRequirement.GameView), Is.True);
            Assert.That(game.HeadlessSafe, Is.False);

            var tests = ToolDescriptor.Infer(new StubTool("run_tests", true));
            Assert.That(tests.ExecutionRequirements.HasFlag(McpExecutionRequirement.LongRunningJob), Is.True);
            Assert.That(tests.ExecutionRequirements.HasFlag(McpExecutionRequirement.EditorUpdates), Is.True);
        }

        [Test]
        public void EndOfFrameCapture_UsesRuntimeMonoBehaviourHost()
        {
            var awaiter = Type.GetType(
                "Maicon.SceneAnalyzer.Editor.Play.EndOfFrameAwaiter, com.maicon.scene-analyzer.Editor.Play",
                throwOnError: true);
            var runtimeHost = Type.GetType(
                "Maicon.SceneAnalyzer.EndOfFrameOperationHost, com.maicon.scene-analyzer",
                throwOnError: true);

            Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(awaiter), Is.False,
                "Editor-only MonoBehaviours cannot be attached to a runtime GameObject");
            Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(runtimeHost), Is.True);
        }

        [Test]
        public async Task GenericJobTools_CanWaitForAndCancelAnyJobKind()
        {
            var completed = ToolJobManager.Create("contract_test", canCancel: true);
            ToolJobManager.Start(completed, (_, __) => Task.FromResult<JToken>(
                new JObject { ["ok"] = true }));
            var waited = await ToolJobManager.WaitAsync(
                completed.Id, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.That(waited, Is.Not.Null);
            Assert.That(waited.Status, Is.EqualTo(ToolJobStatus.Completed));

            var cancellable = ToolJobManager.Create("contract_cancel", canCancel: true);
            var cancelResult = new CancelToolJobTool().Execute(
                new JObject { ["jobId"] = cancellable.Id });
            Assert.That(cancelResult.IsError, Is.False);
            Assert.That((string)cancelResult.Json["status"], Is.EqualTo("CancelRequested"));

            IMcpTool[] tools = { new GetToolJobTool(), new WaitToolJobTool(), new CancelToolJobTool() };
            foreach (var tool in tools)
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError),
                    Is.True, $"{tool.Name} input: {inputError}");
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError),
                    Is.True, $"{tool.Name} output: {outputError}");
            }
        }

        [TestCase("start_addressables_build")]
        [TestCase("write_shader_graph_source")]
        public void CriticalEcosystemTools_RequireConfirmationWhenRegistered(string name)
        {
            if (!Bootstrap.ToolRegistry.TryGet(name, out _)) Assert.Ignore($"optional tool not installed: {name}");
            var descriptor = Bootstrap.ToolRegistry.GetDescriptor(name);
            Assert.That(descriptor.Risk, Is.EqualTo(McpRiskLevel.Critical), name);
            Assert.That(descriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required), name);
        }

        [Test]
        public void OptionalModuleInventory_ReferencesRegisteredToolsOnly()
        {
            var registry = Bootstrap.ToolRegistry;
            Assert.That(registry, Is.Not.Null);
            var modules = OptionalModuleRegistry.All;
            Assert.That(modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(modules.Length));
            foreach (var module in modules)
            {
                Assert.That(module.Id, Is.Not.Empty);
                Assert.That(module.PackageId, Is.Not.Empty, module.Id);
                Assert.That(module.Tools, Is.Ordered, module.Id);
                foreach (var toolName in module.Tools)
                    Assert.That(registry.TryGet(toolName, out _), Is.True, $"{module.Id}: {toolName}");
            }
        }

        [TestCase("execute_menu_item", McpCapability.Reflection, McpRiskLevel.Critical)]
        [TestCase("invoke_static_method", McpCapability.Reflection, McpRiskLevel.Critical)]
        [TestCase("confirm_add_package", McpCapability.PackageManager, McpRiskLevel.Critical)]
        [TestCase("start_build", McpCapability.Build, McpRiskLevel.Critical)]
        [TestCase("apply_project_settings_change", McpCapability.ProjectSettings, McpRiskLevel.High)]
        [TestCase("confirm_delete_assets", McpCapability.Destructive, McpRiskLevel.High)]
        public void HighRiskTools_RequireExpectedCapabilityAndConfirmation(
            string name, McpCapability capability, McpRiskLevel risk)
        {
            var descriptor = ToolDescriptor.Infer(new StubTool(name, true));
            Assert.That((descriptor.Capabilities & capability) != 0, Is.True, descriptor.Capabilities.ToString());
            Assert.That(descriptor.Risk, Is.EqualTo(risk));
            Assert.That(descriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required));
            Assert.That(descriptor.IsWrite, Is.True);
        }

        [Test]
        public void ReflectionPolicy_BlocksFilesystemProcessAndModalMenuSurfaces()
        {
            var delete = typeof(System.IO.File).GetMethod("Delete", new[] { typeof(string) });
            Assert.That(ReflectionPolicy.ValidateMethod(delete, new JArray("any"), out var methodReason), Is.False);
            Assert.That(methodReason, Does.Contain("blocked").Or.Contain("allowlist"));
            Assert.That(ReflectionPolicy.ValidateMenuPath("File/Build Profiles...", out var menuReason), Is.False);
            StringAssert.Contains("blocked", menuReason);
            Assert.That(ReflectionPolicy.ValidateMenuPath("GameObject/Create Empty", out _), Is.True);
            Assert.That(ReflectionPolicy.ValidateMenuPath("Create Empty", out _), Is.False);
            Assert.That(ReflectionPolicy.ValidateMenuPath("GameObject/", out _), Is.False);
        }

        [Test]
        public void ReflectionPolicy_AllowsOnlySynchronousPublicMethodsWithoutSpecialTransport()
        {
            var probe = typeof(ReflectionPolicyProbe);
            Assert.That(ReflectionPolicy.ValidateMethod(probe.GetMethod(nameof(ReflectionPolicyProbe.PublicMethod)), new JArray(), out _), Is.True);
            Assert.That(ReflectionPolicy.ValidateMethod(probe.GetMethod("HiddenMethod", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic), new JArray(), out _), Is.False);
            Assert.That(ReflectionPolicy.ValidateMethod(probe.GetProperty(nameof(ReflectionPolicyProbe.Value)).GetGetMethod(), new JArray(), out _), Is.False);
            Assert.That(ReflectionPolicy.ValidateMethod(probe.GetMethod(nameof(ReflectionPolicyProbe.ParamsMethod)), new JArray(), out _), Is.False);
            Assert.That(ReflectionPolicy.ValidateMethod(probe.GetMethod(nameof(ReflectionPolicyProbe.AsyncMethod)), new JArray(), out _), Is.False);
        }

        [Test]
        public void ReflectionAndDiscoveryTools_HaveValidStructuredSchemas()
        {
            IMcpTool[] tools =
            {
                new ListMenuItemsTool(), new DescribeStaticMethodTool(),
                new ExecuteMenuItemTool(), new InvokeStaticMethodTool(),
                new ListComponentTypesTool(), new DescribeComponentTypeTool(),
            };
            foreach (var tool in tools)
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError), Is.True, $"{tool.Name} input: {inputError}");
                Assert.That(tool.OutputSchema, Is.Not.Null, tool.Name);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError), Is.True, $"{tool.Name} output: {outputError}");
            }
        }

        [Test]
        public void OperationalInventory_UsesRegistryDescriptorsAndFilters()
        {
            var registry = new McpToolRegistry();
            registry.Register(new StubTool("read_probe"));
            registry.Register(new StubTool("set_probe", true));

            var inventory = OperationalReport.Inventory(registry, new JObject
            {
                ["writeOnly"] = true,
                ["limit"] = 10,
            });

            Assert.That((int)inventory["registryCount"], Is.EqualTo(2));
            Assert.That((int)inventory["matched"], Is.EqualTo(1));
            Assert.That((string)inventory["tools"][0]["name"], Is.EqualTo("set_probe"));
            Assert.That((bool)inventory["tools"][0]["isWrite"], Is.True);
            Assert.That(inventory["tools"][0]["capabilities"], Is.TypeOf<JArray>());
        }

        [Test]
        public void OperationalInventory_PagesRegisteredSchemasWithoutReevaluatingTools()
        {
            var registry = new McpToolRegistry();
            var tool = new OneShotSchemaTool();
            registry.Register(new StubTool("a_probe"));
            registry.Register(tool);
            var first = OperationalReport.Inventory(registry, new JObject { ["includeSchemas"] = true, ["limit"] = 1 });
            Assert.That((int)first["matched"], Is.EqualTo(2));
            Assert.That((int)first["returned"], Is.EqualTo(1));
            Assert.That((bool)first["hasMore"], Is.True);
            var second = OperationalReport.Inventory(registry, new JObject { ["includeSchemas"] = true, ["limit"] = 1, ["offset"] = 1 });
            Assert.That((string)second["tools"][0]["name"], Is.EqualTo(tool.Name));
            Assert.That(second["tools"][0]["inputSchema"], Is.Not.Null);
            Assert.That(second["tools"][0]["outputSchema"], Is.Not.Null);
            Assert.That(tool.InputReads, Is.EqualTo(1));
            Assert.That(tool.OutputReads, Is.EqualTo(1));
        }

        [Test]
        public void OperationalHealth_CompactPreservesChecksAndCountsWithSmallerPayload()
        {
            var tool = new GetServerHealthTool();
            var full = tool.Execute(new JObject());
            var compact = tool.Execute(new JObject { ["compact"] = true });
            Assert.That(full.IsError, Is.False, full.Text);
            Assert.That(compact.IsError, Is.False, compact.Text);
            Assert.That(ToolSchemaValidator.ValidateArguments((JObject)compact.Json, tool.OutputSchema, out var error), Is.True, error);
            Assert.That((string)compact.Json["detail"], Is.EqualTo("compact"));
            Assert.That((string)compact.Json["status"], Is.EqualTo((string)full.Json["status"]));
            Assert.That((int)compact.Json["registry"]["count"], Is.EqualTo((int)full.Json["registry"]["count"]));
            Assert.That((int)compact.Json["selfTest"]["checks"], Is.EqualTo((int)full.Json["selfTest"]["checks"]));
            Assert.That(((JArray)compact.Json["loadedModules"]).Count, Is.Zero);
            Assert.That(((JArray)compact.Json["jobs"]["recent"]).Count, Is.Zero);
            Assert.That(compact.Json.ToString().Length, Is.LessThan(full.Json.ToString().Length));
        }

        [Test]
        public void OperationalTools_AreReadOnlyAndHaveValidSchemas()
        {
            IMcpTool[] tools =
            {
                new GetToolInventoryTool(),
                new GetServerHealthTool(),
                new SelfTestTool(),
            };
            foreach (var tool in tools)
            {
                Assert.That(tool.IsWrite, Is.False, tool.Name);
                Assert.That(tool.Descriptor.Capabilities, Is.EqualTo(McpCapability.Read), tool.Name);
                Assert.That(tool.Descriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.None), tool.Name);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var error), Is.True, $"{tool.Name}: {error}");
            }
        }

        [Test]
        public void BootstrapRegistry_HasUniqueValidSchemasAndDescriptors()
        {
            Assert.That(Bootstrap.ToolRegistry, Is.Not.Null);
            var tools = Bootstrap.ToolRegistry.All.ToArray();
            Assert.That(tools.Length, Is.GreaterThanOrEqualTo(160));
            CollectionAssert.IsSubsetOf(new[]
            {
                "get_tool_inventory", "get_server_health", "self_test",
                "get_editor_context", "list_loaded_scenes", "read_prefab",
                "get_asset_dependencies", "list_packages", "preview_build",
            }, tools.Select(tool => tool.Name).ToArray());
            Assert.That(tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(tools.Length));
            foreach (var tool in tools)
            {
                var inputSchema = tool.InputSchema;
                Assert.That((string)inputSchema?["type"], Is.EqualTo("object"),
                    $"{tool.Name}: input schema root must declare type object");
                Assert.That(ToolSchemaValidator.ValidateSchema(inputSchema, out var error), Is.True,
                    $"{tool.Name}: {error}");
                if (tool.OutputSchema != null)
                    Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError), Is.True,
                        $"{tool.Name} output: {outputError}");
                var descriptor = Bootstrap.ToolRegistry.GetDescriptor(tool.Name);
                Assert.That(descriptor, Is.Not.Null, tool.Name);
                Assert.That(descriptor.Name, Is.EqualTo(tool.Name));
            }
        }

        [Test]
        public void BootstrapOperationalSelfTest_PassesWithoutErrors()
        {
            var report = OperationalReport.SelfTest();
            Assert.That((bool)report["valid"], Is.True, report["issues"]?.ToString());
            Assert.That((int)report["errors"], Is.Zero);
            Assert.That((int)report["checks"], Is.GreaterThan(Bootstrap.ToolRegistry.Count));
        }
    }
}

namespace Maicon.SceneAnalyzer.ReflectionTestSurface
{
    public static class ReflectionPolicyProbe
    {
        public static int Value => 1;
        public static int PublicMethod() => 1;
        private static int HiddenMethod() => 1;
        public static int ParamsMethod(params int[] values) => values?.Length ?? 0;
        public static Task AsyncMethod() => Task.CompletedTask;
    }
}
