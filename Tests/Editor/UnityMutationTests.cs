using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.ProjectTools;
using Maicon.SceneAnalyzer.Editor.SceneTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class UnityMutationTests
    {
        private const string Root = "Assets/__SceneAnalyzerUnityMutationAudit";

        private sealed class CodecFixture : ScriptableObject
        {
            public int integer;
            public string text;
            public Vector3 vector;
            public Color color;
            public int[] values;
        }

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerUnityMutationAudit");
            Assert.That(
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), Root + "/Host.unity"),
                Is.True,
                "UnityMutationTests require a saved host scene before exercising additive scene APIs.");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void StableTarget_RoundTripsSceneObjectAndComponent()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject("TargetRoot");
            var child = new GameObject("TargetChild");
            SceneManager.MoveGameObjectToScene(root, scene);
            SceneManager.MoveGameObjectToScene(child, scene);
            child.transform.SetParent(root.transform, false);
            var firstCollider = child.AddComponent<BoxCollider>();
            var secondCollider = child.AddComponent<BoxCollider>();
            Assert.That(EditorSceneManager.SaveScene(scene, Root + "/Targets.unity"), Is.True,
                "A durable component identity requires a saved scene.");
            try
            {
                var gameObjectRef = ToolTargetRef.Capture(child);
                var componentRef = ToolTargetRef.Capture(secondCollider);
                Assert.That(gameObjectRef.SceneHandle, Is.EqualTo(scene.GetMcpSceneHandle()));
                Assert.That(gameObjectRef.HierarchyPath, Is.EqualTo("TargetRoot/TargetChild"));
                Assert.That(gameObjectRef.AssetGuid, Is.Null.Or.Empty);
                Assert.That(componentRef.AssetGuid, Is.Null.Or.Empty);
                Assert.That(componentRef.ComponentOrdinal, Is.EqualTo(1));
                var gameObjectJson = gameObjectRef.ToJson();
                Assert.That(gameObjectJson["assetGuid"], Is.Null, "optional string fields must be omitted instead of serialized as null");
                Assert.That(gameObjectJson["localFileId"], Is.Null, "optional integer fields must be omitted instead of serialized as null");
                Assert.That(gameObjectJson["componentType"], Is.Null, "non-component targets must not emit a null component type");
                Assert.That(gameObjectJson["componentOrdinal"], Is.Null, "non-component targets must not emit a null component ordinal");
                Assert.That(ToolTargetResolver.Resolve(gameObjectRef, out var goError), Is.SameAs(child), goError);
                Assert.That(ToolTargetResolver.Resolve(componentRef, out var componentError), Is.SameAs(secondCollider), componentError);
                componentRef.InstanceId = firstCollider.GetMcpInstanceId();
                Assert.That(ToolTargetResolver.Resolve(componentRef, out var provenanceError), Is.Null);
                StringAssert.StartsWith("TARGET_IDENTITY_STALE", provenanceError);
                var legacyStableRef = ToolTargetRef.FromJson(componentRef.ToJson());
                legacyStableRef.CaptureDomainId = null;
                Assert.That(ToolTargetResolver.Resolve(legacyStableRef, out var ordinalError), Is.SameAs(secondCollider), ordinalError);
                componentRef.InstanceId = secondCollider.GetMcpInstanceId();
                var legacyRef = new ToolTargetRef { InstanceId = firstCollider.GetMcpInstanceId() };
                Assert.That(ToolTargetResolver.Resolve(legacyRef, out var legacyError), Is.SameAs(firstCollider), legacyError);
                var staleStableRef = ToolTargetRef.FromJson(componentRef.ToJson());
                staleStableRef.GlobalObjectId = "invalid-global-id";
                staleStableRef.HierarchyPath = "Missing/Target";
                Assert.That(ToolTargetResolver.Resolve(staleStableRef, out _), Is.Null);
                var jsonRoundTrip = ToolTargetRef.FromJson(componentRef.ToJson());
                Assert.That(ToolTargetResolver.Resolve(jsonRoundTrip, out var roundTripError), Is.SameAs(secondCollider), roundTripError);
            }
            finally
            {
                Object.DestroyImmediate(root);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void StableTarget_PrefabStageReferenceFailsClosedWhenStageIsUnavailableOrDifferent()
        {
            var target = new ToolTargetRef
            {
                IsPrefabStage = true,
                ScenePath = "Assets/Prefabs/Expected.prefab",
                HierarchyPath = "Expected",
            };
            Assert.That(ToolTargetResolver.Resolve(target, out var error), Is.Null);
            StringAssert.StartsWith("PREFAB_STAGE_", error);
        }

        [Test]
        public void SerializedValueCodec_RoundTripsScalarsVectorsColorsAndArrays()
        {
            var fixture = ScriptableObject.CreateInstance<CodecFixture>();
            try
            {
                var serialized = new SerializedObject(fixture);
                SerializedValueCodec.Apply(serialized.FindProperty("integer"), 42);
                SerializedValueCodec.Apply(serialized.FindProperty("text"), "hello");
                SerializedValueCodec.Apply(serialized.FindProperty("vector"), new JArray(1f, 2f, 3f));
                SerializedValueCodec.Apply(serialized.FindProperty("color"), new JObject
                {
                    ["r"] = 0.1f, ["g"] = 0.2f, ["b"] = 0.3f, ["a"] = 0.4f,
                });
                SerializedValueCodec.Apply(serialized.FindProperty("values"), new JArray(3, 5, 8));
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(fixture.integer, Is.EqualTo(42));
                Assert.That(fixture.text, Is.EqualTo("hello"));
                Assert.That(fixture.vector, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(fixture.color.r, Is.EqualTo(0.1f).Within(0.0001f));
                CollectionAssert.AreEqual(new[] { 3, 5, 8 }, fixture.values);
                var arrayJson = SerializedValueCodec.Read(serialized.FindProperty("values"));
                Assert.That((int)arrayJson["total"], Is.EqualTo(3));
            }
            finally
            {
                Object.DestroyImmediate(fixture);
            }
        }

        [Test]
        public void MutationScope_RollsBackWhenNotCompleted()
        {
            var gameObject = new GameObject("MutationRollback");
            try
            {
                using (var mutation = new MutationScope("rollback test"))
                {
                    mutation.Record(gameObject.transform);
                    gameObject.transform.position = new Vector3(4f, 5f, 6f);
                }
                Assert.That(gameObject.transform.position, Is.EqualTo(Vector3.zero));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void MutationScope_RollsBackItsCapturedGroupAfterUndoGroupAdvances()
        {
            var gameObject = new GameObject("MutationCapturedGroup");
            try
            {
                using (var mutation = new MutationScope("captured group test"))
                {
                    mutation.Record(gameObject.transform);
                    gameObject.transform.position = Vector3.one;
                    Undo.IncrementCurrentGroup();
                }
                Assert.That(gameObject.transform.position, Is.EqualTo(Vector3.zero));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void MutationScope_CompleteMarksOwningSceneDirty()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var gameObject = new GameObject("MutationCommit");
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            try
            {
                using (var mutation = new MutationScope("commit test"))
                {
                    mutation.Record(gameObject.transform);
                    gameObject.transform.position = Vector3.one;
                    mutation.Complete();
                }
                Assert.That(scene.isDirty, Is.True);
                Assert.That(gameObject.transform.position, Is.EqualTo(Vector3.one));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void SetQualitySettingsTool_RejectsUnknownQualityLevelName()
        {
            var before = QualitySettings.GetQualityLevel();
            var result = new SetQualitySettingsTool().Execute(new JObject
            {
                ["levelName"] = "__scene_analyzer_missing_quality_level__",
            });
            Assert.That(result.IsError, Is.True);
            Assert.That(QualitySettings.GetQualityLevel(), Is.EqualTo(before));
        }

        [Test]
        public void CreateSceneTool_RejectsTraversalDestinationWithoutSaving()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var result = new SaveSceneTool().Execute(new JObject
                {
                    ["sceneHandle"] = scene.GetMcpSceneHandle(),
                    ["destinationPath"] = "Assets/Scenes/../Escaped.unity",
                });
                Assert.That(result.IsError, Is.True);
                Assert.That(scene.path, Is.Empty);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void CreateSceneTool_CreatesUnsavedSceneWithoutImplicitSave()
        {
            var result = new CreateSceneTool().Execute(new JObject
            {
                ["additive"] = true,
                ["withDefaultObjects"] = false,
            });
            Assert.That(result.IsError, Is.False, result.Text);
            var sceneJson = (JObject)result.Json["scene"];
            int handle = (int)sceneJson["handle"];
            var scene = default(UnityEngine.SceneManagement.Scene);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var candidate = SceneManager.GetSceneAt(i);
                if (candidate.GetMcpSceneHandle() == handle) { scene = candidate; break; }
            }
            try
            {
                Assert.That(scene.IsValid(), Is.True);
                Assert.That(scene.path, Is.Empty);
                Assert.That((bool)result.Json["saved"], Is.False);
            }
            finally
            {
                if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
