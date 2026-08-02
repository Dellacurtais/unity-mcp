using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Mcp;
using Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class ModelingPhaseZeroTests
    {
        private readonly List<string> _assetFolders = new List<string>();
        private readonly List<GameObject> _gameObjects = new List<GameObject>();
        private readonly List<Mesh> _meshes = new List<Mesh>();

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            for (int i = 0; i < _gameObjects.Count; i++)
                if (_gameObjects[i] != null) UnityEngine.Object.DestroyImmediate(_gameObjects[i]);
            for (int i = 0; i < _meshes.Count; i++)
                if (_meshes[i] != null) UnityEngine.Object.DestroyImmediate(_meshes[i]);
            for (int i = _assetFolders.Count - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(_assetFolders[i])) AssetDatabase.DeleteAsset(_assetFolders[i]);
            _gameObjects.Clear();
            _meshes.Clear();
            _assetFolders.Clear();
        }

        [Test]
        public void MeshRevision_SeparatesGeometryAndTopologyChanges()
        {
            var original = new MeshRevision(3, 7);
            var geometry = original.NextGeometry();
            var topology = original.NextTopology();

            Assert.That(geometry.Topology, Is.EqualTo(3));
            Assert.That(geometry.Geometry, Is.EqualTo(8));
            Assert.That(topology.Topology, Is.EqualTo(4));
            Assert.That(topology.Geometry, Is.EqualTo(8));
            Assert.That(original.Equals(geometry), Is.False);
        }

        [Test]
        public void MeshChannelSnapshot_SubsetPreservesRigUvsColorsAndBlendShapes()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var snapshot = MeshChannelSnapshot.Capture(fixture.Mesh);
                var subset = new[] { 0, 2, 7, 11 };
                var destination = new Mesh { name = "ChannelSubset" };
                _meshes.Add(destination);
                snapshot.ApplyVertexChannels(destination, subset);

                Assert.That(destination.vertexCount, Is.EqualTo(subset.Length));
                Assert.That(destination.normals.Length, Is.EqualTo(subset.Length));
                Assert.That(destination.tangents.Length, Is.EqualTo(subset.Length));
                Assert.That(destination.colors32.Length, Is.EqualTo(subset.Length));
                Assert.That(destination.uv.Length, Is.EqualTo(subset.Length));
                Assert.That(destination.bindposes.Length, Is.EqualTo(fixture.Mesh.bindposes.Length));
                Assert.That(destination.blendShapeCount, Is.EqualTo(1));
                Assert.That(destination.GetBlendShapeName(0), Is.EqualTo("Breath"));
                using (var counts = destination.GetBonesPerVertex())
                {
                    Assert.That(counts.Length, Is.EqualTo(subset.Length));
                    for (int i = 0; i < counts.Length; i++) Assert.That(counts[i], Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void MeshFingerprint_ChangesWhenAProtectedChannelChanges()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var before = MeshFingerprint.Compute(fixture.Mesh);
                var uvs = new List<Vector2>();
                fixture.Mesh.GetUVs(0, uvs);
                uvs[0] = new Vector2(0.25f, 0.75f);
                fixture.Mesh.SetUVs(0, uvs);
                var after = MeshFingerprint.Compute(fixture.Mesh);

                Assert.That(after, Is.Not.EqualTo(before));
            }
        }

        [Test]
        public void BoneWeightValidation_ToleratesQuantizationAndOrderingButRejectsRealDrift()
        {
            var expected = new[]
            {
                new BoneWeight1 { boneIndex = 3, weight = 0.7000000f },
                new BoneWeight1 { boneIndex = 8, weight = 0.3000000f },
            };
            var quantizedAndReordered = new[]
            {
                new BoneWeight1 { boneIndex = 8, weight = 0.3000076f },
                new BoneWeight1 { boneIndex = 3, weight = 0.6999924f },
            };
            Assert.That(
                SplitFidelityValidator.BoneWeightsEquivalent(
                    expected, 0, expected.Length,
                    quantizedAndReordered, 0, quantizedAndReordered.Length,
                    out var quantizationDifference),
                Is.True,
                quantizationDifference);

            var changed = new[]
            {
                new BoneWeight1 { boneIndex = 3, weight = 0.68f },
                new BoneWeight1 { boneIndex = 8, weight = 0.32f },
            };
            Assert.That(
                SplitFidelityValidator.BoneWeightsEquivalent(
                    expected, 0, expected.Length,
                    changed, 0, changed.Length,
                    out var realDifference),
                Is.False);
            StringAssert.Contains("bone 3", realDifference);
            StringAssert.Contains("delta", realDifference);
        }

        [Test]
        public void Analyzer_GenericRigProducesSixCompleteParts()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var proposal = SmartBodySplitAnalyzer.Analyze(fixture.Root);

                Assert.That(proposal.RigType, Is.EqualTo("Generic"));
                Assert.That(proposal.Parts.Count, Is.EqualTo(6));
                Assert.That(proposal.SourceTriangleCount, Is.EqualTo(6));
                Assert.That(proposal.AverageConfidence, Is.EqualTo(1f).Within(1e-6f));
                Assert.That(proposal.SourceHash, Has.Length.EqualTo(64));
                Assert.That(proposal.PreviewHash, Has.Length.EqualTo(64));
                foreach (SemanticBodyPart part in new[]
                {
                    SemanticBodyPart.Head,
                    SemanticBodyPart.Torso,
                    SemanticBodyPart.LeftArm,
                    SemanticBodyPart.RightArm,
                    SemanticBodyPart.LeftLeg,
                    SemanticBodyPart.RightLeg,
                })
                    Assert.That(proposal.Find(part)?.Triangles.Count, Is.EqualTo(1), part.ToString());
            }
        }

        [Test]
        public void Analyzer_StaticFallbackAssignsEveryTriangle()
        {
            var gameObject = new GameObject("StaticBody");
            _gameObjects.Add(gameObject);
            var mesh = ModelingTestMeshFactory.CreateGrid(4, 8);
            _meshes.Add(mesh);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            gameObject.AddComponent<MeshRenderer>();

            var proposal = SmartBodySplitAnalyzer.Analyze(gameObject);

            Assert.That(proposal.RigType, Is.EqualTo("None"));
            Assert.That(proposal.SourceTriangleCount, Is.EqualTo(4 * 8 * 2));
            Assert.That(proposal.Parts, Is.Not.Empty);
            Assert.That(string.Join(" | ", proposal.Warnings), Does.Contain("sem rig"));
        }

        [Test]
        public void EditableCopy_CreatesIndependentMeshAndDocumentWithoutChangingSource()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var sourceHash = MeshFingerprint.Compute(fixture.Mesh);
                var folder = NewAssetFolder();
                var result = EditableMeshFactory.Create(fixture.Root, folder);

                Assert.That(result.EditableObject, Is.Not.Null);
                Assert.That(result.EditableMesh, Is.Not.SameAs(fixture.Mesh));
                Assert.That(result.Document.WorkingMesh, Is.SameAs(result.EditableMesh));
                Assert.That(result.Document.SourceHash, Is.EqualTo(sourceHash));
                Assert.That(MeshFingerprint.Compute(fixture.Mesh), Is.EqualTo(sourceHash));
                Assert.That(result.CreatedAssetPaths, Has.Count.EqualTo(2));
            }
        }

        [Test]
        public void Split_CreatesValidatedPartsAndLeavesSourceUntouched()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var sourceHash = MeshFingerprint.Compute(fixture.Mesh);
                var proposal = SmartBodySplitAnalyzer.Analyze(fixture.Root);
                var folder = NewAssetFolder();
                var result = SmartBodySplitService.Apply(
                    proposal,
                    folder,
                    proposal.SourceHash,
                    proposal.PreviewHash);

                Assert.That(result.Validation.IsValid, Is.True);
                Assert.That(result.Manifest.IsValid, Is.True);
                Assert.That(result.Manifest.Parts.Count, Is.EqualTo(6));
                Assert.That(result.BodyPartSet.Parts.Count, Is.EqualTo(6));
                Assert.That(result.PartsRoot, Is.Not.Null);
                Assert.That(result.PartsRoot.GetComponentsInChildren<SkinnedMeshRenderer>(), Has.Length.EqualTo(6));
                Assert.That(MeshFingerprint.Compute(fixture.Mesh), Is.EqualTo(sourceHash));
                foreach (var part in result.Manifest.Parts)
                {
                    Assert.That(part.Mesh, Is.Not.Null);
                    Assert.That(part.Mesh.blendShapeCount, Is.EqualTo(1));
                    Assert.That(part.Mesh.bindposes.Length, Is.EqualTo(fixture.Mesh.bindposes.Length));
                    using (var counts = part.Mesh.GetBonesPerVertex())
                        Assert.That(counts.Length, Is.EqualTo(part.Mesh.vertexCount));
                }
            }
        }

        [Test]
        public void Split_RejectsStaleSourceAndPreviewHashesWithoutCreatingAssets()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var proposal = SmartBodySplitAnalyzer.Analyze(fixture.Root);
                var folder = NewAssetFolder();

                var sourceFailure = Assert.Throws<InvalidOperationException>(() =>
                    SmartBodySplitService.Apply(proposal, folder, new string('0', 64), proposal.PreviewHash));
                StringAssert.Contains("STALE_SOURCE_HASH", sourceFailure.Message);

                var previewFailure = Assert.Throws<InvalidOperationException>(() =>
                    SmartBodySplitService.Apply(proposal, folder, proposal.SourceHash, new string('f', 64)));
                StringAssert.Contains("STALE_PREVIEW_HASH", previewFailure.Message);
                Assert.That(AssetDatabase.FindAssets(string.Empty, new[] { folder }), Is.Empty);
            }
        }

        [Test]
        public void Split_RequiresApprovalAndRejectsRendererMeshSwap()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var proposal = SmartBodySplitAnalyzer.Analyze(fixture.Root);
                var approvalFailure = Assert.Throws<InvalidOperationException>(() =>
                    SmartBodySplitService.Apply(proposal, null, null, null));
                StringAssert.Contains("PREVIEW_APPROVAL_REQUIRED", approvalFailure.Message);

                var replacement = UnityEngine.Object.Instantiate(fixture.Mesh);
                replacement.name = fixture.Mesh.name;
                _meshes.Add(replacement);
                fixture.Renderer.sharedMesh = replacement;
                var bindingFailure = Assert.Throws<InvalidOperationException>(() =>
                    SmartBodySplitService.Apply(proposal, null, proposal.SourceHash, proposal.PreviewHash));
                StringAssert.Contains("STALE_SOURCE_BINDING", bindingFailure.Message);
            }
        }

        [Test]
        public void AssetPaths_RejectTraversalSegments()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                AssetModelerAssetPaths.ValidateAndEnsureFolder("Assets/../Outside"));
            StringAssert.Contains("cannot contain", exception.Message);
        }

        [Test]
        public void Split_RegistersSceneResultWithUndo()
        {
            using (var fixture = ModelingTestMeshFactory.CreateSixPartCharacter())
            {
                var proposal = SmartBodySplitAnalyzer.Analyze(fixture.Root);
                var result = SmartBodySplitService.Apply(
                    proposal,
                    NewAssetFolder(),
                    proposal.SourceHash,
                    proposal.PreviewHash);
                Assert.That(result.PartsRoot, Is.Not.Null);

                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();

                Assert.That(result.PartsRoot == null, Is.True, "Undo must remove the generated scene hierarchy.");
            }
        }

        [Test]
        public void SmartSplitMcpTools_RegisterValidContractsAndExplicitRisk()
        {
            var registry = new McpToolRegistry();
            registry.RegisterRange(new IMcpTool[]
            {
                new AnalyzeBodyPartsTool(),
                new PreviewSmartBodySplitTool(),
                new ApplySmartBodySplitTool(),
                new ValidateSmartBodySplitTool(),
            });

            Assert.That(registry.Count, Is.EqualTo(4));
            var apply = registry.GetDescriptor("mesh_apply_smart_split");
            Assert.That(apply.Capabilities.HasFlag(McpCapability.AssetWrite), Is.True);
            Assert.That(apply.Capabilities.HasFlag(McpCapability.SceneWrite), Is.True);
            Assert.That(apply.Capabilities.HasFlag(McpCapability.Destructive), Is.True);
            Assert.That(apply.ConfirmationPolicy, Is.EqualTo(McpConfirmationPolicy.Required));
            var required = registry.GetInputSchema("mesh_apply_smart_split")["required"].ToString();
            Assert.That(required, Does.Contain("expectedSourceHash").And.Contain("previewHash"));
        }

        private string NewAssetFolder()
        {
            var folder = $"Assets/__AssetModelerPhase0Tests_{Guid.NewGuid():N}";
            AssetModelerAssetPaths.ValidateAndEnsureFolder(folder);
            _assetFolders.Add(folder);
            return folder;
        }
    }
}
