using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Maicon.SceneAnalyzer.Editor.SystemTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class GameObjectAuthoringToolTests
    {
        private const string Root = "Assets/__SceneAnalyzerGameObjectAudit";

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            Selection.objects = Array.Empty<UnityEngine.Object>();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerGameObjectAudit");
            Bootstrap.Snapshot.MarkStale();
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            Selection.objects = Array.Empty<UnityEngine.Object>();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            Bootstrap.Snapshot.MarkStale();
        }

        [Test]
        public void Contracts_ExposeTwentyTwoRegisteredToolsWithStructuredOutputsAndHonestRisk()
        {
            IMcpTool[] tools =
            {
                new CreatePrimitiveTool(), new CreateEmptyTool(), new CreateCameraTool(),
                new InstantiatePrefabTool(), new SetTransformTool(), new SetCameraPropsTool(),
                new SetPropertyTool(), new AddComponentTool(), new RemoveComponentTool(),
                new SetEnabledTool(), new SetActiveTool(), new RenameObjectTool(),
                new SetTagTool(), new SetLayerTool(), new ParentObjectTool(),
                new DeleteObjectTool(), new DuplicateObjectTool(), new LookAtTool(),
                new SelectObjectTool(), CreateUguiTool("Maicon.SceneAnalyzer.Editor.UGUI.SetRectTransformTool"),
                new SetStaticEditorFlagsTool(), new ApplyChangesetTool(),
            };

            Assert.That(tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(22));
            foreach (var tool in tools)
            {
                Assert.That(Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError), Is.True,
                    $"{tool.Name} input: {inputError}");
                Assert.That(tool.OutputSchema, Is.Not.Null, $"{tool.Name} must expose structured output");
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError), Is.True,
                    $"{tool.Name} output: {outputError}");
                Assert.That(tool.Descriptor.EditorMode, Is.EqualTo(McpEditorMode.EditModeOnly), tool.Name);
            }

            foreach (var destructive in tools.Where(tool =>
                         tool.Name == "delete_object" || tool.Name == "remove_component" || tool.Name == "apply_changeset"))
            {
                Assert.That(destructive.Descriptor.Capabilities.HasFlag(McpCapability.Destructive), Is.True, destructive.Name);
                Assert.That(destructive.Descriptor.Risk, Is.EqualTo(McpRiskLevel.High), destructive.Name);
                Assert.That(destructive.Descriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required), destructive.Name);
            }
        }

        [Test]
        public void CreationTools_RejectBrokenParentsWithoutLeavingObjects()
        {
            var prefabPath = CreatePrefabAsset("SourcePrefab");
            var before = SceneObjects().Length;
            var invalidParent = int.MaxValue;
            IMcpTool[] tools =
            {
                new CreateEmptyTool(), new CreatePrimitiveTool(), new CreateCameraTool(), new InstantiatePrefabTool(),
            };
            JObject[] arguments =
            {
                new JObject { ["name"] = "ShouldNotExist", ["parent"] = invalidParent },
                new JObject { ["kind"] = "Cube", ["name"] = "ShouldNotExist", ["parent"] = invalidParent },
                new JObject { ["name"] = "ShouldNotExist", ["parent"] = invalidParent },
                new JObject { ["guidOrPath"] = prefabPath, ["name"] = "ShouldNotExist", ["parent"] = invalidParent },
            };

            for (var index = 0; index < tools.Length; index++)
            {
                var result = tools[index].Execute(arguments[index]);
                Assert.That(result.IsError, Is.True, tools[index].Name);
                Assert.That(SceneObjects().Length, Is.EqualTo(before), tools[index].Name);
            }
        }

        [Test]
        public void ParentObject_PreservesLocalTransformWhenWorldPositionStaysFalseAndRejectsCycles()
        {
            var parent = new GameObject("Parent");
            parent.transform.position = new Vector3(10f, 0f, 0f);
            var child = new GameObject("Child");
            child.transform.localPosition = new Vector3(1f, 2f, 3f);
            child.transform.localRotation = Quaternion.Euler(10f, 20f, 30f);
            child.transform.localScale = new Vector3(2f, 3f, 4f);
            var localPosition = child.transform.localPosition;
            var localRotation = child.transform.localRotation;
            var localScale = child.transform.localScale;
            var tool = new ParentObjectTool();

            var reparented = tool.Execute(new JObject
            {
                ["instanceId"] = child.GetInstanceID(),
                ["newParentId"] = parent.GetInstanceID(),
                ["worldPositionStays"] = false,
            });
            Assert.That(reparented.IsError, Is.False, reparented.Text);
            Assert.That(child.transform.parent, Is.SameAs(parent.transform));
            Assert.That(child.transform.localPosition, Is.EqualTo(localPosition));
            Assert.That(Quaternion.Angle(child.transform.localRotation, localRotation), Is.LessThan(0.001f));
            Assert.That(child.transform.localScale, Is.EqualTo(localScale));

            var cycle = tool.Execute(new JObject
            {
                ["instanceId"] = parent.GetInstanceID(),
                ["newParentId"] = child.GetInstanceID(),
            });
            Assert.That(cycle.IsError, Is.True);
            Assert.That(parent.transform.parent, Is.Null);
        }

        [Test]
        public void CreateCamera_IsMainRetagsInactiveCameraAndUndoRestoresPreviousMainCamera()
        {
            var previous = new GameObject("PreviousMain") { tag = "MainCamera" };
            previous.SetActive(false);
            var result = new CreateCameraTool().Execute(new JObject
            {
                ["name"] = "NewMain",
                ["isMain"] = true,
            });

            Assert.That(result.IsError, Is.False, result.Text);
            var created = EditorUtility.InstanceIDToObject((int)result.Json?["instanceId"]) as GameObject;
            Assert.That(created, Is.Not.Null);
            Assert.That(created.CompareTag("MainCamera"), Is.True);
            Assert.That(previous.CompareTag("Untagged"), Is.True, "inactive MainCamera objects must also be retagged");

            Undo.PerformUndo();
            Assert.That(previous.CompareTag("MainCamera"), Is.True);
            Assert.That(created == null, Is.True, "camera creation and prior-tag edits must be one undo step");
        }

        [Test]
        public void SceneMutators_RejectPersistentPrefabAssetsWithoutChangingThem()
        {
            var prefabPath = CreatePrefabAsset("PersistentRoot");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var beforeName = asset.name;
            var beforePosition = asset.transform.localPosition;

            var renamed = new RenameObjectTool().Execute(new JObject
            {
                ["instanceId"] = asset.GetInstanceID(),
                ["name"] = "MutatedAsset",
            });
            var transformed = new SetTransformTool().Execute(new JObject
            {
                ["instanceId"] = asset.GetInstanceID(),
                ["position"] = new JArray(5f, 6f, 7f),
                ["space"] = "Local",
            });

            Assert.That(renamed.IsError, Is.True);
            Assert.That(transformed.IsError, Is.True);
            Assert.That(asset.name, Is.EqualTo(beforeName));
            Assert.That(asset.transform.localPosition, Is.EqualTo(beforePosition));
            Assert.That(EditorUtility.IsDirty(asset), Is.False);
        }

        [Test]
        public void PrefabInstanceTransformMutation_RecordsOverrideAndSupportsUndoRedo()
        {
            var prefabPath = CreatePrefabAsset("PrefabRoot");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Assert.That(instance, Is.Not.Null);
            var desired = new Vector3(3f, 4f, 5f);

            var result = new SetTransformTool().Execute(new JObject
            {
                ["instanceId"] = instance.GetInstanceID(),
                ["position"] = new JArray(desired.x, desired.y, desired.z),
                ["space"] = "Local",
            });
            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That(instance.transform.localPosition, Is.EqualTo(desired));
            Assert.That(PrefabUtility.GetPropertyModifications(instance)
                .Any(modification => modification.propertyPath == "m_LocalPosition.x"), Is.True);

            Undo.PerformUndo();
            Assert.That(instance.transform.localPosition, Is.EqualTo(Vector3.zero));
            Undo.PerformRedo();
            Assert.That(instance.transform.localPosition, Is.EqualTo(desired));
        }

        [Test]
        public void SetTransform_RejectsNonFiniteValuesWithoutMutation()
        {
            var target = new GameObject("FiniteTarget");
            var result = new SetTransformTool().Execute(new JObject
            {
                ["instanceId"] = target.GetInstanceID(),
                ["position"] = new JArray(float.NaN, 1f, 2f),
            });

            Assert.That(result.IsError, Is.True);
            Assert.That(IsFinite(target.transform.position), Is.True);
            Assert.That(target.transform.position, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void DeleteObject_GuardsLargeDescendantHierarchiesUnlessForced()
        {
            var root = new GameObject("DeepRoot");
            var current = root.transform;
            for (var index = 0; index < 9; index++)
            {
                var child = new GameObject("Child" + index);
                child.transform.SetParent(current, false);
                current = child.transform;
            }

            var result = new DeleteObjectTool().Execute(new JObject { ["instanceId"] = root.GetInstanceID() });
            Assert.That(result.IsError, Is.True);
            Assert.That(root != null, Is.True);
        }

        [Test]
        public void ApplyChangeset_PreviewRejectsInvalidArgumentsAndDuplicateLabelsWithoutMutation()
        {
            var tool = new ApplyChangesetTool();
            var before = SceneObjects().Length;
            var invalidArgs = tool.Execute(new JObject
            {
                ["preview"] = true,
                ["ops"] = new JArray
                {
                    new JObject
                    {
                        ["kind"] = "create_primitive",
                        ["args"] = new JObject { ["kind"] = "NotAPrimitive" },
                    },
                },
            });
            var duplicateLabels = tool.Execute(new JObject
            {
                ["preview"] = true,
                ["ops"] = new JArray
                {
                    new JObject { ["kind"] = "create_empty", ["label"] = "same", ["args"] = new JObject() },
                    new JObject { ["kind"] = "create_empty", ["label"] = "same", ["args"] = new JObject() },
                },
            });

            Assert.That(invalidArgs.IsError, Is.True);
            Assert.That(duplicateLabels.IsError, Is.True);
            Assert.That(SceneObjects().Length, Is.EqualTo(before));
        }

        [Test]
        public void ApplyChangeset_FailureRollsBackEarlierSceneMutations()
        {
            var before = SceneObjects().Length;
            var result = new ApplyChangesetTool().Execute(new JObject
            {
                ["ops"] = new JArray
                {
                    new JObject { ["kind"] = "create_empty", ["label"] = "created", ["args"] = new JObject { ["name"] = "RolledBack" } },
                    new JObject { ["kind"] = "rename_object", ["args"] = new JObject { ["instanceId"] = int.MaxValue, ["name"] = "Never" } },
                },
            });

            Assert.That(result.IsError, Is.True);
            Assert.That(SceneObjects().Length, Is.EqualTo(before));
            Assert.That(GameObject.Find("RolledBack"), Is.Null);
        }

        private static IMcpTool CreateUguiTool(string typeName)
        {
            var type = Type.GetType(typeName + ", com.maicon.scene-analyzer.Editor.UGUI", throwOnError: true);
            return (IMcpTool)Activator.CreateInstance(type);
        }

        private static string CreatePrefabAsset(string name)
        {
            var source = new GameObject(name);
            try
            {
                var path = Root + "/" + name + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(source, path);
                return path;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private static GameObject[] SceneObjects()
            => UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(gameObject => gameObject.scene.IsValid() && gameObject.scene.isLoaded)
                .ToArray();

        private static bool IsFinite(Vector3 value)
            => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
               && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
               && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
