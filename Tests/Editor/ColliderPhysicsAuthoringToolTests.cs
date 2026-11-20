using Maicon.SceneAnalyzer.Editor.Core;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

#if UNITY_2023_3_OR_NEWER
using AuditPhysicsMaterial = UnityEngine.PhysicsMaterial;
#else
using AuditPhysicsMaterial = UnityEngine.PhysicMaterial;
#endif

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class ColliderPhysicsAuthoringToolTests
    {
        private const string Root = "Assets/__SceneAnalyzerColliderAudit";
        private readonly List<Mesh> _meshes = new List<Mesh>();

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
            AssetDatabase.CreateFolder("Assets", "__SceneAnalyzerColliderAudit");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            foreach (var mesh in _meshes)
                if (mesh != null) Object.DestroyImmediate(mesh);
            _meshes.Clear();
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void Contracts_ExposeThreeRegisteredToolsWithHonestRiskAndStructuredOutputs()
        {
            IMcpTool[] tools =
            {
                new AutoFitColliderTool(),
                new AddCompoundCollidersTool(),
                new SetPhysicsMaterialTool(),
            };
            var registry = new McpToolRegistry();
            registry.RegisterRange(tools);
            Assert.That(registry.Count, Is.EqualTo(3));

            foreach (var tool in tools)
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError), Is.True, $"{tool.Name} input: {inputError}");
                Assert.That(tool.OutputSchema, Is.Not.Null, $"{tool.Name} must declare structured output");
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError), Is.True, $"{tool.Name} output: {outputError}");
                Assert.That(Maicon.SceneAnalyzer.Editor.Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
                Assert.That(tool.Descriptor.EditorMode, Is.EqualTo(McpEditorMode.EditModeOnly), tool.Name);
            }

            foreach (var destructive in tools.Take(2))
            {
                Assert.That(destructive.Descriptor.Capabilities.HasFlag(McpCapability.Destructive), Is.True, destructive.Name);
                Assert.That(destructive.Descriptor.Risk, Is.EqualTo(McpRiskLevel.High), destructive.Name);
                Assert.That(destructive.Descriptor.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required), destructive.Name);
            }

            var materialDescriptor = tools[2].Descriptor;
            Assert.That(materialDescriptor.Capabilities, Is.EqualTo(McpCapability.SceneWrite));
            Assert.That(materialDescriptor.Risk, Is.EqualTo(McpRiskLevel.Medium));

            var autoFit = new AutoFitColliderTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1,
                ["kind"] = "box",
                ["convex"] = true,
            }, autoFit.InputSchema, out _), Is.False, "convex only applies to mesh colliders");
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1,
                ["kind"] = "mesh",
                ["useChildren"] = false,
                ["convex"] = true,
            }, autoFit.InputSchema, out var meshSchemaError), Is.True, meshSchemaError);

            var setMaterial = new SetPhysicsMaterialTool();
            Assert.That(ToolSchemaValidator.ValidateArguments(new JObject
            {
                ["instanceId"] = 1,
                ["colliderInstanceId"] = 2,
                ["materialGuidOrPath"] = Root + "/Audit.physicMaterial",
            }, setMaterial.InputSchema, out var colliderTargetError), Is.True, colliderTargetError);
        }

        [Test]
        public void AutoFit_PreflightsKindMeshSourceAndRigidbodyCompatibilityBeforeReplacement()
        {
            var invalidKindObject = CreateRendererObject("InvalidKind", new Vector3(2f, 1f, 1f));
            var invalidKindExisting = invalidKindObject.AddComponent<BoxCollider>();
            var invalidKind = PhysicsAuthor.AutoFitCollider(new JObject
            {
                ["instanceId"] = invalidKindObject.GetMcpInstanceId(),
                ["kind"] = "not-a-kind",
                ["replaceExisting"] = true,
            });
            Assert.That(invalidKind.Ok, Is.False);
            Assert.That(invalidKindObject.GetComponent<BoxCollider>(), Is.SameAs(invalidKindExisting), "validation must happen before destructive replacement");

            var missingMeshObject = new GameObject("MissingMesh");
            missingMeshObject.AddComponent<MeshRenderer>();
            var missingMeshExisting = missingMeshObject.AddComponent<SphereCollider>();
            var missingMesh = PhysicsAuthor.AutoFitCollider(new JObject
            {
                ["instanceId"] = missingMeshObject.GetMcpInstanceId(),
                ["kind"] = "mesh",
                ["useChildren"] = false,
                ["replaceExisting"] = true,
                ["convex"] = true,
            });
            Assert.That(missingMesh.Ok, Is.False);
            Assert.That(missingMeshObject.GetComponent<SphereCollider>(), Is.SameAs(missingMeshExisting));
            Assert.That(missingMeshObject.GetComponent<MeshCollider>(), Is.Null);

            var dynamicObject = CreateRendererObject("DynamicMesh", new Vector3(2f, 2f, 2f));
            dynamicObject.AddComponent<Rigidbody>().isKinematic = false;
            var dynamicExisting = dynamicObject.AddComponent<BoxCollider>();
            var nonConvexDynamic = PhysicsAuthor.AutoFitCollider(new JObject
            {
                ["instanceId"] = dynamicObject.GetMcpInstanceId(),
                ["kind"] = "mesh",
                ["useChildren"] = false,
                ["replaceExisting"] = true,
                ["convex"] = false,
            });
            Assert.That(nonConvexDynamic.Ok, Is.False);
            Assert.That(dynamicObject.GetComponent<BoxCollider>(), Is.SameAs(dynamicExisting));
            Assert.That(dynamicObject.GetComponent<MeshCollider>(), Is.Null);

            var triggerObject = CreateRendererObject("TriggerMesh", new Vector3(2f, 2f, 2f));
            var triggerExisting = triggerObject.AddComponent<CapsuleCollider>();
            var nonConvexTrigger = PhysicsAuthor.AutoFitCollider(new JObject
            {
                ["instanceId"] = triggerObject.GetMcpInstanceId(),
                ["kind"] = "mesh",
                ["useChildren"] = false,
                ["replaceExisting"] = true,
                ["isTrigger"] = true,
                ["convex"] = false,
            });
            Assert.That(nonConvexTrigger.Ok, Is.False);
            Assert.That(triggerObject.GetComponent<CapsuleCollider>(), Is.SameAs(triggerExisting));
            Assert.That(triggerObject.GetComponent<MeshCollider>(), Is.Null);
        }

        [Test]
        public void AutoFit_UsesRendererLocalBoundsSelectsCapsuleAxisAndIsUndoable()
        {
            var boxObject = CreateRendererObject("RotatedBox", new Vector3(4f, 2f, 1f));
            boxObject.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            var controller = boxObject.AddComponent<CharacterController>();

            Undo.IncrementCurrentGroup();
            var boxUndoGroup = Undo.GetCurrentGroup();
            var boxResult = new AutoFitColliderTool().Execute(new JObject
            {
                ["instanceId"] = boxObject.GetMcpInstanceId(),
                ["kind"] = "box",
                ["useChildren"] = false,
                ["replaceExisting"] = true,
            });
            Undo.CollapseUndoOperations(boxUndoGroup);

            Assert.That(boxResult.IsError, Is.False, boxResult.Text);
            Assert.That(boxResult.Json, Is.TypeOf<JObject>());
            var box = boxObject.GetComponent<BoxCollider>();
            Assert.That(box, Is.Not.Null);
            Assert.That(Vector3.Distance(box.size, new Vector3(4f, 2f, 1f)), Is.LessThan(0.0001f));
            Assert.That(boxObject.GetComponent<CharacterController>(), Is.SameAs(controller));
            Assert.That((string)boxResult.Json["kind"], Is.EqualTo("box"));
            Assert.That(boxResult.Json["collider"], Is.Not.Null);

            Undo.PerformUndo();
            Assert.That(boxObject.GetComponent<BoxCollider>(), Is.Null);
            Assert.That(boxObject.GetComponent<CharacterController>(), Is.Not.Null);

            Undo.PerformRedo();
            var redoneBox = boxObject.GetComponent<BoxCollider>();
            Assert.That(redoneBox, Is.Not.Null);
            Assert.That(Vector3.Distance(redoneBox.size, new Vector3(4f, 2f, 1f)), Is.LessThan(0.0001f));
            Assert.That(boxObject.GetComponent<CharacterController>(), Is.Not.Null);
            Undo.PerformUndo();

            var capsuleObject = CreateRendererObject("LongCapsule", new Vector3(6f, 2f, 1f));
            var capsuleResult = new AutoFitColliderTool().Execute(new JObject
            {
                ["instanceId"] = capsuleObject.GetMcpInstanceId(),
                ["kind"] = "capsule",
                ["useChildren"] = false,
                ["replaceExisting"] = false,
            });
            Assert.That(capsuleResult.IsError, Is.False, capsuleResult.Text);
            var capsule = capsuleObject.GetComponent<CapsuleCollider>();
            Assert.That(capsule.direction, Is.EqualTo(0), "capsule height axis must follow the longest local-bounds axis");
            Assert.That(capsule.height, Is.EqualTo(6f).Within(0.0001f));
            Assert.That(capsule.radius, Is.EqualTo(1f).Within(0.0001f));
        }

        [Test]
        public void Compound_PreflightsWholeBatchAndRejectsEmptyOrMeshlessRendererSets()
        {
            var emptyRoot = new GameObject("EmptyRoot");
            var empty = PhysicsAuthor.AddCompoundColliders(new JObject
            {
                ["instanceId"] = emptyRoot.GetMcpInstanceId(),
            });
            Assert.That(empty.Ok, Is.False, "a write that adds zero colliders must not report success");

            var invalidRoot = new GameObject("InvalidRoot");
            var invalidChild = CreateRendererObject("InvalidChild", new Vector3(2f, 1f, 1f), invalidRoot.transform);
            var invalidExisting = invalidChild.AddComponent<BoxCollider>();
            var invalidKind = PhysicsAuthor.AddCompoundColliders(new JObject
            {
                ["instanceId"] = invalidRoot.GetMcpInstanceId(),
                ["kind"] = "not-a-kind",
                ["replaceExisting"] = true,
            });
            Assert.That(invalidKind.Ok, Is.False);
            Assert.That(invalidChild.GetComponent<BoxCollider>(), Is.SameAs(invalidExisting));

            var meshlessRoot = new GameObject("MeshlessRoot");
            var meshlessChild = new GameObject("MeshlessChild");
            meshlessChild.transform.SetParent(meshlessRoot.transform);
            meshlessChild.AddComponent<MeshRenderer>();
            var meshlessExisting = meshlessChild.AddComponent<SphereCollider>();
            var meshless = PhysicsAuthor.AddCompoundColliders(new JObject
            {
                ["instanceId"] = meshlessRoot.GetMcpInstanceId(),
                ["kind"] = "mesh",
                ["replaceExisting"] = true,
            });
            Assert.That(meshless.Ok, Is.False);
            Assert.That(meshlessChild.GetComponent<SphereCollider>(), Is.SameAs(meshlessExisting));
            Assert.That(meshlessChild.GetComponent<MeshCollider>(), Is.Null);
        }

        [Test]
        public void Compound_UsesEachRendererLocalBoundsReturnsAllTargetsAndUndoRestoresReplacement()
        {
            var root = new GameObject("CompoundRoot");
            var child = CreateRendererObject("RotatedChild", new Vector3(5f, 2f, 1f), root.transform);
            child.transform.localRotation = Quaternion.Euler(0f, 35f, 0f);
            var original = child.AddComponent<SphereCollider>();
            original.radius = 0.25f;

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var result = new AddCompoundCollidersTool().Execute(new JObject
            {
                ["instanceId"] = root.GetMcpInstanceId(),
                ["kind"] = "capsule",
                ["replaceExisting"] = true,
                ["isTrigger"] = true,
            });
            Undo.CollapseUndoOperations(group);

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That((int)result.Json["addedCount"], Is.EqualTo(1));
            Assert.That((int)result.Json["replacedCount"], Is.EqualTo(1));
            Assert.That((JArray)result.Json["colliders"], Has.Count.EqualTo(1));
            var capsule = child.GetComponent<CapsuleCollider>();
            Assert.That(capsule, Is.Not.Null);
            Assert.That(capsule.direction, Is.EqualTo(0));
            Assert.That(capsule.height, Is.EqualTo(5f).Within(0.0001f));
            Assert.That(capsule.radius, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(capsule.isTrigger, Is.True);

            Undo.PerformUndo();
            Assert.That(child.GetComponent<CapsuleCollider>(), Is.Null);
            Assert.That(child.GetComponent<SphereCollider>(), Is.Not.Null);
            Assert.That(child.GetComponent<SphereCollider>().radius, Is.EqualTo(0.25f));

            Undo.PerformRedo();
            var redoneCapsule = child.GetComponent<CapsuleCollider>();
            Assert.That(redoneCapsule, Is.Not.Null);
            Assert.That(redoneCapsule.direction, Is.EqualTo(0));
            Assert.That(redoneCapsule.height, Is.EqualTo(5f).Within(0.0001f));
            Assert.That(redoneCapsule.radius, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(redoneCapsule.isTrigger, Is.True);
            Assert.That(child.GetComponent<SphereCollider>(), Is.Null);
            Undo.PerformUndo();
        }

        [Test]
        public void SetPhysicsMaterial_RejectsAmbiguousColliderWithoutMutation()
        {
            var materialPath = CreatePhysicsMaterialAsset("Ambiguous.physicMaterial");
            var material = AssetDatabase.LoadAssetAtPath<AuditPhysicsMaterial>(materialPath);
            var target = new GameObject("AmbiguousColliderTarget");
            var box = target.AddComponent<BoxCollider>();
            var sphere = target.AddComponent<SphereCollider>();

            var result = new SetPhysicsMaterialTool().Execute(new JObject
            {
                ["instanceId"] = target.GetMcpInstanceId(),
                ["materialGuidOrPath"] = materialPath,
            });

            Assert.That(result.IsError, Is.True, "multiple colliders require an explicit collider target");
            Assert.That(box.sharedMaterial, Is.Null);
            Assert.That(sphere.sharedMaterial, Is.Null);
            Assert.That(material, Is.Not.Null);
        }

        [Test]
        public void SetPhysicsMaterial_TargetsColliderRecordsPrefabOverrideAndUndoRestoresIt()
        {
            var originalPath = CreatePhysicsMaterialAsset("Original.physicMaterial");
            var replacementPath = CreatePhysicsMaterialAsset("Replacement.physicMaterial");
            var original = AssetDatabase.LoadAssetAtPath<AuditPhysicsMaterial>(originalPath);
            var replacement = AssetDatabase.LoadAssetAtPath<AuditPhysicsMaterial>(replacementPath);

            var source = new GameObject("ColliderPrefabSource");
            source.AddComponent<BoxCollider>().sharedMaterial = original;
            source.AddComponent<SphereCollider>().sharedMaterial = original;
            var prefabPath = Root + "/Collider.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            Object.DestroyImmediate(source);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var colliders = instance.GetComponents<Collider>();
            var target = colliders.OfType<SphereCollider>().Single();
            var untouched = colliders.OfType<BoxCollider>().Single();

            var result = new SetPhysicsMaterialTool().Execute(new JObject
            {
                ["instanceId"] = instance.GetMcpInstanceId(),
                ["colliderInstanceId"] = target.GetMcpInstanceId(),
                ["materialGuidOrPath"] = AssetDatabase.AssetPathToGUID(replacementPath),
            });

            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That(result.Json, Is.TypeOf<JObject>());
            Assert.That((int)result.Json["collider"]["instanceId"], Is.EqualTo(target.GetMcpInstanceId()));
            Assert.That((string)result.Json["material"]["assetPath"], Is.EqualTo(replacementPath));
            Assert.That(target.sharedMaterial, Is.SameAs(replacement));
            Assert.That(untouched.sharedMaterial, Is.SameAs(original));
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.True);
            Assert.That(instance.scene.isDirty, Is.True);

            Undo.PerformUndo();
            Assert.That(target.sharedMaterial, Is.SameAs(original));
            Assert.That(untouched.sharedMaterial, Is.SameAs(original));

            Undo.PerformRedo();
            Assert.That(target.sharedMaterial, Is.SameAs(replacement));
            Assert.That(untouched.sharedMaterial, Is.SameAs(original));
            Undo.PerformUndo();
        }

        [Test]
        public void AuthoringTools_RejectPersistentPrefabAssetsWithoutMutation()
        {
            var materialPath = CreatePhysicsMaterialAsset("Persistent.physicMaterial");
            var material = AssetDatabase.LoadAssetAtPath<AuditPhysicsMaterial>(materialPath);
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.name = "PersistentColliderPrefab";
            var prefabPath = Root + "/Persistent.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            Object.DestroyImmediate(source);

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var originalCollider = prefabAsset.GetComponent<BoxCollider>();
            Assert.That(PrefabUtility.IsPartOfPrefabAsset(prefabAsset), Is.True);

            var materialResult = new SetPhysicsMaterialTool().Execute(new JObject
            {
                ["instanceId"] = prefabAsset.GetMcpInstanceId(),
                ["colliderInstanceId"] = originalCollider.GetMcpInstanceId(),
                ["materialGuidOrPath"] = materialPath,
            });
            Assert.That(materialResult.IsError, Is.True);
            Assert.That(originalCollider.sharedMaterial, Is.Null);

            var autoFitResult = new AutoFitColliderTool().Execute(new JObject
            {
                ["instanceId"] = prefabAsset.GetMcpInstanceId(),
                ["kind"] = "sphere",
                ["useChildren"] = false,
                ["replaceExisting"] = false,
            });
            Assert.That(autoFitResult.IsError, Is.True);
            Assert.That(prefabAsset.GetComponent<SphereCollider>(), Is.Null);

            var compoundResult = new AddCompoundCollidersTool().Execute(new JObject
            {
                ["instanceId"] = prefabAsset.GetMcpInstanceId(),
                ["kind"] = "capsule",
                ["replaceExisting"] = false,
            });
            Assert.That(compoundResult.IsError, Is.True);
            Assert.That(prefabAsset.GetComponent<CapsuleCollider>(), Is.Null);
            Assert.That(material, Is.Not.Null);
        }

        private GameObject CreateRendererObject(string name, Vector3 size, Transform parent = null)
        {
            var gameObject = new GameObject(name);
            if (parent != null) gameObject.transform.SetParent(parent, false);
            var mesh = CreateBoxMesh(size);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            gameObject.AddComponent<MeshRenderer>();
            return gameObject;
        }

        private Mesh CreateBoxMesh(Vector3 size)
        {
            var extents = size * 0.5f;
            var mesh = new Mesh { name = "ColliderAuditMesh" };
            mesh.vertices = new[]
            {
                new Vector3(-extents.x, -extents.y, -extents.z), new Vector3(extents.x, -extents.y, -extents.z),
                new Vector3(extents.x, extents.y, -extents.z), new Vector3(-extents.x, extents.y, -extents.z),
                new Vector3(-extents.x, -extents.y, extents.z), new Vector3(extents.x, -extents.y, extents.z),
                new Vector3(extents.x, extents.y, extents.z), new Vector3(-extents.x, extents.y, extents.z),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6,
                0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
            };
            mesh.RecalculateBounds();
            _meshes.Add(mesh);
            return mesh;
        }

        private static string CreatePhysicsMaterialAsset(string fileName)
        {
            var path = Root + "/" + fileName;
            AssetDatabase.CreateAsset(new AuditPhysicsMaterial(fileName), path);
            return path;
        }
    }
}
