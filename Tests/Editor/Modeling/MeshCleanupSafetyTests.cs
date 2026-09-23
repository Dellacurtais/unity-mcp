using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class MeshCleanupSafetyTests
    {
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var value in _objects) if (value != null) UnityEngine.Object.DestroyImmediate(value);
            _objects.Clear();
        }

        [Test]
        public void DefaultCleanupRemovesOnlyUnusedAndExactDegeneratesKeepingTinyFacesAndIslands()
        {
            var mesh = Track(new Mesh { name = "TinyValidIsland" });
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.zero,
                new Vector3(1e-12f, 0f, 0f), new Vector3(0f, 1e-12f, 0f), Vector3.one * 99f };
            mesh.vertices = positions;
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5, 0, 0, 1 };
            var analysis = MeshCleanupAnalysis.Analyze(mesh);
            Assert.That(analysis.Components.Count, Is.EqualTo(2));
            Assert.That(analysis.DegenerateFaceCount, Is.EqualTo(1));
            Assert.That(analysis.UnreferencedVertexCount, Is.EqualTo(1));
            var result = CleanupMeshOperation.Deserialize("{}").Apply(Context(mesh));
            Assert.That(result.RemovedVertexCount, Is.EqualTo(1));
            Assert.That(result.RemovedFaceCount, Is.EqualTo(1));
            Assert.That(result.RemovedComponentCount, Is.Zero);
            CollectionAssert.AreEqual(positions.Take(6).ToArray(), mesh.vertices);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, mesh.triangles);
            Assert.That(MeshCleanupAnalysis.Analyze(mesh).Components.Count, Is.EqualTo(2));
        }

        [Test]
        public void LooseLinesAndPointsRequireOptInAndKeepSurfaceSupportedPrimitivesAndMaterialSlots()
        {
            var mesh = Track(new Mesh { name = "MixedPrimitives" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up,
                Vector3.one * 3, Vector3.one * 4, Vector3.one * 5, Vector3.one * 6 };
            mesh.subMeshCount = 4;
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
            mesh.SetIndices(new[] { 0, 1, 3, 4 }, MeshTopology.Lines, 1);
            mesh.SetIndices(new[] { 2, 5 }, MeshTopology.Points, 2);
            mesh.SetIndices(new[] { 0, 1, 3, 4 }, MeshTopology.LineStrip, 3);
            var analysis = MeshCleanupAnalysis.Analyze(mesh);
            Assert.That(analysis.LooseLineCount, Is.EqualTo(3));
            Assert.That(analysis.LoosePointCount, Is.EqualTo(1));
            var defaultResult = new CleanupMeshOperation().Apply(Context(mesh));
            Assert.That(defaultResult.RemovedVertexCount, Is.EqualTo(1));
            Assert.That(mesh.GetTopology(3), Is.EqualTo(MeshTopology.LineStrip));
            CollectionAssert.AreEqual(new[] { 0, 1, 3, 4 }, mesh.GetIndices(1));
            CollectionAssert.AreEqual(new[] { 2, 5 }, mesh.GetIndices(2));
            var result = new CleanupMeshOperation(removeLooseLines: true, removeLoosePoints: true).Apply(Context(mesh));
            Assert.That(result.RemovedLineCount, Is.EqualTo(3));
            Assert.That(result.RemovedPointCount, Is.EqualTo(1));
            Assert.That(result.RemovedVertexCount, Is.EqualTo(3));
            Assert.That(mesh.subMeshCount, Is.EqualTo(4));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, mesh.GetIndices(0));
            CollectionAssert.AreEqual(new[] { 0, 1 }, mesh.GetIndices(1));
            CollectionAssert.AreEqual(new[] { 2 }, mesh.GetIndices(2));
            Assert.That(mesh.GetTopology(3), Is.EqualTo(MeshTopology.Lines));
            CollectionAssert.AreEqual(new[] { 0, 1 }, mesh.GetIndices(3));
        }

        [Test]
        public void ComponentsAreDeterministicAndOnlyExplicitSelectionIsRemoved()
        {
            var mesh = Track(new Mesh { name = "TwoComponents" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up,
                Vector3.one * 5, Vector3.one * 5 + Vector3.right, Vector3.one * 5 + Vector3.up };
            mesh.subMeshCount = 2;
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
            mesh.SetIndices(new[] { 3, 4, 5 }, MeshTopology.Triangles, 1);
            var first = MeshCleanupAnalysis.Analyze(mesh);
            var second = MeshCleanupAnalysis.Analyze(mesh);
            CollectionAssert.AreEqual(first.Components.Select(c => c.Id), second.Components.Select(c => c.Id));
            CollectionAssert.AreEqual(new[] { 1 }, first.Components[1].SubMeshIndices);
            Assert.That(first.Components[1].VertexCount, Is.EqualTo(3));
            var before = MeshFingerprint.Compute(mesh);
            Assert.Throws<InvalidOperationException>(() => new CleanupMeshOperation(removeComponentIds: new[] { 2 }).Apply(Context(mesh)));
            Assert.That(MeshFingerprint.Compute(mesh), Is.EqualTo(before));
            var result = new CleanupMeshOperation(removeComponentIds: new[] { 0 }).Apply(Context(mesh));
            Assert.That(result.RemovedComponentCount, Is.EqualTo(1));
            Assert.That(result.RemovedFaceCount, Is.EqualTo(1));
            Assert.That(mesh.subMeshCount, Is.EqualTo(2));
            Assert.That(mesh.GetIndices(0), Is.Empty);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, mesh.GetIndices(1));
            Assert.That(mesh.vertices[0], Is.EqualTo(Vector3.one * 5));
        }

        [Test]
        public void QuadsRemainValidSurfacesAndAreOnlyRemovedByExplicitComponentSelection()
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up, Vector3.forward * 2 };
            mesh.SetIndices(new[] { 0, 1, 2, 3 }, MeshTopology.Quads, 0);
            new CleanupMeshOperation().Apply(Context(mesh));
            Assert.That(mesh.GetTopology(0), Is.EqualTo(MeshTopology.Quads));
            Assert.That(mesh.vertexCount, Is.EqualTo(4));
            Assert.That(MeshCleanupAnalysis.Analyze(mesh).Components.Single().FaceCount, Is.EqualTo(1));
        }

        [Test]
        public void ComponentIdsDoNotShiftWhenMinimumAreaChangesAfterDiagnosis()
        {
            var mesh = Track(new Mesh { name = "StableComponentIds" });
            mesh.vertices = new[]
            {
                Vector3.zero, new Vector3(1e-12f, 0, 0), new Vector3(0, 1e-12f, 0),
                Vector3.right * 10, Vector3.right * 11, Vector3.right * 10 + Vector3.up,
                Vector3.right * 20, Vector3.right * 21, Vector3.right * 20 + Vector3.up,
            };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            var exact = MeshCleanupAnalysis.Analyze(mesh);
            var threshold = MeshCleanupAnalysis.Analyze(mesh, 1e-6f);
            Assert.That(exact.Components.Count, Is.EqualTo(3));
            Assert.That(threshold.Components.Count, Is.EqualTo(3));
            CollectionAssert.AreEqual(exact.Components.Select(c => c.FirstFaceIndex), threshold.Components.Select(c => c.FirstFaceIndex));
            Assert.That(threshold.DegenerateFaceCount, Is.EqualTo(1));
            // Id 1 is the middle island in the original diagnosis. Raising the
            // cutoff removes the tiny first triangle, but must not retarget id 1.
            var result = new CleanupMeshOperation(1e-6f, removeComponentIds: new[] { 1 }).Apply(Context(mesh));
            Assert.That(result.RemovedFaceCount, Is.EqualTo(2));
            Assert.That(result.RemovedComponentCount, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { Vector3.right * 20, Vector3.right * 21, Vector3.right * 20 + Vector3.up }, mesh.vertices);
        }

        [Test]
        public void CleanupPreservesEverySurvivingConventionalChannelAndBlendShapeFrame()
        {
            var mesh = Track(new Mesh { name = "RichChannels" });
            mesh.vertices = new[] { Vector3.zero, Vector3.one * 99, Vector3.right, Vector3.up };
            mesh.normals = new[] { Vector3.forward, Vector3.right, Vector3.forward, Vector3.forward };
            mesh.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, -1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            mesh.colors32 = new[] { new Color32(1, 2, 3, 4), new Color32(7, 8, 9, 10), new Color32(11, 12, 13, 14), new Color32(21, 22, 23, 24) };
            for (int channel = 0; channel < 8; channel++)
                mesh.SetUVs(channel, new List<Vector4> { new Vector4(channel, 1, 2, 3), new Vector4(99, 98, 97, 96), new Vector4(channel, 4, 5, 6), new Vector4(channel, 7, 8, 9) });
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.up) };
            using (var counts = new NativeArray<byte>(new byte[] { 2, 1, 2, 1 }, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(new[] {
                new BoneWeight1 { boneIndex = 0, weight = .75f }, new BoneWeight1 { boneIndex = 1, weight = .25f },
                new BoneWeight1 { boneIndex = 1, weight = 1 },
                new BoneWeight1 { boneIndex = 1, weight = .6f }, new BoneWeight1 { boneIndex = 0, weight = .4f },
                new BoneWeight1 { boneIndex = 0, weight = 1 } }, Allocator.Temp)) mesh.SetBoneWeights(counts, weights);
            for (int frame = 1; frame <= 2; frame++)
                mesh.AddBlendShapeFrame("Shape", frame * 50, new[] { Vector3.up * frame, Vector3.one * 99, Vector3.right * frame, Vector3.forward * frame },
                    new[] { Vector3.right, Vector3.zero, Vector3.up, Vector3.forward }, new[] { Vector3.up, Vector3.zero, Vector3.forward, Vector3.right });
            mesh.triangles = new[] { 0, 2, 3 };
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            new CleanupMeshOperation().Apply(Context(mesh));
            var subset = new[] { 0, 2, 3 };
            CollectionAssert.AreEqual(subset.Select(i => snapshot.Positions[i]), mesh.vertices);
            CollectionAssert.AreEqual(subset.Select(i => snapshot.Normals[i]), mesh.normals);
            CollectionAssert.AreEqual(subset.Select(i => snapshot.Tangents[i]), mesh.tangents);
            CollectionAssert.AreEqual(subset.Select(i => snapshot.Colors32[i]), mesh.colors32);
            CollectionAssert.AreEqual(snapshot.BindPoses, mesh.bindposes);
            for (int channel = 0; channel < 8; channel++)
            {
                var uv = new List<Vector4>(); mesh.GetUVs(channel, uv);
                CollectionAssert.AreEqual(subset.Select(i => snapshot.Uvs[channel].Values[i]), uv);
            }
            using (var counts = mesh.GetBonesPerVertex()) CollectionAssert.AreEqual(new byte[] { 2, 2, 1 }, counts.ToArray());
            using (var weights = mesh.GetAllBoneWeights())
            {
                var expected = new List<BoneWeight1>();
                foreach (int vertex in subset) for (int j = snapshot.BoneWeightOffsets[vertex]; j < snapshot.BoneWeightOffsets[vertex + 1]; j++) expected.Add(snapshot.BoneWeights[j]);
                Assert.That(weights.Length, Is.EqualTo(expected.Count));
                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.That(weights[i].boneIndex, Is.EqualTo(expected[i].boneIndex));
                    Assert.That(weights[i].weight, Is.EqualTo(expected[i].weight).Within(1f / 65535f));
                }
            }
            Assert.That(mesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(mesh.GetBlendShapeName(0), Is.EqualTo("Shape"));
            Assert.That(mesh.GetBlendShapeFrameCount(0), Is.EqualTo(2));
            for (int frame = 0; frame < 2; frame++)
            {
                var p = new Vector3[3]; var n = new Vector3[3]; var t = new Vector3[3];
                mesh.GetBlendShapeFrameVertices(0, frame, p, n, t);
                var expected = snapshot.BlendShapes[0].Frames[frame];
                Assert.That(mesh.GetBlendShapeFrameWeight(0, frame), Is.EqualTo(expected.Weight));
                CollectionAssert.AreEqual(subset.Select(i => expected.DeltaVertices[i]), p);
                CollectionAssert.AreEqual(subset.Select(i => expected.DeltaNormals[i]), n);
                CollectionAssert.AreEqual(subset.Select(i => expected.DeltaTangents[i]), t);
            }
        }

        [Test]
        public void SnapshotAndCleanupPreserveRawUvOneDimensionFormatsAndStreams()
        {
            var mesh = Track(new Mesh { name = "RawLayout" });
            mesh.SetVertexBufferParams(4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 1, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 2, 1),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 2));
            mesh.SetVertexBufferData(new[] { Vector3.zero, Vector3.one * 9, Vector3.right, Vector3.up }, 0, 0, 4, 0);
            var uvBytes = new byte[32];
            for (int i = 0; i < 4; i++)
            {
                Array.Copy(BitConverter.GetBytes(i + .25f), 0, uvBytes, i * 8, 4);
                uvBytes[i * 8 + 4] = 0; uvBytes[i * 8 + 5] = 0x38; // Float16 .5
                uvBytes[i * 8 + 6] = 0; uvBytes[i * 8 + 7] = 0x3c; // Float16 1
            }
            mesh.SetVertexBufferData(uvBytes, 0, 0, uvBytes.Length, 1);
            mesh.SetVertexBufferData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, 0, 0, 16, 2);
            mesh.triangles = new[] { 0, 2, 3 };
            var descriptors = mesh.GetVertexAttributes();
            var before = RawStreams(mesh);
            var roundTrip = Track(MeshChannelSnapshot.Capture(mesh).CreateMesh("RoundTrip"));
            CollectionAssert.AreEqual(descriptors, roundTrip.GetVertexAttributes());
            AssertRawSubset(before, mesh, roundTrip, new[] { 0, 1, 2, 3 });
            new CleanupMeshOperation().Apply(Context(mesh));
            CollectionAssert.AreEqual(descriptors, mesh.GetVertexAttributes());
            Assert.That(mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0), Is.EqualTo(1));
            AssertRawSubset(before, roundTrip, mesh, new[] { 0, 2, 3 });
        }

        [Test]
        public void SnapshotRoundTripPreservesUInt16BaseVertexAndCleanupCompactsItSafely()
        {
            var mesh = Track(new Mesh { name = "BaseVertex", indexFormat = IndexFormat.UInt16 });
            var positions = new Vector3[65539];
            positions[65536] = Vector3.zero; positions[65537] = Vector3.right; positions[65538] = Vector3.up;
            mesh.vertices = positions;
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, true, 65536);
            var copy = Track(MeshChannelSnapshot.Capture(mesh).CreateMesh("BaseVertexCopy"));
            Assert.That(copy.indexFormat, Is.EqualTo(IndexFormat.UInt16));
            Assert.That(copy.GetBaseVertex(0), Is.EqualTo(65536));
            CollectionAssert.AreEqual(mesh.GetIndices(0, true), copy.GetIndices(0, true));
            new CleanupMeshOperation().Apply(Context(copy));
            Assert.That(copy.vertexCount, Is.EqualTo(3));
            CollectionAssert.AreEqual(new[] { Vector3.zero, Vector3.right, Vector3.up }, copy.vertices);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, copy.GetIndices(0, true));
        }

        [Test]
        public void WeldJsonDefaultsPreserveSeamsAndNegativeCleanupAreaIsRejectedWithoutMutation()
        {
            var parameters = JObject.Parse(WeldVerticesOperation.Deserialize("{\"tolerance\":0.001}").SerializeParameters());
            Assert.That((bool)parameters["preserveSeams"], Is.True);
            var mesh = Track(new Mesh { name = "InvalidThreshold" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var before = MeshFingerprint.Compute(mesh);
            Assert.Throws<InvalidOperationException>(() => new CleanupMeshOperation(-1).Apply(Context(mesh)));
            Assert.That(MeshFingerprint.Compute(mesh), Is.EqualTo(before));
        }

        private MeshOperationContext Context(Mesh mesh)
        {
            var document = Track(ScriptableObject.CreateInstance<MeshEditDocument>());
            document.Initialize(null, mesh, mesh);
            return new MeshOperationContext(document, mesh, document.Revision, default);
        }

        private T Track<T>(T value) where T : UnityEngine.Object { _objects.Add(value); return value; }

        private static byte[][] RawStreams(Mesh mesh)
        {
            var result = new byte[mesh.vertexBufferCount][];
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
                for (int stream = 0; stream < result.Length; stream++) result[stream] = data[0].GetVertexData<byte>(stream).ToArray();
            return result;
        }

        private static void AssertRawSubset(byte[][] original, Mesh originalLayout, Mesh result, int[] subset)
        {
            var after = RawStreams(result);
            Assert.That(after.Length, Is.EqualTo(original.Length));
            for (int stream = 0; stream < original.Length; stream++)
            {
                int stride = originalLayout.GetVertexBufferStride(stream);
                Assert.That(result.GetVertexBufferStride(stream), Is.EqualTo(stride));
                var expected = new byte[stride * subset.Length];
                for (int i = 0; i < subset.Length; i++) Buffer.BlockCopy(original[stream], subset[i] * stride, expected, i * stride, stride);
                CollectionAssert.AreEqual(expected, after[stream]);
            }
        }
    }
}
