using Maicon.SceneAnalyzer.Editor.Core;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.NavMesh.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tests
{
    public sealed class NavMeshPackageToolTests
    {
        private const string Root = "Assets/__SceneAnalyzerNavMeshPackageAudit";

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            UnityEngine.AI.NavMesh.RemoveAllNavMeshData();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerNavMeshPackageAudit");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            UnityEngine.AI.NavMesh.RemoveAllNavMeshData();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void Contracts_CoverFivePackageToolsAndMatchAINavigation2014()
        {
            IMcpTool[] tools =
            {
                new ListNavMeshSurfacesTool(),
                new BakeNavMeshSurfaceTool(),
                new AddNavMeshSurfaceTool(),
                new AddNavMeshModifierTool(),
                new AddNavMeshLinkTool(),
            };
            var registry = new McpToolRegistry();
            Assert.DoesNotThrow(() => registry.RegisterRange(tools));
            Assert.That(registry.Count, Is.EqualTo(5));
            foreach (var tool in tools)
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError), Is.True, $"{tool.Name} input: {inputError}");
                Assert.That(tool.OutputSchema, Is.Not.Null, $"{tool.Name} must expose structured output");
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError), Is.True, $"{tool.Name} output: {outputError}");
                Assert.That(Maicon.SceneAnalyzer.Editor.Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
            }

            Assert.That(tools[0].Descriptor.Capabilities, Is.EqualTo(McpCapability.Read));
            var bake = tools.Single(tool => tool.Name == "bake_navmesh_surface");
            Assert.That(bake.Descriptor.Capabilities.HasFlag(McpCapability.AssetWrite), Is.True);
            Assert.That(bake.Descriptor.Capabilities.HasFlag(McpCapability.Destructive), Is.True);
            Assert.That(bake.Descriptor.Risk, Is.EqualTo(McpRiskLevel.High));
            Assert.That(bake.Descriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required));

            IMcpTool addSurface = new AddNavMeshSurfaceTool();
            var collect = (JArray)addSurface.InputSchema["properties"]?["collectObjects"]?["enum"];
            Assert.That(collect.Values<string>(), Does.Contain("MarkedWithModifier"));
            Assert.That((string)addSurface.InputSchema["properties"]?["collectObjects"]?["default"], Is.EqualTo("All"));
            Assert.That(addSurface.InputSchema["properties"]?["bake"], Is.Null, "asset-backed bake must remain behind bake_navmesh_surface's high-risk confirmation gate");
            Assert.That(addSurface.Descriptor.Capabilities.HasFlag(McpCapability.AssetWrite), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1,
                ["collectObjects"] = "Volume",
                ["size"] = Vec(10f, 4f, 10f),
                ["center"] = Vec(0f, 2f, 0f),
                ["defaultArea"] = "Walkable",
                ["agentTypeID"] = 0,
            }, addSurface.InputSchema, out var surfaceError), Is.True, surfaceError);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1,
                ["bake"] = true,
            }, addSurface.InputSchema, out var bakeBypassError), Is.False, bakeBypassError);
        }

        [Test]
        public void AddSurface_PreflightsBeforeAddingAndSupportsFullPackageConfigurationWithUndoRedo()
        {
            var invalidHost = new GameObject("InvalidSurface");
            var invalid = NavMeshSurfaceAuthor.AddSurface(new JObject
            {
                ["instanceId"] = invalidHost.GetMcpInstanceId(),
                ["agentTypeID"] = 999999,
                ["layerMask"] = "__MissingNavMeshLayer__",
                ["defaultArea"] = "__MissingNavMeshArea__",
            });
            Assert.That(invalid.Ok, Is.False);
            Assert.That(invalidHost.GetComponent<NavMeshSurface>(), Is.Null);

            var host = new GameObject("VolumeSurface");
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var result = new AddNavMeshSurfaceTool().Execute(new JObject
            {
                ["instanceId"] = host.GetMcpInstanceId(),
                ["agentTypeID"] = 0,
                ["collectObjects"] = "Volume",
                ["useGeometry"] = "PhysicsColliders",
                ["layerMask"] = "Default",
                ["defaultArea"] = "Walkable",
                ["size"] = Vec(10f, 4f, 8f),
                ["center"] = Vec(0f, 2f, 0f),
                ["generateLinks"] = true,
                ["overrideVoxelSize"] = true,
                ["voxelSize"] = 0.1f,
                ["overrideTileSize"] = true,
                ["tileSize"] = 64,
                ["minRegionArea"] = 3f,
                ["buildHeightMesh"] = true,
            });
            Undo.CollapseUndoOperations(group);

            Assert.That(result.IsError, Is.False, result.Text);
            var surface = host.GetComponent<NavMeshSurface>();
            Assert.That(surface, Is.Not.Null);
            Assert.That(surface.collectObjects, Is.EqualTo(CollectObjects.Volume));
            Assert.That(surface.size, Is.EqualTo(new Vector3(10f, 4f, 8f)));
            Assert.That(surface.center, Is.EqualTo(new Vector3(0f, 2f, 0f)));
            Assert.That(surface.defaultArea, Is.EqualTo(0));
            Assert.That(surface.minRegionArea, Is.EqualTo(3f));
            Assert.That(surface.buildHeightMesh, Is.True);
            Assert.That((string)result.Json?["collectObjects"], Is.EqualTo("Volume"));

            Undo.PerformUndo();
            Assert.That(host.GetComponent<NavMeshSurface>(), Is.Null);
            Undo.PerformRedo();
            Assert.That(host.GetComponent<NavMeshSurface>()?.tileSize, Is.EqualTo(64));
            Undo.PerformUndo();
        }

        [Test]
        public void ModifierAndLink_FailClosedBeforeMutationAndRoundTripThroughUndo()
        {
            var modifierHost = new GameObject("ModifierHost");
            var invalidModifier = NavMeshSurfaceAuthor.AddModifier(new JObject
            {
                ["instanceId"] = modifierHost.GetMcpInstanceId(),
                ["volume"] = true,
                ["area"] = "__MissingNavMeshArea__",
                ["size"] = Vec(-1f, 2f, 3f),
            });
            Assert.That(invalidModifier.Ok, Is.False);
            Assert.That(modifierHost.GetComponent<NavMeshModifierVolume>(), Is.Null);

            var linkHost = new GameObject("LinkHost");
            var endpoint = new GameObject("Endpoint");
            var invalidLink = NavMeshSurfaceAuthor.AddLink(new JObject
            {
                ["instanceId"] = linkHost.GetMcpInstanceId(),
                ["agentTypeID"] = 999999,
                ["area"] = "__MissingNavMeshArea__",
                ["width"] = -1f,
            });
            Assert.That(invalidLink.Ok, Is.False);
            Assert.That(linkHost.GetComponent<NavMeshLink>(), Is.Null);

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var result = new AddNavMeshLinkTool().Execute(new JObject
            {
                ["instanceId"] = linkHost.GetMcpInstanceId(),
                ["startPoint"] = Vec(0f, 0f, -2f),
                ["endInstanceId"] = endpoint.GetMcpInstanceId(),
                ["width"] = 1.5f,
                ["agentTypeID"] = 0,
                ["area"] = "Jump",
                ["bidirectional"] = false,
                ["activated"] = false,
                ["autoUpdate"] = true,
                ["costModifier"] = 2f,
            });
            Undo.CollapseUndoOperations(group);

            Assert.That(result.IsError, Is.False, result.Text);
            var link = linkHost.GetComponent<NavMeshLink>();
            Assert.That(link, Is.Not.Null);
            Assert.That(link.endTransform, Is.EqualTo(endpoint.transform));
            Assert.That(link.area, Is.EqualTo(UnityEngine.AI.NavMesh.GetAreaFromName("Jump")));
            Assert.That(link.activated, Is.False);
            Assert.That((string)result.Json?["areaName"], Is.EqualTo("Jump"));

            Undo.PerformUndo();
            Assert.That(linkHost.GetComponent<NavMeshLink>(), Is.Null);
            Undo.PerformRedo();
            Assert.That(linkHost.GetComponent<NavMeshLink>()?.width, Is.EqualTo(1.5f));
            Undo.PerformUndo();
        }

        [Test]
        public void AuthoringAndBake_RejectPersistentPrefabAssetsWithoutMutation()
        {
            var source = new GameObject("NavMeshPrefab");
            var path = Root + "/NavMesh.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, path);
            Object.DestroyImmediate(source);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefabAsset, Is.Not.Null, "the prefab fixture must import before exercising the tools");

            Assert.That(NavMeshSurfaceAuthor.AddSurface(new JObject
            {
                ["instanceId"] = prefabAsset.GetMcpInstanceId(),
                ["collectObjects"] = "Children",
            }).Ok, Is.False);
            Assert.That(NavMeshSurfaceAuthor.AddModifier(new JObject
            {
                ["instanceId"] = prefabAsset.GetMcpInstanceId(),
                ["area"] = "Walkable",
            }).Ok, Is.False);
            Assert.That(NavMeshSurfaceAuthor.AddLink(new JObject
            {
                ["instanceId"] = prefabAsset.GetMcpInstanceId(),
                ["area"] = "Jump",
            }).Ok, Is.False);
            Assert.That(NavMeshSurfaceAuthor.BakeSurface(new JObject
            {
                ["surfaceInstanceId"] = prefabAsset.GetMcpInstanceId(),
            }).Ok, Is.False);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var reloadedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(reloadedPrefab.GetComponent<NavMeshSurface>(), Is.Null);
            Assert.That(reloadedPrefab.GetComponent<NavMeshModifier>(), Is.Null);
            Assert.That(reloadedPrefab.GetComponent<NavMeshLink>(), Is.Null);
        }

        [Test]
        public void ListSurfaces_IncludesInactiveLoadedObjectsAndReturnsStructuredState()
        {
            var active = new GameObject("ActiveSurface");
            active.AddComponent<NavMeshSurface>();
            var inactive = new GameObject("InactiveSurface");
            inactive.AddComponent<NavMeshSurface>();
            inactive.SetActive(false);

            var result = new ListNavMeshSurfacesTool().Execute(new JObject());
            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That((int)result.Json?["count"], Is.EqualTo(2));
            var names = ((JArray)result.Json?["surfaces"]).Select(item => (string)item?["name"]).ToArray();
            Assert.That(names, Is.EquivalentTo(new[] { "ActiveSurface", "InactiveSurface" }));
        }

        [Test]
        public void BakeSurface_RejectsEmptyWritesAndRebakesWithUndoWithoutDeletingPreviousAsset()
        {
            var empty = new BakeNavMeshSurfaceTool().Execute(new JObject());
            Assert.That(empty.IsError, Is.True, "baking zero surfaces must not report a successful write");

            var host = new GameObject("BakedSurface");
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.transform.SetParent(host.transform, false);
            var surface = host.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.agentTypeID = 0;

            var first = new BakeNavMeshSurfaceTool().Execute(new JObject
            {
                ["surfaceInstanceId"] = host.GetMcpInstanceId(),
            });
            var firstPath = (string)first.Json?["surfaces"]?[0]?["assetPath"];
            string secondPath = null;
            try
            {
                Assert.That(first.IsError, Is.False, first.Text);
                Assert.That((int)first.Json?["bakedCount"], Is.EqualTo(1));
                Assert.That(surface.navMeshData, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(surface.navMeshData), Is.EqualTo(firstPath));

                var second = new BakeNavMeshSurfaceTool().Execute(new JObject
                {
                    ["surfaceInstanceId"] = host.GetMcpInstanceId(),
                });
                secondPath = (string)second.Json?["surfaces"]?[0]?["assetPath"];

                Assert.That(second.IsError, Is.False, second.Text);
                Assert.That(secondPath, Is.Not.EqualTo(firstPath));
                Assert.That((string)second.Json?["surfaces"]?[0]?["retainedPreviousAssetPath"], Is.EqualTo(firstPath));
                Assert.That(AssetDatabase.LoadAssetAtPath<NavMeshData>(firstPath), Is.Not.Null, "rebake must retain the previous asset for shared references and recovery");
                Assert.That(AssetDatabase.LoadAssetAtPath<NavMeshData>(secondPath), Is.SameAs(surface.navMeshData));

                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(AssetDatabase.GetAssetPath(surface.navMeshData), Is.EqualTo(firstPath));
                Assert.That(AssetDatabase.LoadAssetAtPath<NavMeshData>(secondPath), Is.Not.Null, "Unity Undo does not remove project files");
                Undo.PerformRedo();
                Assert.That(AssetDatabase.GetAssetPath(surface.navMeshData), Is.EqualTo(secondPath));
            }
            finally
            {
                if (!string.IsNullOrEmpty(secondPath)) AssetDatabase.DeleteAsset(secondPath);
                if (!string.IsNullOrEmpty(firstPath)) AssetDatabase.DeleteAsset(firstPath);
            }
        }

        private static JArray Vec(float x, float y, float z) => new JArray(x, y, z);
    }
}
