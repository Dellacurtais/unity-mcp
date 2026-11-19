using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class RendererMeshUtilityTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void MeshRendererAndSkinnedRenderer_ReturnAssignedMesh(bool skinned)
        {
            var root = new GameObject("MeshOwner");
            var mesh = new Mesh();
            try
            {
                Renderer renderer;
                if (skinned)
                {
                    var skin = root.AddComponent<SkinnedMeshRenderer>();
                    skin.sharedMesh = mesh;
                    renderer = skin;
                }
                else
                {
                    root.AddComponent<MeshFilter>().sharedMesh = mesh;
                    renderer = root.AddComponent<MeshRenderer>();
                }
                Assert.That(RendererMeshUtility.GetSharedMesh(renderer), Is.SameAs(mesh));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NullAndDestroyedRenderers_ReturnNull(bool skinned)
        {
            Assert.That(RendererMeshUtility.GetSharedMesh(null), Is.Null);
            var root = new GameObject("DestroyedRenderer");
            try
            {
                Renderer renderer = skinned ? (Renderer)root.AddComponent<SkinnedMeshRenderer>() : root.AddComponent<MeshRenderer>();
                Object.DestroyImmediate(renderer);
                Assert.That(ReferenceEquals(renderer, null), Is.False, "Keep the destroyed Unity wrapper for this regression.");
                Assert.That(RendererMeshUtility.GetSharedMesh(renderer), Is.Null);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void MissingEmptyAndDestroyedMeshFilters_ReturnNull()
        {
            var root = new GameObject("IncompleteMeshRenderer");
            try
            {
                var renderer = root.AddComponent<MeshRenderer>();
                Assert.That(RendererMeshUtility.GetSharedMesh(renderer), Is.Null);
                var filter = root.AddComponent<MeshFilter>();
                Assert.That(RendererMeshUtility.GetSharedMesh(renderer), Is.Null);
                Object.DestroyImmediate(filter);
                Assert.That(RendererMeshUtility.GetSharedMesh(renderer), Is.Null);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void LineRenderer_WithIncidentalMeshFilter_DoesNotOwnThatMesh()
        {
            var root = new GameObject("BeamHalo");
            var mesh = new Mesh();
            try
            {
                var renderer = root.AddComponent<LineRenderer>();
                Assert.That(RendererMeshUtility.GetSharedMesh(renderer), Is.Null);
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                Assert.That(RendererMeshUtility.GetSharedMesh(renderer), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
