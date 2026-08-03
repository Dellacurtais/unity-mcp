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
    }
}
