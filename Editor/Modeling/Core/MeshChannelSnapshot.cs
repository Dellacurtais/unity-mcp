using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    public sealed class MeshUvSnapshot
    {
        public int Dimension { get; }
        public Vector4[] Values { get; }

        public MeshUvSnapshot(int dimension, Vector4[] values)
        {
            Dimension = dimension;
            Values = values ?? Array.Empty<Vector4>();
        }
    }

    public sealed class BlendShapeFrameSnapshot
    {
        public float Weight { get; }
        public Vector3[] DeltaVertices { get; }
        public Vector3[] DeltaNormals { get; }
        public Vector3[] DeltaTangents { get; }

        public BlendShapeFrameSnapshot(
            float weight,
            Vector3[] deltaVertices,
            Vector3[] deltaNormals,
            Vector3[] deltaTangents)
        {
            Weight = weight;
            DeltaVertices = deltaVertices;
            DeltaNormals = deltaNormals;
            DeltaTangents = deltaTangents;
        }
    }

    public sealed class BlendShapeSnapshot
    {
        public string Name { get; }
        public IReadOnlyList<BlendShapeFrameSnapshot> Frames { get; }

        public BlendShapeSnapshot(string name, List<BlendShapeFrameSnapshot> frames)
        {
            Name = name ?? string.Empty;
            Frames = frames ?? new List<BlendShapeFrameSnapshot>();
        }
    }

    public sealed class MeshSubMeshSnapshot
    {
        public MeshTopology Topology { get; }
        public int[] Indices { get; }
        public int BaseVertex { get; }

        public MeshSubMeshSnapshot(MeshTopology topology, int[] indices, int baseVertex = 0)
        {
            Topology = topology;
            Indices = indices ?? Array.Empty<int>();
            BaseVertex = baseVertex;
        }
    }

    public sealed class MeshChannelSnapshot
    {
        public MeshChannelManifest Manifest { get; }
        public Vector3[] Positions { get; }
        public Vector3[] Normals { get; }
        public Vector4[] Tangents { get; }
        public Color[] Colors { get; }
        public Color32[] Colors32 { get; }
        public bool ColorsAre32Bit { get; }
        public MeshUvSnapshot[] Uvs { get; }
        public byte[] BonesPerVertex { get; }
        public BoneWeight1[] BoneWeights { get; }
        public int[] BoneWeightOffsets { get; }
        public Matrix4x4[] BindPoses { get; }
        public IReadOnlyList<BlendShapeSnapshot> BlendShapes { get; }
        public IReadOnlyList<MeshSubMeshSnapshot> SubMeshes { get; }
        public IndexFormat IndexFormat { get; }
        public Bounds Bounds { get; }
        private readonly byte[][] _vertexStreams;
        private readonly int[] _vertexStrides;
        internal IReadOnlyList<byte[]> VertexStreams => _vertexStreams;
        internal IReadOnlyList<int> VertexStrides => _vertexStrides;

        private MeshChannelSnapshot(
            MeshChannelManifest manifest,
            Vector3[] positions,
            Vector3[] normals,
            Vector4[] tangents,
            Color[] colors,
            Color32[] colors32,
            bool colorsAre32Bit,
            MeshUvSnapshot[] uvs,
            byte[] bonesPerVertex,
            BoneWeight1[] boneWeights,
            int[] boneWeightOffsets,
            Matrix4x4[] bindPoses,
            List<BlendShapeSnapshot> blendShapes,
            List<MeshSubMeshSnapshot> subMeshes,
            IndexFormat indexFormat,
            Bounds bounds,
            byte[][] vertexStreams,
            int[] vertexStrides)
        {
            Manifest = manifest;
            Positions = positions;
            Normals = normals;
            Tangents = tangents;
            Colors = colors;
            Colors32 = colors32;
            ColorsAre32Bit = colorsAre32Bit;
            Uvs = uvs;
            BonesPerVertex = bonesPerVertex;
            BoneWeights = boneWeights;
            BoneWeightOffsets = boneWeightOffsets;
            BindPoses = bindPoses;
            BlendShapes = blendShapes;
            SubMeshes = subMeshes;
            IndexFormat = indexFormat;
            Bounds = bounds;
            _vertexStreams = vertexStreams;
            _vertexStrides = vertexStrides;
        }

        public static MeshChannelSnapshot Capture(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var manifest = MeshChannelManifest.Capture(mesh);
            var vertexStreams = new byte[mesh.vertexBufferCount][];
            var vertexStrides = new int[mesh.vertexBufferCount];
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                for (int stream = 0; stream < vertexStreams.Length; stream++)
                {
                    vertexStrides[stream] = mesh.GetVertexBufferStride(stream);
                    vertexStreams[stream] = data[0].GetVertexData<byte>(stream).ToArray();
                    if (vertexStreams[stream].Length != checked(mesh.vertexCount * vertexStrides[stream]))
                        throw new InvalidOperationException("MESH_CHANNEL_LAYOUT_UNSUPPORTED: vertex stream size does not match its stride.");
                }
            }
            var positions = mesh.vertices;
            var normals = manifest.HasNormals ? mesh.normals : Array.Empty<Vector3>();
            var tangents = manifest.HasTangents ? mesh.tangents : Array.Empty<Vector4>();

            var colorsAre32Bit = manifest.HasColors
                && mesh.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8;
            var colors = manifest.HasColors && !colorsAre32Bit ? mesh.colors : Array.Empty<Color>();
            var colors32 = manifest.HasColors && colorsAre32Bit ? mesh.colors32 : Array.Empty<Color32>();

            var uvs = new MeshUvSnapshot[8];
            for (int channel = 0; channel < uvs.Length; channel++)
            {
                int dimension = manifest.GetUvDimension(channel);
                if (dimension <= 0)
                {
                    uvs[channel] = new MeshUvSnapshot(0, Array.Empty<Vector4>());
                    continue;
                }
                var values = new List<Vector4>(mesh.vertexCount);
                mesh.GetUVs(channel, values);
                uvs[channel] = new MeshUvSnapshot(dimension, values.ToArray());
            }

            byte[] bonesPerVertex;
            BoneWeight1[] boneWeights;
            if (manifest.HasBoneWeights)
            {
                using (var counts = mesh.GetBonesPerVertex()) bonesPerVertex = counts.ToArray();
                using (var weights = mesh.GetAllBoneWeights()) boneWeights = weights.ToArray();
            }
            else
            {
                bonesPerVertex = Array.Empty<byte>();
                boneWeights = Array.Empty<BoneWeight1>();
            }

            var offsets = new int[mesh.vertexCount + 1];
            for (int i = 0; i < mesh.vertexCount; i++)
                offsets[i + 1] = offsets[i] + (i < bonesPerVertex.Length ? bonesPerVertex[i] : 0);

            var blendShapes = new List<BlendShapeSnapshot>(mesh.blendShapeCount);
            for (int shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
            {
                var frames = new List<BlendShapeFrameSnapshot>(mesh.GetBlendShapeFrameCount(shapeIndex));
                for (int frameIndex = 0; frameIndex < mesh.GetBlendShapeFrameCount(shapeIndex); frameIndex++)
                {
                    var deltaVertices = new Vector3[mesh.vertexCount];
                    var deltaNormals = new Vector3[mesh.vertexCount];
                    var deltaTangents = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(
                        shapeIndex,
                        frameIndex,
                        deltaVertices,
                        deltaNormals,
                        deltaTangents);
                    frames.Add(new BlendShapeFrameSnapshot(
                        mesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex),
                        deltaVertices,
                        deltaNormals,
                        deltaTangents));
                }
                blendShapes.Add(new BlendShapeSnapshot(mesh.GetBlendShapeName(shapeIndex), frames));
            }

            var subMeshes = new List<MeshSubMeshSnapshot>(mesh.subMeshCount);
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                subMeshes.Add(new MeshSubMeshSnapshot(
                    mesh.GetTopology(subMesh),
                    mesh.GetIndices(subMesh, true), checked((int)mesh.GetBaseVertex(subMesh))));

            return new MeshChannelSnapshot(
                manifest,
                positions,
                normals,
                tangents,
                colors,
                colors32,
                colorsAre32Bit,
                uvs,
                bonesPerVertex,
                boneWeights,
                offsets,
                mesh.bindposes,
                blendShapes,
                subMeshes,
                mesh.indexFormat,
                mesh.bounds,
                vertexStreams,
                vertexStrides);
        }

        public Mesh CreateMesh(string name = null)
        {
            var allVertices = new int[Positions.Length];
            for (int i = 0; i < allVertices.Length; i++) allVertices[i] = i;
            var mesh = CreateVertexSubset(string.IsNullOrWhiteSpace(name) ? "EditableMesh" : name, allVertices, IndexFormat);
            try
            {
                mesh.subMeshCount = SubMeshes.Count;
                for (int subMesh = 0; subMesh < SubMeshes.Count; subMesh++)
                {
                    var source = SubMeshes[subMesh];
                    var local = new int[source.Indices.Length];
                    for (int i = 0; i < local.Length; i++) local[i] = checked(source.Indices[i] - source.BaseVertex);
                    mesh.SetIndices(local, source.Topology, subMesh, false, source.BaseVertex);
                }
                mesh.bounds = Bounds;
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }

        public void ApplyAll(Mesh destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            var prepared = CreateMesh(destination.name);
            try { EditorUtility.CopySerialized(prepared, destination); }
            finally { UnityEngine.Object.DestroyImmediate(prepared); }
        }

        public void ApplyVertexChannels(Mesh destination, IReadOnlyList<int> sourceVertexIndices)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (sourceVertexIndices == null) throw new ArgumentNullException(nameof(sourceVertexIndices));

            var prepared = CreateVertexSubset(destination.name, sourceVertexIndices, destination.indexFormat);
            try { EditorUtility.CopySerialized(prepared, destination); }
            finally { UnityEngine.Object.DestroyImmediate(prepared); }
        }

        // Build off to the side: unsupported skin/layout combinations fail before
        // changing an asset or the caller's working mesh. No typed setter widens UV1,
        // Float16 or normalized attributes, or silently repacks vertex streams.
        internal Mesh CreateVertexSubset(string name, IReadOnlyList<int> sourceVertexIndices, IndexFormat format)
        {
            if (sourceVertexIndices == null) throw new ArgumentNullException(nameof(sourceVertexIndices));
            for (int i = 0; i < sourceVertexIndices.Count; i++) CheckedSourceIndex(sourceVertexIndices[i]);
            var attributes = new VertexAttributeDescriptor[Manifest.VertexAttributes.Count];
            for (int i = 0; i < attributes.Length; i++) attributes[i] = Manifest.VertexAttributes[i];
            var result = new Mesh { name = name, indexFormat = format };
            try
            {
                if (attributes.Length > 0) result.SetVertexBufferParams(sourceVertexIndices.Count, attributes);
                result.bindposes = BindPoses != null ? (Matrix4x4[])BindPoses.Clone() : Array.Empty<Matrix4x4>();
                ApplyBoneWeights(result, sourceVertexIndices);
                var restored = result.GetVertexAttributes();
                if (restored.Length != attributes.Length)
                    throw new InvalidOperationException("MESH_CHANNEL_LAYOUT_UNSUPPORTED: restoring skin weights changed the vertex layout.");
                for (int i = 0; i < attributes.Length; i++)
                    if (!restored[i].Equals(attributes[i]))
                        throw new InvalidOperationException("MESH_CHANNEL_LAYOUT_UNSUPPORTED: restoring skin weights changed an attribute format or stream.");
                for (int stream = 0; stream < _vertexStreams.Length; stream++)
                {
                    int stride = _vertexStrides[stream];
                    if (result.GetVertexBufferStride(stream) != stride)
                        throw new InvalidOperationException("MESH_CHANNEL_LAYOUT_UNSUPPORTED: restored stream stride differs.");
                    var filtered = new byte[checked(sourceVertexIndices.Count * stride)];
                    for (int vertex = 0; vertex < sourceVertexIndices.Count; vertex++)
                        Buffer.BlockCopy(_vertexStreams[stream], checked(sourceVertexIndices[vertex] * stride), filtered, checked(vertex * stride), stride);
                    if (filtered.Length > 0)
                        result.SetVertexBufferData(filtered, 0, 0, filtered.Length, stream, MeshUpdateFlags.DontRecalculateBounds);
                }
                ApplyBlendShapes(result, sourceVertexIndices);
                result.bounds = Bounds;
                return result;
            }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }

        private void ApplyBoneWeights(Mesh destination, IReadOnlyList<int> sourceVertexIndices)
        {
            if (BonesPerVertex.Length != Positions.Length) return;
            var counts = new byte[sourceVertexIndices.Count];
            var weights = new List<BoneWeight1>();
            for (int destinationIndex = 0; destinationIndex < sourceVertexIndices.Count; destinationIndex++)
            {
                int sourceIndex = CheckedSourceIndex(sourceVertexIndices[destinationIndex]);
                int start = BoneWeightOffsets[sourceIndex];
                int count = BoneWeightOffsets[sourceIndex + 1] - start;
                if (count > byte.MaxValue) throw new InvalidOperationException("A vertex has more than 255 bone weights.");
                counts[destinationIndex] = (byte)count;
                for (int i = 0; i < count; i++) weights.Add(BoneWeights[start + i]);
            }
            using (var nativeCounts = new NativeArray<byte>(counts, Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                destination.SetBoneWeights(nativeCounts, nativeWeights);
        }

        private void ApplyBlendShapes(Mesh destination, IReadOnlyList<int> sourceVertexIndices)
        {
            for (int shapeIndex = 0; shapeIndex < BlendShapes.Count; shapeIndex++)
            {
                var shape = BlendShapes[shapeIndex];
                for (int frameIndex = 0; frameIndex < shape.Frames.Count; frameIndex++)
                {
                    var frame = shape.Frames[frameIndex];
                    destination.AddBlendShapeFrame(
                        shape.Name,
                        frame.Weight,
                        Select(frame.DeltaVertices, sourceVertexIndices).ToArray(),
                        Select(frame.DeltaNormals, sourceVertexIndices).ToArray(),
                        Select(frame.DeltaTangents, sourceVertexIndices).ToArray());
                }
            }
        }

        private int CheckedSourceIndex(int sourceIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= Positions.Length)
                throw new ArgumentOutOfRangeException(nameof(sourceIndex), sourceIndex, "Source vertex index is outside the captured mesh.");
            return sourceIndex;
        }

        private static List<T> Select<T>(T[] source, IReadOnlyList<int> sourceVertexIndices)
        {
            var selected = new List<T>(sourceVertexIndices.Count);
            for (int i = 0; i < sourceVertexIndices.Count; i++)
            {
                int index = sourceVertexIndices[i];
                if (index < 0 || index >= source.Length)
                    throw new ArgumentOutOfRangeException(nameof(sourceVertexIndices), index, "Source vertex index is outside the captured channel.");
                selected.Add(source[index]);
            }
            return selected;
        }
    }
}
