using Maicon.SceneAnalyzer.Editor.Core;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
#pragma warning disable CS0618
    public sealed class NavMeshToolTests
    {
        private const string Root = "Assets/__SceneAnalyzerNavMeshBuiltInAudit";

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            NavMesh.RemoveAllNavMeshData();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerNavMeshBuiltInAudit");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            NavMesh.RemoveAllNavMeshData();
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void Contracts_CoverTenBuiltInToolsWithStrictSchemasStructuredOutputsAndHonestRisk()
        {
            IMcpTool[] tools =
            {
                new NavMeshSamplePositionTool(),
                new NavMeshCalculatePathTool(),
                new NavMeshRaycastTool(),
                new NavMeshFindClosestEdgeTool(),
                new ListNavMeshAreasTool(),
                new ListNavMeshAgentTypesTool(),
                new BakeNavMeshTool(),
                new SetNavMeshAgentTool(),
                new AddOffMeshLinkTool(),
                new AddNavMeshObstacleTool(),
            };

            var registry = new McpToolRegistry();
            Assert.DoesNotThrow(() => registry.RegisterRange(tools));
            Assert.That(registry.Count, Is.EqualTo(10));
            foreach (var tool in tools)
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError), Is.True, $"{tool.Name} input: {inputError}");
                Assert.That(tool.OutputSchema, Is.Not.Null, $"{tool.Name} must expose structured output");
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError), Is.True, $"{tool.Name} output: {outputError}");
                Assert.That(Maicon.SceneAnalyzer.Editor.Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
            }

            foreach (var readTool in tools.Take(6))
            {
                Assert.That(readTool.Descriptor.Capabilities, Is.EqualTo(McpCapability.Read), readTool.Name);
                Assert.That(readTool.Descriptor.Risk, Is.EqualTo(McpRiskLevel.Low), readTool.Name);
            }

            var bake = tools.Single(tool => tool.Name == "bake_navmesh");
            Assert.That(bake.InputSchema["properties"]?["async"], Is.Null, "fire-and-forget bake without polling is not an honest contract");
            Assert.That(bake.Descriptor.Capabilities.HasFlag(McpCapability.Destructive), Is.True);
            Assert.That(bake.Descriptor.Risk, Is.EqualTo(McpRiskLevel.High));
            Assert.That(bake.Descriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required));
            Assert.That(new AddOffMeshLinkTool().Description, Does.Contain("deprecated").IgnoreCase);

            var sample = new NavMeshSamplePositionTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["position"] = Vec(0f, 0f, 0f),
                ["maxDistance"] = 1f,
                ["areaMask"] = new JArray("Walkable"),
                ["agentTypeID"] = 0,
            }, sample.InputSchema, out var sampleError), Is.True, sampleError);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["position"] = new JObject { ["x"] = 0f, ["y"] = 0f },
            }, sample.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["position"] = Vec(0f, 0f, 0f),
                ["maxDistance"] = -1f,
            }, sample.InputSchema, out _), Is.False);

            var obstacle = new AddNavMeshObstacleTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1,
                ["shape"] = "Capsule",
                ["radius"] = -0.5f,
            }, obstacle.InputSchema, out _), Is.False);
        }

        [Test]
        public void Queries_FailClosedForUnknownFiltersAndReturnStructuredEmptyResults()
        {
            var unknownArea = new NavMeshSamplePositionTool().Execute(new JObject
            {
                ["position"] = Vec(0f, 0f, 0f),
                ["areaMask"] = "__MissingNavMeshArea__",
            });
            Assert.That(unknownArea.IsError, Is.True, "unknown names must not widen to AllAreas");

            var unknownAgent = new NavMeshCalculatePathTool().Execute(new JObject
            {
                ["start"] = Vec(0f, 0f, 0f),
                ["end"] = Vec(1f, 0f, 0f),
                ["agentTypeID"] = 999999,
            });
            Assert.That(unknownAgent.IsError, Is.True);

            var sample = new NavMeshSamplePositionTool().Execute(new JObject
            {
                ["position"] = Vec(0f, 0f, 0f),
                ["maxDistance"] = 1f,
            });
            Assert.That(sample.IsError, Is.False, sample.Text);
            Assert.That((bool)sample.Json?["found"], Is.False);

            var path = new NavMeshCalculatePathTool().Execute(new JObject
            {
                ["start"] = Vec(0f, 0f, 0f),
                ["end"] = Vec(1f, 0f, 0f),
            });
            Assert.That(path.IsError, Is.False, path.Text);
            Assert.That((bool)path.Json?["found"], Is.False);
            Assert.That((JArray)path.Json?["corners"], Is.Empty);

            var ray = new NavMeshRaycastTool().Execute(new JObject
            {
                ["start"] = Vec(0f, 0f, 0f),
                ["end"] = Vec(1f, 0f, 0f),
            });
            Assert.That(ray.IsError, Is.False, ray.Text);
            Assert.That((bool)ray.Json?["found"], Is.False, "an empty NavMesh must not be reported as a clear line");

            var edge = new NavMeshFindClosestEdgeTool().Execute(new JObject
            {
                ["position"] = Vec(0f, 0f, 0f),
            });
            Assert.That(edge.IsError, Is.False, edge.Text);
            Assert.That((bool)edge.Json?["found"], Is.False);
        }

        [Test]
        public void Discovery_ReturnsAreaIndicesAndAgentTypesAsStructuredData()
        {
            var areas = new ListNavMeshAreasTool().Execute(new JObject());
            Assert.That(areas.IsError, Is.False, areas.Text);
            Assert.That((int)areas.Json?["count"], Is.GreaterThanOrEqualTo(3));
            Assert.That(((JArray)areas.Json?["areas"]).Any(area => (string)area?["name"] == "Walkable" && (int)area?["index"] == 0), Is.True);
            Assert.That(((JArray)areas.Json?["areas"]).Any(area => (string)area?["name"] == "Not Walkable" && (int)area?["index"] == 1), Is.True);

            var agents = new ListNavMeshAgentTypesTool().Execute(new JObject());
            Assert.That(agents.IsError, Is.False, agents.Text);
            Assert.That((int)agents.Json?["count"], Is.GreaterThanOrEqualTo(1));
            Assert.That(((JArray)agents.Json?["agentTypes"]).Any(agent => (int)agent?["agentTypeID"] == 0), Is.True);
        }

        [Test]
        public void SetAgent_PreflightsInvalidValuesAndRejectsPersistentPrefabAssets()
        {
            var go = new GameObject("Agent");
            var agent = go.AddComponent<NavMeshAgent>();
            agent.speed = 3.5f;
            var invalid = NavMeshAuthor.SetAgent(new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["speed"] = 7f,
                ["areaMask"] = "__MissingNavMeshArea__",
            });
            Assert.That(invalid.Ok, Is.False);
            Assert.That(agent.speed, Is.EqualTo(3.5f));

            var source = new GameObject("AgentPrefab");
            source.AddComponent<NavMeshAgent>().speed = 2f;
            var path = Root + "/Agent.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, path);
            Object.DestroyImmediate(source);
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var persistentAgent = prefabAsset.GetComponent<NavMeshAgent>();
            var persistent = NavMeshAuthor.SetAgent(new JObject
            {
                ["instanceId"] = prefabAsset.GetMcpInstanceId(),
                ["speed"] = 8f,
            });
            Assert.That(persistent.Ok, Is.False);
            Assert.That(persistentAgent.speed, Is.EqualTo(2f));
        }

        [Test]
        public void SetAgent_IsStructuredUndoableRedoableAndRecordsPrefabOverrides()
        {
            var source = new GameObject("AgentPrefabSource");
            var sourceAgent = source.AddComponent<NavMeshAgent>();
            sourceAgent.speed = 3.5f;
            sourceAgent.radius = 0.5f;
            var path = Root + "/AgentOverride.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, path);
            Object.DestroyImmediate(source);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var agent = instance.GetComponent<NavMeshAgent>();

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var result = new SetNavMeshAgentTool().Execute(new JObject
            {
                ["instanceId"] = instance.GetMcpInstanceId(),
                ["speed"] = 7f,
                ["acceleration"] = 12f,
                ["radius"] = 0.75f,
                ["height"] = 2.25f,
                ["areaMask"] = "Walkable",
            });
            Undo.CollapseUndoOperations(group);

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That((float)result.Json?["speed"], Is.EqualTo(7f));
            Assert.That((int)result.Json?["areaMask"], Is.EqualTo(1));
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.True);
            Assert.That(instance.scene.isDirty, Is.True);

            Undo.PerformUndo();
            Assert.That(agent.speed, Is.EqualTo(3.5f));
            Assert.That(agent.radius, Is.EqualTo(0.5f));
            Undo.PerformRedo();
            Assert.That(agent.speed, Is.EqualTo(7f));
            Assert.That(agent.radius, Is.EqualTo(0.75f));
            Undo.PerformUndo();
        }

        [Test]
        public void LinkAndObstacle_PreflightBeforeMutationAndSupportUndoRedo()
        {
            var host = new GameObject("LinkHost");
            var endpoint = new GameObject("LinkEnd");
            var missingEndpoint = NavMeshAuthor.AddOffMeshLink(new JObject
            {
                ["instanceId"] = host.GetMcpInstanceId(),
                ["endInstanceId"] = int.MaxValue,
            });
            Assert.That(missingEndpoint.Ok, Is.False);
            Assert.That(host.GetComponent<OffMeshLink>(), Is.Null);

            var invalidArea = NavMeshAuthor.AddOffMeshLink(new JObject
            {
                ["instanceId"] = host.GetMcpInstanceId(),
                ["endInstanceId"] = endpoint.GetMcpInstanceId(),
                ["area"] = "__MissingNavMeshArea__",
            });
            Assert.That(invalidArea.Ok, Is.False);
            Assert.That(host.GetComponent<OffMeshLink>(), Is.Null);

            var obstacleHost = new GameObject("ObstacleHost");
            var invalidObstacle = NavMeshAuthor.AddObstacle(new JObject
            {
                ["instanceId"] = obstacleHost.GetMcpInstanceId(),
                ["shape"] = "NotAShape",
                ["radius"] = -1f,
            });
            Assert.That(invalidObstacle.Ok, Is.False);
            Assert.That(obstacleHost.GetComponent<NavMeshObstacle>(), Is.Null);

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var result = new AddNavMeshObstacleTool().Execute(new JObject
            {
                ["instanceId"] = obstacleHost.GetMcpInstanceId(),
                ["shape"] = "Capsule",
                ["center"] = Vec(1f, 2f, 3f),
                ["radius"] = 1.25f,
                ["height"] = 4f,
                ["carving"] = true,
            });
            Undo.CollapseUndoOperations(group);
            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That((string)result.Json?["shape"], Is.EqualTo("Capsule"));
            Assert.That(obstacleHost.GetComponent<NavMeshObstacle>(), Is.Not.Null);

            Undo.PerformUndo();
            Assert.That(obstacleHost.GetComponent<NavMeshObstacle>(), Is.Null);
            Undo.PerformRedo();
            Assert.That(obstacleHost.GetComponent<NavMeshObstacle>()?.radius, Is.EqualTo(1.25f));
            Undo.PerformUndo();
        }

        private static JArray Vec(float x, float y, float z) => new JArray(x, y, z);
    }
#pragma warning restore CS0618
}
