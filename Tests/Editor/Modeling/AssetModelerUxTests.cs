using System;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.UI;
using NUnit.Framework;
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
    }
}
