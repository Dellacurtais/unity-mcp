using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Maicon.SceneAnalyzer.Editor.SceneTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class SceneHierarchyToolTests
    {
        private const string Root = "Assets/__SceneAnalyzerSceneHierarchyAudit";

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerSceneHierarchyAudit");
            Assert.That(SaveFixtureScene(SceneManager.GetActiveScene(), Root + "/Active.unity"), Is.True);
            Bootstrap.Snapshot.MarkStale();
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            Bootstrap.Snapshot.MarkStale();
        }

        [Test]
        public void Contracts_ExposeThirteenRegisteredToolsWithStructuredOutputsAndHonestRisk()
        {
            IMcpTool[] tools =
            {
                new ReadHierarchyTool(),
                new ReadSceneTool(),
                new ReadComponentTool(),
                new QuerySceneTool(),
                new ClassifySceneTool(),
                new ListLoadedScenesTool(),
                new CreateSceneTool(),
                new OpenSceneTool(),
                new CloseSceneTool(),
                new SetActiveSceneTool(),
                new SaveSceneTool(),
                new SaveAllScenesTool(),
                CreatePlayTool("Maicon.SceneAnalyzer.Editor.Play.Tools.GetActiveCamerasTool"),
            };

            var registry = new McpToolRegistry();
            registry.RegisterRange(tools);
            Assert.That(registry.Count, Is.EqualTo(13));
            foreach (var tool in tools)
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError), Is.True, $"{tool.Name} input: {inputError}");
                Assert.That(tool.OutputSchema, Is.Not.Null, $"{tool.Name} must expose structured output");
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError), Is.True, $"{tool.Name} output: {outputError}");
                Assert.That(Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
            }

            foreach (var read in tools.Take(6).Concat(tools.Skip(12)))
                Assert.That(read.Descriptor.Capabilities, Is.EqualTo(McpCapability.Read), read.Name);

            var close = tools.Single(tool => tool.Name == "close_scene").Descriptor;
            Assert.That(close.Capabilities.HasFlag(McpCapability.Destructive), Is.True);
            Assert.That(close.Risk, Is.EqualTo(McpRiskLevel.High));
            Assert.That(close.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required));

            foreach (var save in tools.Where(tool => tool.Name == "save_scene" || tool.Name == "save_all_scenes"))
            {
                Assert.That(save.Descriptor.Capabilities.HasFlag(McpCapability.AssetWrite), Is.True, save.Name);
                Assert.That(save.Descriptor.EditorMode, Is.EqualTo(McpEditorMode.EditModeOnly), save.Name);
            }
        }

        [Test]
        public void CreateScene_RejectsAdditiveCreationWhenAnUntitledSceneIsAlreadyOpen()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            ToolResult result = null;
            Assert.DoesNotThrow(() => result = new CreateSceneTool().Execute(new JObject { ["additive"] = true }));
            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsError, Is.True);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1));
        }

        [Test]
        public void SceneSelectors_RequireExactlyOneNonEmptyIdentityAndFailClosedAtRuntime()
        {
            var tool = new SetActiveSceneTool();
            var active = SceneManager.GetActiveScene();

            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject { ["sceneName"] = "" }, tool.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject { ["sceneHandle"] = 0 }, tool.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject { ["sceneHandle"] = (int)active.handle }, tool.InputSchema, out _), Is.True);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["sceneName"] = active.name,
                ["sceneHandle"] = (int)active.handle,
            }, tool.InputSchema, out _), Is.False);
            Assert.That(tool.Execute(new JObject { ["sceneName"] = "" }).IsError, Is.True);
            Assert.That(tool.Execute(new JObject { ["sceneGuid"] = "not-a-guid" }).IsError, Is.True);
            var alreadyActive = tool.Execute(new JObject { ["sceneHandle"] = (int)active.handle });
            Assert.That(alreadyActive.IsError, Is.False, alreadyActive.Text);
            Assert.That((int)alreadyActive.Json?["scene"]?["handle"], Is.EqualTo((int)active.handle));
        }

        [Test]
        public void ReadScene_ReportsTruncationOnlyWhenNodesWereActuallyOmitted()
        {
            new GameObject("OnlyRoot");
            Bootstrap.Snapshot.MarkStale();

            var exact = new ReadSceneTool().Execute(new JObject
            {
                ["depth"] = 5,
                ["maxNodes"] = 1,
            });

            Assert.That(exact.IsError, Is.False, exact.Text);
            Assert.That((int)exact.Json?["emitted"], Is.EqualTo(1));
            Assert.That((bool)exact.Json?["truncated"], Is.False, "hitting the limit exactly is not truncation when no node was omitted");
        }

        [Test]
        public void ReadComponent_AcceptsTheComponentInstanceIdsReturnedByReadScene()
        {
            var go = new GameObject("ComponentHost");
            var camera = go.AddComponent<Camera>();

            var result = new ReadComponentTool().Execute(new JObject
            {
                ["instanceId"] = camera.GetInstanceID(),
            });

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That((int)result.Json?["instanceId"], Is.EqualTo(camera.GetInstanceID()), "legacy instanceId remains available");
            Assert.That((int)result.Json?["componentInstanceId"], Is.EqualTo(camera.GetInstanceID()));
            Assert.That((JArray)result.Json?["components"], Has.Count.EqualTo(1));
            Assert.That((string)result.Json?["components"]?[0]?["componentType"], Is.EqualTo(typeof(Camera).FullName));
        }

        [Test]
        public void QueryScene_FailsClosedOnInvalidFiltersAndReturnsStructuredMatches()
        {
            var go = new GameObject("QueryTarget");
            go.transform.position = new Vector3(2f, 0f, 0f);
            Bootstrap.Snapshot.MarkStale();
            var tool = new QuerySceneTool();

            var valid = tool.Execute(new JObject
            {
                ["nameContains"] = "target",
                ["withinRadius"] = new JObject
                {
                    ["center"] = new JArray(0f, 0f, 0f),
                    ["radius"] = 3f,
                },
            });
            var unknownLayer = tool.Execute(new JObject { ["layer"] = "__MissingSceneLayer__" });
            var unknownTag = tool.Execute(new JObject { ["tag"] = "__MissingSceneTag__" });
            var negativeRadius = tool.Execute(new JObject
            {
                ["withinRadius"] = new JObject
                {
                    ["center"] = new JArray(0f, 0f, 0f),
                    ["radius"] = -1f,
                },
            });

            Assert.That(valid.IsError, Is.False, valid.Text);
            Assert.That(valid.Json, Is.Not.Null, "query_scene must return machine-readable matches");
            Assert.That((int)valid.Json?["matchCount"], Is.EqualTo(1));
            Assert.That((int)valid.Json?["matches"]?[0]?["instanceId"], Is.EqualTo(go.GetInstanceID()));
            Assert.That(unknownLayer.IsError, Is.True, "unknown layer must not silently remove the layer filter");
            Assert.That(unknownTag.IsError, Is.True, "unknown tag must fail before CompareTag-style access or an unbounded query");
            Assert.That(negativeRadius.IsError, Is.True, "negative radius must not silently turn into an unbounded query");
        }

        [Test]
        public void ClassifyScene_ReturnsMachineReadableBucketsAndHierarchyStats()
        {
            var root = new GameObject("Root");
            new GameObject("Child").transform.SetParent(root.transform, false);
            Bootstrap.Snapshot.MarkStale();

            var result = new ClassifySceneTool().Execute(new JObject());

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That(result.Json, Is.Not.Null, "classify_scene must return machine-readable data");
            Assert.That((int)result.Json?["totalGameObjects"], Is.EqualTo(2));
            Assert.That((int)result.Json?["hierarchy"]?["rootCount"], Is.EqualTo(1));
            Assert.That((int)result.Json?["hierarchy"]?["leafCount"], Is.EqualTo(1));
            Assert.That((JArray)result.Json?["tags"], Is.Not.Empty);
            Assert.That((JArray)result.Json?["layers"], Is.Not.Empty);
        }

        [Test]
        public void ListLoadedScenes_DistinguishesLoadedAndUnloadedHierarchyEntries()
        {
            var path = CreateSceneAsset("Unloaded", "StoredRoot");
            var opened = new OpenSceneTool().Execute(new JObject
            {
                ["scenePath"] = path,
                ["mode"] = "AdditiveWithoutLoading",
            });
            Assert.That(opened.IsError, Is.False, opened.Text);

            var result = new ListLoadedScenesTool().Execute(new JObject());

            Assert.That((int)result.Json?["loadedCount"], Is.EqualTo(1));
            Assert.That((int)result.Json?["unloadedCount"], Is.EqualTo(1));
            Assert.That((JArray)result.Json?["loadedScenes"], Has.Count.EqualTo(1));
            Assert.That((JArray)result.Json?["unloadedScenes"], Has.Count.EqualTo(1));
            Assert.That((string)result.Json?["unloadedScenes"]?[0]?["path"], Is.EqualTo(path));
            var unloadedHandle = (int)result.Json?["unloadedScenes"]?[0]?["handle"];
            var setActive = new SetActiveSceneTool().Execute(new JObject { ["sceneHandle"] = unloadedHandle });
            Assert.That(setActive.IsError, Is.True, "unloaded hierarchy entries cannot become active scenes");
        }

        [Test]
        public void CloseScene_RejectsRemovingTheLastLoadedScene()
        {
            var active = SceneManager.GetActiveScene();
            var result = new CloseSceneTool().Execute(new JObject
            {
                ["sceneHandle"] = (int)active.handle,
                ["discardUnsaved"] = true,
            });

            Assert.That(result.IsError, Is.True);
            Assert.That((string)result.Json?["code"], Is.EqualTo("LAST_LOADED_SCENE"));
            Assert.That(active.IsValid() && active.isLoaded, Is.True);
            Assert.That(EditorSceneManager.loadedSceneCount, Is.EqualTo(1));
        }

        [Test]
        public void CloseScene_RequiresExplicitDiscardForDirtyScenes()
        {
            var previousActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            new GameObject("UnsavedChange");
            EditorSceneManager.MarkSceneDirty(scene);
            var tool = new CloseSceneTool();

            var rejected = tool.Execute(new JObject { ["sceneHandle"] = (int)scene.handle });
            Assert.That(rejected.IsError, Is.True);
            Assert.That(scene.IsValid() && scene.isLoaded, Is.True);

            if (SceneManager.GetActiveScene() == scene)
                Assert.That(SceneManager.SetActiveScene(previousActive), Is.True);
            var discarded = tool.Execute(new JObject
            {
                ["sceneHandle"] = (int)scene.handle,
                ["discardUnsaved"] = true,
            });
            Assert.That(discarded.IsError, Is.False, discarded.Text);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1));
        }

        [Test]
        public void SaveScene_SchemaAndRuntimeRejectIncompleteOrNonCanonicalDestinations()
        {
            var tool = new SaveSceneTool();
            var active = SceneManager.GetActiveScene();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["sceneHandle"] = (int)active.handle,
                ["saveAsCopy"] = true,
            }, tool.InputSchema, out _), Is.False);
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["sceneHandle"] = (int)active.handle,
                ["destinationPath"] = Root + "/Copy.unity",
                ["saveAsCopy"] = true,
            }, tool.InputSchema, out _), Is.True);

            var invalid = tool.Execute(new JObject
            {
                ["sceneHandle"] = (int)active.handle,
                ["destinationPath"] = Root + "/ Bad/Copy.unity",
                ["saveAsCopy"] = true,
            });
            Assert.That(invalid.IsError, Is.True);
            Assert.That(AssetDatabase.IsValidFolder(Root + "/ Bad"), Is.False, "invalid paths must fail before creating folders");
        }

        [Test]
        public void SaveScene_AsCopyPreservesSourcePathAndDirtyState()
        {
            var active = SceneManager.GetActiveScene();
            var sourcePath = active.path;
            new GameObject("CopyOnlyChange");
            EditorSceneManager.MarkSceneDirty(active);
            var destination = Root + "/Copy.unity";

            var previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            ToolResult result;
            try
            {
                result = new SaveSceneTool().Execute(new JObject
                {
                    ["sceneHandle"] = (int)active.handle,
                    ["destinationPath"] = destination,
                    ["saveAsCopy"] = true,
                });
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
            }

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That(active.path, Is.EqualTo(sourcePath));
            Assert.That(active.isDirty, Is.True, "Unity SaveScene(saveAsCopy:true) must not clear the dirty marker");
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(destination), Is.Not.Null);
            Assert.That((bool)result.Json?["saveAsCopy"], Is.True);
        }

        [Test]
        public void SaveScene_RejectsOverwritingAnotherSceneAsset()
        {
            var destination = CreateSceneAsset("Existing", "OriginalRoot");
            var previousActive = SceneManager.GetActiveScene();
            var active = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            if (SceneManager.GetActiveScene() != active)
                Assert.That(SceneManager.SetActiveScene(active), Is.True);
            new GameObject("ReplacementRoot");
            EditorSceneManager.MarkSceneDirty(active);

            var previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            ToolResult result;
            try
            {
                result = new SaveSceneTool().Execute(new JObject
                {
                    ["sceneHandle"] = (int)active.handle,
                    ["destinationPath"] = destination,
                });
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
            }

            if (active.IsValid() && active.isLoaded)
            {
                Assert.That(SceneManager.SetActiveScene(previousActive), Is.True);
                Assert.That(EditorSceneManager.CloseScene(active, true), Is.True);
            }
            Assert.That(result.IsError, Is.True, "saving an unsaved scene over another scene asset must fail closed");
            var stored = EditorSceneManager.OpenScene(destination, OpenSceneMode.Additive);
            try
            {
                Assert.That(stored.GetRootGameObjects().Select(item => item.name), Does.Contain("OriginalRoot"));
                Assert.That(stored.GetRootGameObjects().Select(item => item.name), Does.Not.Contain("ReplacementRoot"));
            }
            finally
            {
                EditorSceneManager.CloseScene(stored, true);
            }
        }

        [Test]
        public void GetActiveCameras_ReportsCountMainCameraAndSceneIdentity()
        {
            var enabledGo = new GameObject("EnabledCamera") { tag = "MainCamera" };
            var enabled = enabledGo.AddComponent<Camera>();
            var disabledGo = new GameObject("DisabledCamera");
            disabledGo.AddComponent<Camera>().enabled = false;

            var result = CreatePlayTool("Maicon.SceneAnalyzer.Editor.Play.Tools.GetActiveCamerasTool").Execute(new JObject());

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That(result.Json, Is.Not.Null, "get_active_cameras must return machine-readable data");
            Assert.That((int)result.Json?["count"], Is.EqualTo(1));
            Assert.That((int)result.Json?["mainCameraInstanceId"], Is.EqualTo(enabled.GetInstanceID()));
            Assert.That((JArray)result.Json?["cameras"], Has.Count.EqualTo(1));
            Assert.That((int)result.Json?["cameras"]?[0]?["sceneHandle"], Is.EqualTo((int)SceneManager.GetActiveScene().handle));
        }

        private static IMcpTool CreatePlayTool(string typeName)
        {
            var type = Type.GetType(typeName + ", com.maicon.scene-analyzer.Editor.Play", throwOnError: true);
            return (IMcpTool)Activator.CreateInstance(type);
        }

        private static bool SaveFixtureScene(Scene scene, string path)
        {
            var previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try { return EditorSceneManager.SaveScene(scene, path); }
            finally { LogAssert.ignoreFailingMessages = previousIgnore; }
        }

        private static string CreateSceneAsset(string fileName, string rootName)
        {
            var previousActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject(rootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            var path = Root + "/" + fileName + ".unity";
            Assert.That(SaveFixtureScene(scene, path), Is.True);
            Assert.That(EditorSceneManager.CloseScene(scene, true), Is.True);
            if (previousActive.IsValid() && previousActive.isLoaded && SceneManager.GetActiveScene() != previousActive)
                Assert.That(SceneManager.SetActiveScene(previousActive), Is.True);
            return path;
        }
    }
}
