using System;
using System.Collections.Generic;
using Unity.Collections;
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
            List<BlendShapeSnapshot> blendShapes)
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
        }

        public static MeshChannelSnapshot Capture(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var manifest = MeshChannelManifest.Capture(mesh);
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
                blendShapes);
        }

        public void ApplyVertexChannels(Mesh destination, IReadOnlyList<int> sourceVertexIndices)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (sourceVertexIndices == null) throw new ArgumentNullException(nameof(sourceVertexIndices));

            var vertices = Select(Positions, sourceVertexIndices);
            destination.SetVertices(vertices);
            if (Normals.Length == Positions.Length) destination.SetNormals(Select(Normals, sourceVertexIndices));
            if (Tangents.Length == Positions.Length) destination.SetTangents(Select(Tangents, sourceVertexIndices));
            if (ColorsAre32Bit && Colors32.Length == Positions.Length)
                destination.SetColors(Select(Colors32, sourceVertexIndices));
            else if (Colors.Length == Positions.Length)
                destination.SetColors(Select(Colors, sourceVertexIndices));

            for (int channel = 0; channel < Uvs.Length; channel++)
            {
                var uv = Uvs[channel];
                if (uv.Dimension <= 0 || uv.Values.Length != Positions.Length) continue;
                ApplyUv(destination, channel, uv.Dimension, uv.Values, sourceVertexIndices);
            }

            destination.bindposes = BindPoses != null ? (Matrix4x4[])BindPoses.Clone() : Array.Empty<Matrix4x4>();
            ApplyBoneWeights(destination, sourceVertexIndices);
            ApplyBlendShapes(destination, sourceVertexIndices);
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

        private static void ApplyUv(
            Mesh destination,
            int channel,
            int dimension,
            Vector4[] values,
            IReadOnlyList<int> sourceVertexIndices)
        {
            switch (dimension)
            {
                case 2:
                {
                    var selected = new List<Vector2>(sourceVertexIndices.Count);
                    for (int i = 0; i < sourceVertexIndices.Count; i++)
                    {
                        var value = values[sourceVertexIndices[i]];
                        selected.Add(new Vector2(value.x, value.y));
                    }
                    destination.SetUVs(channel, selected);
                    break;
                }
                case 3:
                {
                    var selected = new List<Vector3>(sourceVertexIndices.Count);
                    for (int i = 0; i < sourceVertexIndices.Count; i++)
                    {
                        var value = values[sourceVertexIndices[i]];
                        selected.Add(new Vector3(value.x, value.y, value.z));
                    }
                    destination.SetUVs(channel, selected);
                    break;
                }
                default:
                    destination.SetUVs(channel, Select(values, sourceVertexIndices));
                    break;
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
