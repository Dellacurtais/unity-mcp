using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Transform;
using Maicon.SceneAnalyzer.Editor.Modeling.SceneTools;
using Maicon.SceneAnalyzer.Editor.Modeling.SceneUx;
using Maicon.SceneAnalyzer.Editor.Modeling.UI;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class AssetModelerUxTests
    {
        [Test]
        public void Navigation_OpensOneWorkspaceAndReturnsHome()
        {
            var navigation = new AssetModelerNavigationState();

            Assert.That(navigation.IsHome, Is.True);
            navigation.Open(AssetModelerWorkspace.Topology);
            Assert.That(navigation.Workspace, Is.EqualTo(AssetModelerWorkspace.Topology));
            Assert.That(navigation.IsHome, Is.False);
            navigation.Back();
            Assert.That(navigation.Workspace, Is.EqualTo(AssetModelerWorkspace.Home));
        }

        [Test]
        public void WorkspaceCatalog_ExposesContextualCapabilities()
        {
            Assert.That(
                AssetModelerWorkspaceCatalog.ObjectToolLabels.Length,
                Is.EqualTo(Enum.GetValues(typeof(AssetModelerObjectTool)).Length));
            Assert.That(
                AssetModelerWorkspaceCatalog.TopologyToolLabels.Length,
                Is.EqualTo(Enum.GetValues(typeof(AssetModelerTopologyTool)).Length));
            Assert.That(AssetModelerWorkspaceCatalog.TopologyUsesFaces(AssetModelerTopologyTool.ExtrudeFaces), Is.True);
            Assert.That(AssetModelerWorkspaceCatalog.TopologyUsesFaces(AssetModelerTopologyTool.FillHole), Is.False);
            Assert.That(
                AssetModelerWorkspaceCatalog.SupportsMeshPreview(
                    AssetModelerWorkspace.Objects,
                    AssetModelerObjectTool.Transform,
                    AssetModelerTopologyTool.Cleanup),
                Is.True);
            Assert.That(
                AssetModelerWorkspaceCatalog.SupportsMeshPreview(
                    AssetModelerWorkspace.Objects,
                    AssetModelerObjectTool.Array,
                    AssetModelerTopologyTool.Cleanup),
                Is.False);
        }

        [Test]
        public void SceneFacePicker_ReturnsNearestFaceAndHonorsTransform()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(0f, 1f, 0f),
                    new Vector3(-1f, -1f, 1f), new Vector3(1f, -1f, 1f), new Vector3(0f, 1f, 1f),
                };
                mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
                var topology = HalfEdgeTopology.Build(mesh);
                var transform = Matrix4x4.TRS(new Vector3(10f, 0f, 0f), Quaternion.identity, Vector3.one);

                bool hit = AssetModelerSceneController.TryPickFace(
                    new Ray(new Vector3(10f, 0f, -2f), Vector3.forward),
                    transform.inverse,
                    topology,
                    mesh.vertices,
                    out int face);

                Assert.That(hit, Is.True);
                Assert.That(face, Is.EqualTo(0), "The closest visible triangle must win.");
                Assert.That(
                    AssetModelerSceneController.TryPickFace(
                        new Ray(new Vector3(20f, 0f, -2f), Vector3.forward),
                        transform.inverse,
                        topology,
                        mesh.vertices,
                        out _),
                    Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void SceneMarqueePicker_DistinguishesContainedAndCrossingFaces()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(1f, 1f, 0f), new Vector3(2f, 1f, 0f), new Vector3(1f, 2f, 0f),
                    new Vector3(2.5f, 2.5f, 0f), new Vector3(4f, 2.5f, 0f), new Vector3(2.5f, 4f, 0f),
                    new Vector3(5f, 5f, 0f), new Vector3(6f, 5f, 0f), new Vector3(5f, 6f, 0f),
                };
                mesh.triangles = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
                var topology = HalfEdgeTopology.Build(mesh);
                var rect = new Rect(0f, 0f, 3f, 3f);
                Vector3 Project(Vector3 point) => new Vector3(point.x, point.y, 1f);

                var contained = AssetModelerSceneController.CollectFacesInGuiRect(
                    rect, false, Matrix4x4.identity, topology, mesh.vertices, Project);
                var crossing = AssetModelerSceneController.CollectFacesInGuiRect(
                    rect, true, Matrix4x4.identity, topology, mesh.vertices, Project);

                Assert.That(contained, Is.EqualTo(new[] { 0 }));
                Assert.That(crossing, Is.EqualTo(new[] { 0, 1 }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void SceneConnectedSelection_ExpandsOnlySeededMeshIsland()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f),
                    new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 0f), new Vector3(3f, 1f, 0f),
                };
                mesh.triangles = new[] { 0, 1, 2, 1, 3, 2, 4, 5, 6 };
                var topology = HalfEdgeTopology.Build(mesh);

                var connected = AssetModelerSceneController.CollectConnectedFaces(topology, new[] { 0 });

                Assert.That(connected, Is.EqualTo(new[] { 0, 1 }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void EdgeSelection_PreservesCanonicalPairsAcrossRegionsAndRemap()
        {
            var selection = new MeshSelectionState();
            selection.SetSelection(MeshElementKind.Edge, new[] { 2, 1, 1, 2, 3, 4, 99 }, 3);

            Assert.That(selection.Indices, Is.EqualTo(new[] { 1, 2, 3, 4 }));
            selection.SaveRegion("Edges");
            Assert.That(selection.Regions[0].Indices, Is.EqualTo(new[] { 1, 2, 3, 4 }));

            selection.RemapTopologyPreservingVertices(4, 4);

            Assert.That(selection.Indices, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(selection.Regions[0].Indices, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void BoundaryEdgeLoop_ReturnsClosedEdgePairsInsteadOfVertexSequence()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                var topology = HalfEdgeTopology.Build(mesh);

                var encoded = AssetModelerModelingTool.CollectEdgeLoop(
                    topology, mesh.vertices, new MeshEdgeKey(0, 1));
                var actual = MeshSelectionEncoding.EdgePairs(encoded);

                Assert.That(encoded, Has.Length.EqualTo(8));
                Assert.That(actual, Is.EquivalentTo(topology.BoundaryEdges));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void UvSeamDiagnostic_FindsDuplicatedGeometricEdgeWithDifferentUvs()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    Vector3.zero, Vector3.right, Vector3.up,
                    Vector3.right, Vector3.one, Vector3.up,
                };
                mesh.uv = new[]
                {
                    Vector2.zero, Vector2.right, Vector2.up,
                    new Vector2(0.2f, 0f), Vector2.one, new Vector2(0.2f, 1f),
                };
                mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };

                var seams = AssetModelerMeshDiagnostics.FindUvSeams(mesh, HalfEdgeTopology.Build(mesh));

                Assert.That(seams, Does.Contain(new MeshEdgeKey(1, 2)));
                Assert.That(seams, Does.Contain(new MeshEdgeKey(3, 5)));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void AttachedLinearArray_UsesBoundsAsPitchWithoutHiddenGap()
        {
            var source = new GameObject("ArraySource");
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(-1f, -0.5f, 0f), new Vector3(1f, -0.5f, 0f), new Vector3(0f, 0.5f, 0f),
                };
                mesh.triangles = new[] { 0, 1, 2 };
                source.AddComponent<MeshFilter>().sharedMesh = mesh;
                source.AddComponent<MeshRenderer>();
                var settings = new SceneArraySettings
                {
                    Mode = SceneArrayMode.Linear,
                    Count = 3,
                    Step = Vector3.right,
                    AttachedByBounds = true,
                };

                var poses = SceneObjectOperations.BuildArrayTransforms(source, settings);

                Assert.That(poses.Select(pose => pose.position.x), Is.EqualTo(new[] { 0f, 2f, 4f }).Within(1e-5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MirrorAndWeld_ModifierRoundTripsThroughRegistryAndRebuild()
        {
            var source = new Mesh { name = "HalfMesh" };
            var working = new Mesh { name = "HalfMesh_Working" };
            var document = ScriptableObject.CreateInstance<MeshEditDocument>();
            try
            {
                source.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                source.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
                source.triangles = new[] { 0, 1, 2 };
                source.RecalculateNormals();
                working.vertices = source.vertices;
                working.uv = source.uv;
                working.triangles = source.triangles;
                working.normals = source.normals;
                document.Initialize(null, source, working);
                ModelingOperationCatalog.RegisterAll();
                var operation = new MirrorAndWeldMeshOperation(0, 0f, 1e-5f, true);

                string hash;
                using (var preview = ModelingCommandService.Preview(document, operation, document.Revision))
                    hash = preview.PreviewHash;
                ModelingCommandService.Apply(document, operation, document.Revision, hash);
                var rebuilt = ModelingCommandService.Rebuild(document);

                Assert.That(document.ModifierStack, Has.Count.EqualTo(1));
                Assert.That(document.ModifierStack[0].OperationId, Is.EqualTo("mesh.mirror_weld"));
                Assert.That(rebuilt.IsValid, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(document);
                UnityEngine.Object.DestroyImmediate(working);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void BoneSelection_ConsidersAllModernBoneWeightInfluences()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[] { Vector3.zero };
                using (var counts = new NativeArray<byte>(new byte[] { 5 }, Allocator.Temp))
                using (var weights = new NativeArray<BoneWeight1>(new[]
                {
                    new BoneWeight1 { boneIndex = 4, weight = 0.5f },
                    new BoneWeight1 { boneIndex = 3, weight = 0.2f },
                    new BoneWeight1 { boneIndex = 2, weight = 0.15f },
                    new BoneWeight1 { boneIndex = 1, weight = 0.1f },
                    new BoneWeight1 { boneIndex = 0, weight = 0.05f },
                }, Allocator.Temp))
                    mesh.SetBoneWeights(counts, weights);

                Assert.That(AssetModelerModelingTool.CollectDominantVertices(mesh, 4), Is.EqualTo(new[] { 0 }));
                Assert.That(AssetModelerModelingTool.CollectDominantVertices(mesh, 3), Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
