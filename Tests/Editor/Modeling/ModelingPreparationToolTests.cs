using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Mcp;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class ModelingPreparationToolTests
    {
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = AssetModelerAssetPaths.ValidateAndEnsureFolder("Assets/AssetModelerTests/Preparation_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null && !AssetDatabase.Contains(_objects[i])) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            AssetDatabase.DeleteAsset(_folder);
            Undo.ClearAll();
        }

        [Test]
        public void EditableCopy_ClonesSiblingBonesAndColliderWithoutSharingRig()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var collider = fixture.Renderer.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = fixture.Mesh;
                var result = EditableMeshFactory.Create(fixture.Renderer.gameObject, _folder);
                _objects.Add(result.EditableRoot);
                var skin = (SkinnedMeshRenderer)result.EditableRenderer;
                Assert.That(result.EditableRoot, Is.Not.EqualTo(result.EditableObject));
                Assert.That(skin.bones.Length, Is.EqualTo(fixture.Renderer.bones.Length));
                for (int i = 0; i < skin.bones.Length; i++)
                {
                    Assert.That(skin.bones[i], Is.Not.SameAs(fixture.Renderer.bones[i]));
                    Assert.That(skin.bones[i].IsChildOf(result.EditableRoot.transform), Is.True);
                }
                Assert.That(skin.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(result.EditableMesh));
                Assert.That(collider.sharedMesh, Is.SameAs(fixture.Mesh));
                var originalBonePosition = fixture.Renderer.bones[0].position;
                skin.bones[0].position += Vector3.up;
                Assert.That(fixture.Renderer.bones[0].position, Is.EqualTo(originalBonePosition));
            }
        }

        [Test]
        public void PreparingAnotherMeshInCopy_PreservesRootAndPreviousEdits()
        {
            var source = new GameObject("Model"); _objects.Add(source);
            var first = GameObject.CreatePrimitive(PrimitiveType.Cube); first.transform.SetParent(source.transform, false);
            var second = GameObject.CreatePrimitive(PrimitiveType.Cube); second.transform.SetParent(source.transform, false);
            var firstMesh = first.GetComponent<MeshFilter>().sharedMesh;
            var a = EditableMeshFactory.CreateModelCopy(source, first.GetComponent<MeshRenderer>(), _folder);
            var copy = a.EditableRoot; _objects.Add(copy);
            var vertices = a.EditableMesh.vertices; vertices[0] += Vector3.up; a.EditableMesh.vertices = vertices;
            var b = EditableMeshFactory.CreateForExistingCopy(copy, copy.transform.GetChild(1).GetComponent<MeshRenderer>(), _folder);
            Assert.That(b.EditableRoot, Is.SameAs(copy));
            Assert.That(copy.transform.childCount, Is.EqualTo(2));
            Assert.That(a.EditableMesh.vertices[0], Is.EqualTo(vertices[0]));
            Assert.That(a.EditableRenderer.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(a.EditableMesh));
            Assert.That(b.EditableMesh, Is.Not.SameAs(a.EditableMesh));
            Assert.That(first.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(firstMesh));
            Assert.That(firstMesh.vertices[0], Is.Not.EqualTo(vertices[0]));
        }

        [Test]
        public void RendererSelection_RejectsAmbiguousRootAndAcceptsExplicitMesh()
        {
            var root = new GameObject("TwoMeshes"); _objects.Add(root);
            var first = GameObject.CreatePrimitive(PrimitiveType.Cube); first.transform.SetParent(root.transform);
            var second = GameObject.CreatePrimitive(PrimitiveType.Cube); second.transform.SetParent(root.transform);
            Assert.That(EditableMeshFactory.TryResolveRenderer(root, out _, out _, out var error), Is.False);
            StringAssert.Contains("AMBIGUOUS_MESH", error);
            Assert.That(EditableMeshFactory.TryResolveRenderer(second, out var selected, out _, out _), Is.True);
            Assert.That(selected.gameObject, Is.SameAs(second));
        }

        [Test]
        public void PreparationTools_AreRegisteredWithTypedContracts()
        {
            foreach (var name in new[] { "mesh_analyze_cleanup", "mesh_preview_cleanup", "mesh_apply_cleanup", "model_preview_scale", "model_apply_scale" })
            {
                Assert.That(Maicon.SceneAnalyzer.Editor.Bootstrap.ToolRegistry.TryGet(name, out var tool), Is.True, name);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var error), Is.True, error);
                Assert.That(tool.Descriptor.Category, Is.EqualTo("mesh"));
            }
        }

        [Test]
        public void PreparationSchemas_AreValidAndRejectMissingApprovalOrUnknownFlags()
        {
            foreach (var tool in new IMcpTool[] { new AnalyzeMeshCleanupTool(), new PreviewMeshCleanupTool(), new ApplyMeshCleanupTool(), new PreviewModelScaleTool(), new ApplyModelScaleTool() })
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError), Is.True, tool.Name + ": " + inputError);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError), Is.True, tool.Name + ": " + outputError);
            }
            var input = new JObject { ["documentAssetPath"] = _folder + "/document.asset", ["expectedTopologyRevision"] = 0, ["expectedGeometryRevision"] = 0 };
            Assert.That(ToolSchemaValidator.ValidateArguments(input, new PreviewMeshCleanupTool().InputSchema, out _), Is.True);
            Assert.That(ToolSchemaValidator.ValidateArguments(input, new ApplyMeshCleanupTool().InputSchema, out _), Is.False);
            input["deleteAllSmallParts"] = true;
            Assert.That(ToolSchemaValidator.ValidateArguments(input, new PreviewMeshCleanupTool().InputSchema, out _), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CleanupSchemas_RequireDiagnosisHashOnlyForExplicitComponents(bool apply)
        {
            IMcpTool tool = apply ? (IMcpTool)new ApplyMeshCleanupTool() : new PreviewMeshCleanupTool();
            var input = new JObject
            {
                ["documentAssetPath"] = _folder + "/document.asset",
                ["expectedTopologyRevision"] = 0, ["expectedGeometryRevision"] = 0,
            };
            if (apply) input["previewHash"] = new string('a', 64);
            Assert.That(ToolSchemaValidator.ValidateArguments(input, tool.InputSchema, out _), Is.True);
            input["removeComponentIds"] = new JArray();
            Assert.That(ToolSchemaValidator.ValidateArguments(input, tool.InputSchema, out _), Is.True);
            input["removeComponentIds"] = new JArray(1);
            Assert.That(ToolSchemaValidator.ValidateArguments(input, tool.InputSchema, out _), Is.False);
            input["expectedMeshHash"] = new string('b', 64);
            Assert.That(ToolSchemaValidator.ValidateArguments(input, tool.InputSchema, out var error), Is.True, error);
        }

        [Test]
        public void CleanupTools_BindSelectedComponentsToDiagnosisHashAcrossExternalTopologyChanges()
        {
            var source = new Mesh { name = "ThreeComponents" }; _objects.Add(source);
            source.vertices = new[]
            {
                Vector3.zero, Vector3.right, Vector3.up,
                Vector3.right * 10, Vector3.right * 11, Vector3.right * 10 + Vector3.up,
                Vector3.right * 20, Vector3.right * 21, Vector3.right * 20 + Vector3.up,
            };
            source.triangles = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            var go = new GameObject("Fragments"); _objects.Add(go);
            go.AddComponent<MeshFilter>().sharedMesh = source; go.AddComponent<MeshRenderer>();
            var session = EditableMeshFactory.Create(go, _folder); _objects.Add(session.EditableRoot);
            var path = AssetDatabase.GetAssetPath(session.Document);
            var diagnoseTool = new AnalyzeMeshCleanupTool();
            var diagnosis = diagnoseTool.Execute(new JObject { ["documentAssetPath"] = path });
            AssertValidResult(diagnoseTool, diagnosis);
            Assert.That((float)diagnosis.Json["components"][1]["bounds"]["center"][0], Is.EqualTo(10.5f));
            var args = new JObject
            {
                ["documentAssetPath"] = path, ["expectedTopologyRevision"] = 0, ["expectedGeometryRevision"] = 0,
                ["removeComponentIds"] = new JArray(1),
            };
            var previewTool = new PreviewMeshCleanupTool();
            var applyTool = new ApplyMeshCleanupTool();
            var initialHash = MeshFingerprint.Compute(session.EditableMesh);
            AssertErrorCode(previewTool.Execute(args), "EXPECTED_MESH_HASH_REQUIRED");
            args["previewHash"] = new string('a', 64);
            AssertErrorCode(applyTool.Execute(args), "EXPECTED_MESH_HASH_REQUIRED");
            Assert.That(MeshFingerprint.Compute(session.EditableMesh), Is.EqualTo(initialHash));

            args.Remove("previewHash");
            args["expectedMeshHash"] = diagnosis.Json["meshHash"].DeepClone();
            var preview = previewTool.Execute(args); AssertValidResult(previewTool, preview);
            args["previewHash"] = preview.Json["previewHash"].DeepClone();

            // An external API can change the mesh without advancing its edit-document revision.
            // Removing the first face shifts component id 1 from the middle island to the last.
            session.EditableMesh.triangles = new[] { 3, 4, 5, 6, 7, 8 };
            Assert.That(session.Document.Revision.Topology, Is.EqualTo(0));
            Assert.That(session.Document.Revision.Geometry, Is.EqualTo(0));
            var changedHash = MeshFingerprint.Compute(session.EditableMesh);
            var changedDiagnosis = diagnoseTool.Execute(new JObject { ["documentAssetPath"] = path });
            AssertValidResult(diagnoseTool, changedDiagnosis);
            Assert.That((float)changedDiagnosis.Json["components"][1]["bounds"]["center"][0], Is.EqualTo(20.5f));
            var previewArgs = (JObject)args.DeepClone(); previewArgs.Remove("previewHash");
            AssertErrorCode(previewTool.Execute(previewArgs), "STALE_MESH_HASH");
            AssertErrorCode(applyTool.Execute(args), "STALE_MESH_HASH");
            Assert.That(MeshFingerprint.Compute(session.EditableMesh), Is.EqualTo(changedHash));

            // Optional hashes also guard default cleanup, even with no explicit component selection.
            previewArgs.Remove("removeComponentIds"); args.Remove("removeComponentIds");
            AssertErrorCode(previewTool.Execute(previewArgs), "STALE_MESH_HASH");
            AssertErrorCode(applyTool.Execute(args), "STALE_MESH_HASH");
            Assert.That(MeshFingerprint.Compute(session.EditableMesh), Is.EqualTo(changedHash));

            previewArgs["removeComponentIds"] = new JArray(1);
            previewArgs["expectedMeshHash"] = changedDiagnosis.Json["meshHash"].DeepClone();
            preview = previewTool.Execute(previewArgs); AssertValidResult(previewTool, preview);
            previewArgs["previewHash"] = preview.Json["previewHash"].DeepClone();
            AssertValidResult(applyTool, applyTool.Execute(previewArgs));
            Assert.That(session.EditableMesh.vertexCount, Is.EqualTo(3));
            Assert.That(session.EditableMesh.vertices[0], Is.EqualTo(Vector3.right * 10));
            Assert.That(source.vertexCount, Is.EqualTo(9));
        }

        [Test]
        public void CleanupTools_DefaultsPreserveValidPartsAndRejectStalePreview()
        {
            var source = new Mesh { name = "LooseVertex" }; _objects.Add(source);
            source.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one * 99f };
            source.triangles = new[] { 0, 1, 2 };
            var go = new GameObject("DirtyMesh"); _objects.Add(go);
            go.AddComponent<MeshFilter>().sharedMesh = source; go.AddComponent<MeshRenderer>();
            var session = EditableMeshFactory.Create(go, _folder); _objects.Add(session.EditableRoot);
            var args = new JObject { ["documentAssetPath"] = AssetDatabase.GetAssetPath(session.Document), ["expectedTopologyRevision"] = 0, ["expectedGeometryRevision"] = 0 };
            var diagnoseTool = new AnalyzeMeshCleanupTool();
            var diagnosis = diagnoseTool.Execute(new JObject { ["documentAssetPath"] = args["documentAssetPath"].DeepClone() });
            AssertValidResult(diagnoseTool, diagnosis);
            Assert.That((int)diagnosis.Json["unusedVertices"], Is.EqualTo(1));
            var previewTool = new PreviewMeshCleanupTool();
            var preview = previewTool.Execute(args); AssertValidResult(previewTool, preview);
            Assert.That((int)preview.Json["removedVertices"], Is.EqualTo(1));
            Assert.That(session.EditableMesh.vertexCount, Is.EqualTo(4));
            args["previewHash"] = preview.Json["previewHash"].DeepClone();
            var vertices = session.EditableMesh.vertices; vertices[1] *= 2; session.EditableMesh.vertices = vertices;
            var staleDiagnosis = diagnoseTool.Execute(new JObject
            {
                ["documentAssetPath"] = args["documentAssetPath"].DeepClone(),
                ["expectedMeshHash"] = diagnosis.Json["meshHash"].DeepClone(), ["offset"] = 0, ["limit"] = 1,
            });
            Assert.That(staleDiagnosis.IsError, Is.True);
            Assert.That((string)staleDiagnosis.Json["code"], Is.EqualTo("STALE_MESH_HASH"));
            Assert.That(new ApplyMeshCleanupTool().Execute(args).IsError, Is.True);
            Assert.That(session.EditableMesh.vertexCount, Is.EqualTo(4));
            args.Remove("previewHash");
            preview = previewTool.Execute(args); AssertValidResult(previewTool, preview);
            args["previewHash"] = preview.Json["previewHash"].DeepClone();
            var applyTool = new ApplyMeshCleanupTool();
            AssertValidResult(applyTool, applyTool.Execute(args));
            Assert.That(session.EditableMesh.vertexCount, Is.EqualTo(3));
            Assert.That(session.EditableMesh.vertices[1], Is.EqualTo(Vector3.right * 2));
            Assert.That(source.vertexCount, Is.EqualTo(4));
        }

        [Test]
        public void ScalePreviewTool_ReportsDimensionsAndCreatesNoAssets()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); _objects.Add(go);
            go.transform.localScale = Vector3.one * 3;
            var tool = new PreviewModelScaleTool();
            var before = AssetDatabase.FindAssets(string.Empty, new[] { _folder }).Length;
            var result = tool.Execute(new JObject
            {
                ["target"] = ToolTargetRef.Capture(go).ToJson(), ["mode"] = "target_dimension",
                ["axis"] = "y", ["targetDimension"] = 1.8, ["outputFolder"] = _folder,
            });
            AssertValidResult(tool, result);
            Assert.That((float)result.Json["boundsAfter"]["size"][1], Is.EqualTo(1.8f).Within(1e-5f));
            Assert.That(go.transform.localScale, Is.EqualTo(Vector3.one * 3));
            Assert.That(AssetDatabase.FindAssets(string.Empty, new[] { _folder }).Length, Is.EqualTo(before));
        }

        private static void AssertValidResult(IMcpTool tool, ToolResult result)
        {
            Assert.That(result.IsError, Is.False, result.Text);
            Assert.That(ToolSchemaValidator.ValidateArguments((JObject)result.Json, tool.OutputSchema, out var error), Is.True, error);
        }

        private static void AssertErrorCode(ToolResult result, string code)
        {
            Assert.That(result.IsError, Is.True, result.Text);
            Assert.That((string)result.Json["code"], Is.EqualTo(code), result.Text);
        }
    }
}
