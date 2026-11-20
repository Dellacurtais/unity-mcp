using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Maicon.SceneAnalyzer.Editor.SystemTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
#pragma warning disable CS0618
    public sealed class PhysicsQueryToolTests
    {
        private bool _autoSyncTransforms;
        private Vector2 _gravity;
        private int _velocityIterations;
        private int _positionIterations;
        private bool _queriesHitTriggers;
        private bool _queriesStartInColliders;

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _autoSyncTransforms = Physics.autoSyncTransforms;
            _gravity = Physics2D.gravity;
            _velocityIterations = Physics2D.velocityIterations;
            _positionIterations = Physics2D.positionIterations;
            _queriesHitTriggers = Physics2D.queriesHitTriggers;
            _queriesStartInColliders = Physics2D.queriesStartInColliders;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.autoSyncTransforms = _autoSyncTransforms;
            Physics2D.gravity = _gravity;
            Physics2D.velocityIterations = _velocityIterations;
            Physics2D.positionIterations = _positionIterations;
            Physics2D.queriesHitTriggers = _queriesHitTriggers;
            Physics2D.queriesStartInColliders = _queriesStartInColliders;
        }

        [Test]
        public void SchemasDescriptorsAndRegistry_CoverNineToolsAndRejectAmbiguousShapes()
        {
            IMcpTool[] tools =
            {
                new RaycastTool(),
                new OverlapQueryTool(),
                new SweepQueryTool(),
                new CheckGroundTool(),
                new ComputeBoundsTool(),
                new QueryCollisionLayersTool(),
                new Physics2DQueryTool(),
                new GetPhysics2DSettingsTool(),
                new SetPhysics2DSettingsTool(),
            };

            var registry = new McpToolRegistry();
            Assert.DoesNotThrow(() => registry.RegisterRange(tools));
            Assert.That(registry.Count, Is.EqualTo(9));
            foreach (var tool in tools)
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var schemaError), Is.True, $"{tool.Name} input: {schemaError}");
                Assert.That(tool.OutputSchema, Is.Not.Null, $"{tool.Name} must declare its structured output");
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputSchemaError), Is.True, $"{tool.Name} output: {outputSchemaError}");
                Assert.That(registry.TryGet(tool.Name, out _), Is.True, tool.Name);
                Assert.That(Maicon.SceneAnalyzer.Editor.Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
            }

            foreach (var readTool in tools.Where(tool => !tool.IsWrite))
            {
                Assert.That(readTool.Descriptor.Capabilities, Is.EqualTo(McpCapability.Read), readTool.Name);
                Assert.That(readTool.Descriptor.Risk, Is.EqualTo(McpRiskLevel.Low), readTool.Name);
            }

            var settingsDescriptor = tools.Single(tool => tool.Name == "set_physics2d_settings").Descriptor;
            Assert.That(settingsDescriptor.Capabilities, Is.EqualTo(McpCapability.ProjectSettings));
            Assert.That(settingsDescriptor.Risk, Is.EqualTo(McpRiskLevel.High));
            Assert.That(settingsDescriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required));
            Assert.That(settingsDescriptor.EditorMode, Is.EqualTo(McpEditorMode.EditModeOnly));

            var settings = new SetPhysics2DSettingsTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["gravity"] = new JArray(0f, -9.81f) },
                settings.InputSchema,
                out var gravityError), Is.True, gravityError);
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["gravity"] = Vec(0f, -9.81f, 1f) },
                settings.InputSchema,
                out _), Is.False, "Physics2D gravity must be a Vector2; z cannot be accepted and discarded");

            var overlap = new OverlapQueryTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["shape"] = "sphere", ["center"] = Vec(0f, 0f, 0f), ["radius"] = 1f },
                overlap.InputSchema,
                out var validSphereError), Is.True, validSphereError);
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["shape"] = "sphere", ["center"] = Vec(0f, 0f, 0f) },
                overlap.InputSchema,
                out _), Is.False, "sphere queries must declare radius");
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["shape"] = "capsule", ["pointA"] = Vec(0f, 0f, 0f), ["radius"] = 1f },
                overlap.InputSchema,
                out _), Is.False, "capsule queries must declare both endpoints");
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["shape"] = "box", ["center"] = Vec(0f, 0f, 0f), ["halfExtents"] = Vec(1f, 1f, 1f), ["radius"] = 1f },
                overlap.InputSchema,
                out _), Is.False, "shape-specific fields must not be mixed");

            var raycast = new RaycastTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["origin"] = Vec(0f, 0f, 0f), ["direction"] = Vec(1f, 0f, 0f), ["layerMask"] = "Default" },
                raycast.InputSchema,
                out var layerError), Is.True, layerError);
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["origin"] = Vec(0f, 0f, 0f), ["direction"] = Vec(1f, 0f, 0f), ["layerMask"] = new JObject() },
                raycast.InputSchema,
                out _), Is.False, "layerMask must be an integer, layer name, or array of layer names");
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["origin"] = Vec(0f, 0f, 0f), ["direction"] = Vec(1f, 0f, 0f), ["layerMask"] = (long)int.MaxValue + 1L },
                raycast.InputSchema,
                out _), Is.False, "layerMask integers above Int32 must be rejected by the public schema");
            Assert.That(ToolSchemaValidator.ValidateArguments(
                new JObject { ["origin"] = Vec(0f, 0f, 0f), ["direction"] = Vec(1f, 0f, 0f), ["layerMask"] = (long)int.MinValue - 1L },
                raycast.InputSchema,
                out _), Is.False, "layerMask integers below Int32 must be rejected by the public schema");
        }

        [Test]
        public void Raycast_SynchronizesMovedCollidersAndReturnsStructuredHits()
        {
            Physics.autoSyncTransforms = false;
            var target = new GameObject("MovedTarget");
            target.AddComponent<BoxCollider>();
            target.transform.position = new Vector3(100f, 0f, 0f);
            Physics.SyncTransforms();
            target.transform.position = Vector3.zero;

            var result = new RaycastTool().Execute(new JObject
            {
                ["origin"] = Vec(-5f, 0f, 0f),
                ["direction"] = Vec(1f, 0f, 0f),
                ["maxDistance"] = 10f,
                ["multiple"] = true,
            });

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That((int)result.Json?["count"], Is.EqualTo(1));
            Assert.That((string)result.Json?["hits"]?[0]?["name"], Is.EqualTo("MovedTarget"));
            Assert.That(result.Json?["hits"]?[0]?["target"], Is.Not.Null);
            Assert.That((float)result.Json?["hits"]?[0]?["distance"], Is.EqualTo(4.5f).Within(0.01f));
        }

        [Test]
        public void Queries3D_RejectInvalidFiltersAndReturnDeterministicStructuredData()
        {
            var alpha = new GameObject("Alpha");
            alpha.AddComponent<BoxCollider>();
            var beta = new GameObject("Beta");
            beta.AddComponent<SphereCollider>();
            Physics.SyncTransforms();

            var invalidLayer = new RaycastTool().Execute(new JObject
            {
                ["origin"] = Vec(-5f, 0f, 0f),
                ["direction"] = Vec(1f, 0f, 0f),
                ["maxDistance"] = 10f,
                ["layerMask"] = "__MissingPhysicsLayer__",
            });
            Assert.That(invalidLayer.IsError, Is.True, "an unknown layer must not silently widen to DefaultRaycastLayers");

            ToolResult oversizedLayer = null;
            Assert.DoesNotThrow(() => oversizedLayer = new RaycastTool().Execute(new JObject
            {
                ["origin"] = Vec(-5f, 0f, 0f),
                ["direction"] = Vec(1f, 0f, 0f),
                ["layerMask"] = (long)int.MaxValue + 1L,
            }), "an out-of-Int32 mask must return a structured error instead of throwing");
            Assert.That(oversizedLayer.IsError, Is.True);

            ToolResult undersizedLayer = null;
            Assert.DoesNotThrow(() => undersizedLayer = new RaycastTool().Execute(new JObject
            {
                ["origin"] = Vec(-5f, 0f, 0f),
                ["direction"] = Vec(1f, 0f, 0f),
                ["layerMask"] = (long)int.MinValue - 1L,
            }), "an under-Int32 mask must return a structured error instead of throwing");
            Assert.That(undersizedLayer.IsError, Is.True);

            var invalidMatrixLayer = new QueryCollisionLayersTool().Execute(new JObject
            {
                ["layer"] = "__MissingPhysicsLayer__",
            });
            Assert.That(invalidMatrixLayer.IsError, Is.True, "an unknown layer must not silently return the full matrix");

            var matrix = new QueryCollisionLayersTool().Execute(new JObject());
            Assert.That(matrix.IsError, Is.False, matrix.Text);
            Assert.That((string)matrix.Json?["matrixState"], Is.EqualTo("effectiveRuntime"));
            Assert.That((JArray)matrix.Json?["layers"], Has.Count.EqualTo(32), "the full matrix must retain unnamed layer indices");

            var overlap = new OverlapQueryTool().Execute(new JObject
            {
                ["shape"] = "sphere",
                ["center"] = Vec(0f, 0f, 0f),
                ["radius"] = 2f,
            });
            Assert.That(overlap.IsError, Is.False, overlap.Text);
            Assert.That((int)overlap.Json?["count"], Is.EqualTo(2));
            CollectionAssert.AreEqual(
                new[] { "Alpha", "Beta" },
                ((JArray)overlap.Json?["colliders"]).Select(item => (string)item["name"]).ToArray());

            var negativeRadius = new OverlapQueryTool().Execute(new JObject
            {
                ["shape"] = "sphere",
                ["center"] = Vec(0f, 0f, 0f),
                ["radius"] = -1f,
            });
            Assert.That(negativeRadius.IsError, Is.True, "negative query dimensions must fail before reaching Unity Physics");

            var zeroGroundDirection = new CheckGroundTool().Execute(new JObject
            {
                ["instanceId"] = alpha.GetMcpInstanceId(),
                ["down"] = Vec(0f, 0f, 0f),
            });
            Assert.That(zeroGroundDirection.IsError, Is.True, "check_ground must reject a zero direction");

            var bounds = new GameObject("Bounds");
            bounds.AddComponent<MeshRenderer>();
            bounds.AddComponent<MeshFilter>().sharedMesh = CreateUnitMesh();
            var boundsResult = new ComputeBoundsTool().Execute(new JObject
            {
                ["instanceId"] = bounds.GetMcpInstanceId(),
                ["recursive"] = false,
            });
            Assert.That(boundsResult.IsError, Is.False, boundsResult.Text);
            Assert.That(boundsResult.Json?["bounds"]?["center"], Is.Not.Null);
            Assert.That(boundsResult.Json?["target"], Is.Not.Null);
        }

        [Test]
        public void Physics2DQuery_ReturnsAllHitsSynchronizesTransformsAndRejectsInvalidRaycasts()
        {
            var alpha = new GameObject("Alpha2D");
            alpha.AddComponent<CircleCollider2D>();
            var beta = new GameObject("Beta2D");
            beta.AddComponent<BoxCollider2D>();
            Physics2D.SyncTransforms();

            var overlap = new Physics2DQueryTool().Execute(new JObject
            {
                ["kind"] = "overlap_circle",
                ["origin"] = new JArray(0f, 0f),
                ["radius"] = 2f,
            });
            Assert.That(overlap.IsError, Is.False, overlap.Text);
            Assert.That((JArray)overlap.Json?["hits"], Has.Count.EqualTo(2));
            CollectionAssert.AreEqual(
                new[] { "Alpha2D", "Beta2D" },
                ((JArray)overlap.Json?["hits"]).Select(item => (string)item["name"]).ToArray());

            alpha.transform.position = new Vector3(100f, 0f, 0f);
            Physics2D.SyncTransforms();
            alpha.transform.position = Vector3.zero;
            var raycast = new Physics2DQueryTool().Execute(new JObject
            {
                ["kind"] = "raycast",
                ["origin"] = new JArray(-5f, 0f),
                ["direction"] = new JArray(1f, 0f),
                ["distance"] = 10f,
            });
            Assert.That(raycast.IsError, Is.False, raycast.Text);
            Assert.That(((JArray)raycast.Json?["hits"]).Any(item => (string)item["collider"]?["name"] == "Alpha2D"), Is.True);

            var zeroDirection = new Physics2DQueryTool().Execute(new JObject
            {
                ["kind"] = "raycast",
                ["origin"] = new JArray(0f, 0f),
                ["direction"] = new JArray(0f, 0f),
            });
            Assert.That(zeroDirection.IsError, Is.True);

            var nonFiniteDistance = new Physics2DQueryTool().Execute(new JObject
            {
                ["kind"] = "raycast",
                ["origin"] = new JArray(0f, 0f),
                ["direction"] = new JArray(1f, 0f),
                ["distance"] = float.NaN,
            });
            Assert.That(nonFiniteDistance.IsError, Is.True);

            ToolResult oversizedLayer = null;
            Assert.DoesNotThrow(() => oversizedLayer = new Physics2DQueryTool().Execute(new JObject
            {
                ["kind"] = "overlap_point",
                ["origin"] = new JArray(0f, 0f),
                ["layerMask"] = (long)int.MaxValue + 1L,
            }), "an out-of-Int32 mask must return a structured error instead of throwing");
            Assert.That(oversizedLayer.IsError, Is.True);

            ToolResult undersizedLayer = null;
            Assert.DoesNotThrow(() => undersizedLayer = new Physics2DQueryTool().Execute(new JObject
            {
                ["kind"] = "overlap_point",
                ["origin"] = new JArray(0f, 0f),
                ["layerMask"] = (long)int.MinValue - 1L,
            }), "an under-Int32 mask must return a structured error instead of throwing");
            Assert.That(undersizedLayer.IsError, Is.True);
        }

        [Test]
        public void Physics2DSettings_InvalidBatchDoesNotPartiallyMutateProjectSettings()
        {
            var gravityBefore = Physics2D.gravity;
            var velocityIterationsBefore = Physics2D.velocityIterations;

            var result = new SetPhysics2DSettingsTool().Execute(new JObject
            {
                ["gravity"] = new JArray(gravityBefore.x + 1f, gravityBefore.y - 1f),
                ["velocityIterations"] = 0,
            });

            Assert.That(result.IsError, Is.True);
            Assert.That(Physics2D.gravity, Is.EqualTo(gravityBefore));
            Assert.That(Physics2D.velocityIterations, Is.EqualTo(velocityIterationsBefore));
        }

        [Test]
        public void Physics2DSettings_AreProjectSettingsGatedAndReturnEffectiveState()
        {
            var tool = new SetPhysics2DSettingsTool();
            Assert.That(tool.Execute(new JObject()).IsError, Is.True, "an empty high-risk settings write must not succeed as a no-op");

            var changed = tool.Execute(new JObject
            {
                ["gravity"] = new JArray(2f, -7f),
                ["velocityIterations"] = 9,
                ["positionIterations"] = 4,
                ["queriesHitTriggers"] = !_queriesHitTriggers,
                ["queriesStartInColliders"] = !_queriesStartInColliders,
            });

            Assert.That(changed.IsError, Is.False, changed.Text);
            Assert.That(Physics2D.gravity, Is.EqualTo(new Vector2(2f, -7f)));
            Assert.That((float)changed.Json?["gravity"]?["x"], Is.EqualTo(2f));
            Assert.That((float)changed.Json?["gravity"]?["y"], Is.EqualTo(-7f));
            Assert.That((int)changed.Json?["velocityIterations"], Is.EqualTo(9));
            Assert.That((int)changed.Json?["positionIterations"], Is.EqualTo(4));
            Assert.That((bool)changed.Json?["persistedImmediately"], Is.True);
            Assert.That((bool)changed.Json?["undoRegistered"], Is.False);
            CollectionAssert.AreEquivalent(
                new[] { "gravity", "velocityIterations", "positionIterations", "queriesHitTriggers", "queriesStartInColliders" },
                ((JArray)changed.Json?["changedKeys"]).Values<string>());
        }

        private static JArray Vec(float x, float y, float z) => new JArray(x, y, z);

        private static Mesh CreateUnitMesh()
        {
            var mesh = new Mesh { name = "PhysicsQueryBoundsMesh" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0f, 0.5f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
#pragma warning restore CS0618
}
