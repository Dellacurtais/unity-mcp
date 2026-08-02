using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    public readonly struct MeshVertexSource
    {
        public int[] Indices { get; }
        public float[] Weights { get; }

        public MeshVertexSource(int[] indices, float[] weights)
        {
            if (indices == null || weights == null || indices.Length == 0 || indices.Length != weights.Length)
                throw new ArgumentException("Vertex source requires matching non-empty indices and weights.");
            Indices = indices;
            Weights = weights;
        }

        public static MeshVertexSource Copy(int index)
            => new MeshVertexSource(new[] { index }, new[] { 1f });

        public static MeshVertexSource Midpoint(int a, int b)
            => new MeshVertexSource(new[] { a, b }, new[] { 0.5f, 0.5f });

        public static MeshVertexSource TriangleCenter(int a, int b, int c)
            => new MeshVertexSource(new[] { a, b, c }, new[] { 1f / 3f, 1f / 3f, 1f / 3f });
    }

    public static class MeshChannelTransfer
    {
        public static void ApplyInterpolated(
            MeshChannelSnapshot source,
            Mesh destination,
            IReadOnlyList<MeshVertexSource> vertices)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));

            destination.SetVertices(Interpolate(source.Positions, vertices, Vector3.zero));
            if (source.Normals.Length == source.Positions.Length)
            {
                var normals = new List<Vector3>(vertices.Count);
                for (int i = 0; i < vertices.Count; i++)
                {
                    if (TryGetExactCopy(vertices[i], out int sourceIndex))
                    {
                        normals.Add(source.Normals[sourceIndex]);
                        continue;
                    }
                    var value = Weighted(source.Normals, vertices[i], Vector3.zero);
                    normals.Add(value.sqrMagnitude > 1e-12f ? value.normalized : Vector3.up);
                }
                destination.SetNormals(normals);
            }
            if (source.Tangents.Length == source.Positions.Length)
            {
                var tangents = new List<Vector4>(vertices.Count);
                for (int i = 0; i < vertices.Count; i++)
                {
                    if (TryGetExactCopy(vertices[i], out int sourceIndex))
                    {
                        tangents.Add(source.Tangents[sourceIndex]);
                        continue;
                    }
                    var value = Weighted(source.Tangents, vertices[i], Vector4.zero);
                    var xyz = new Vector3(value.x, value.y, value.z);
                    if (xyz.sqrMagnitude > 1e-12f) xyz.Normalize();
                    tangents.Add(new Vector4(xyz.x, xyz.y, xyz.z, value.w < 0f ? -1f : 1f));
                }
                destination.SetTangents(tangents);
            }

            if (source.ColorsAre32Bit && source.Colors32.Length == source.Positions.Length)
            {
                var colors = new List<Color32>(vertices.Count);
                for (int i = 0; i < vertices.Count; i++)
                {
                    var color = WeightedColor32(source.Colors32, vertices[i]);
                    colors.Add(color);
                }
                destination.SetColors(colors);
            }
            else if (source.Colors.Length == source.Positions.Length)
            {
                var colors = new List<Color>(vertices.Count);
                for (int i = 0; i < vertices.Count; i++) colors.Add(Weighted(source.Colors, vertices[i], Color.clear));
                destination.SetColors(colors);
            }

            for (int channel = 0; channel < source.Uvs.Length; channel++)
            {
                var uv = source.Uvs[channel];
                if (uv.Dimension <= 0 || uv.Values.Length != source.Positions.Length) continue;
                if (uv.Dimension == 2)
                {
                    var values = new List<Vector2>(vertices.Count);
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        var value = Weighted(uv.Values, vertices[i], Vector4.zero);
                        values.Add(new Vector2(value.x, value.y));
                    }
                    destination.SetUVs(channel, values);
                }
                else if (uv.Dimension == 3)
                {
                    var values = new List<Vector3>(vertices.Count);
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        var value = Weighted(uv.Values, vertices[i], Vector4.zero);
                        values.Add(new Vector3(value.x, value.y, value.z));
                    }
                    destination.SetUVs(channel, values);
                }
                else
                {
                    var values = new List<Vector4>(vertices.Count);
                    for (int i = 0; i < vertices.Count; i++) values.Add(Weighted(uv.Values, vertices[i], Vector4.zero));
                    destination.SetUVs(channel, values);
                }
            }

            destination.bindposes = source.BindPoses != null
                ? (Matrix4x4[])source.BindPoses.Clone()
                : Array.Empty<Matrix4x4>();
            ApplyBoneWeights(source, destination, vertices);
            ApplyBlendShapes(source, destination, vertices);
        }

        private static void ApplyBoneWeights(
            MeshChannelSnapshot source,
            Mesh destination,
            IReadOnlyList<MeshVertexSource> vertices)
        {
            if (source.BonesPerVertex.Length != source.Positions.Length) return;
            var counts = new byte[vertices.Count];
            var output = new List<BoneWeight1>();
            for (int destinationVertex = 0; destinationVertex < vertices.Count; destinationVertex++)
            {
                var combined = new Dictionary<int, float>();
                var vertexSource = vertices[destinationVertex];
                for (int contributor = 0; contributor < vertexSource.Indices.Length; contributor++)
                {
                    int sourceVertex = CheckedIndex(vertexSource.Indices[contributor], source.Positions.Length);
                    float vertexWeight = vertexSource.Weights[contributor];
                    int start = source.BoneWeightOffsets[sourceVertex];
                    int end = source.BoneWeightOffsets[sourceVertex + 1];
                    for (int weightIndex = start; weightIndex < end; weightIndex++)
                    {
                        var weight = source.BoneWeights[weightIndex];
                        combined.TryGetValue(weight.boneIndex, out var existing);
                        combined[weight.boneIndex] = existing + Mathf.Max(0f, vertexWeight) * weight.weight;
                    }
                }

                var sorted = new List<KeyValuePair<int, float>>(combined);
                sorted.Sort((left, right) => right.Value.CompareTo(left.Value));
                if (sorted.Count > byte.MaxValue) sorted.RemoveRange(byte.MaxValue, sorted.Count - byte.MaxValue);
                float sum = 0f;
                for (int i = 0; i < sorted.Count; i++) sum += sorted[i].Value;
                counts[destinationVertex] = (byte)sorted.Count;
                for (int i = 0; i < sorted.Count; i++)
                    output.Add(new BoneWeight1
                    {
                        boneIndex = sorted[i].Key,
                        weight = sum > 1e-12f ? sorted[i].Value / sum : 0f,
                    });
            }
            using (var nativeCounts = new NativeArray<byte>(counts, Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(output.ToArray(), Allocator.Temp))
                destination.SetBoneWeights(nativeCounts, nativeWeights);
        }

        private static void ApplyBlendShapes(
            MeshChannelSnapshot source,
            Mesh destination,
            IReadOnlyList<MeshVertexSource> vertices)
        {
            for (int shapeIndex = 0; shapeIndex < source.BlendShapes.Count; shapeIndex++)
            {
                var shape = source.BlendShapes[shapeIndex];
                for (int frameIndex = 0; frameIndex < shape.Frames.Count; frameIndex++)
                {
                    var frame = shape.Frames[frameIndex];
                    destination.AddBlendShapeFrame(
                        shape.Name,
                        frame.Weight,
                        Interpolate(frame.DeltaVertices, vertices, Vector3.zero).ToArray(),
                        Interpolate(frame.DeltaNormals, vertices, Vector3.zero).ToArray(),
                        Interpolate(frame.DeltaTangents, vertices, Vector3.zero).ToArray());
                }
            }
        }

        private static List<Vector3> Interpolate(
            Vector3[] source,
            IReadOnlyList<MeshVertexSource> vertices,
            Vector3 fallback)
        {
            var result = new List<Vector3>(vertices.Count);
            for (int i = 0; i < vertices.Count; i++) result.Add(Weighted(source, vertices[i], fallback));
            return result;
        }

        private static Vector3 Weighted(Vector3[] source, MeshVertexSource vertex, Vector3 fallback)
        {
            if (source == null || source.Length == 0) return fallback;
            var value = Vector3.zero;
            float sum = 0f;
            for (int i = 0; i < vertex.Indices.Length; i++)
            {
                int index = CheckedIndex(vertex.Indices[i], source.Length);
                value += source[index] * vertex.Weights[i];
                sum += vertex.Weights[i];
            }
            return Mathf.Abs(sum) > 1e-12f ? value / sum : fallback;
        }

        private static Vector4 Weighted(Vector4[] source, MeshVertexSource vertex, Vector4 fallback)
        {
            if (source == null || source.Length == 0) return fallback;
            var value = Vector4.zero;
            float sum = 0f;
            for (int i = 0; i < vertex.Indices.Length; i++)
            {
                int index = CheckedIndex(vertex.Indices[i], source.Length);
                value += source[index] * vertex.Weights[i];
                sum += vertex.Weights[i];
            }
            return Mathf.Abs(sum) > 1e-12f ? value / sum : fallback;
        }

        private static Color Weighted(Color[] source, MeshVertexSource vertex, Color fallback)
        {
            if (source == null || source.Length == 0) return fallback;
            var value = Color.clear;
            float sum = 0f;
            for (int i = 0; i < vertex.Indices.Length; i++)
            {
                int index = CheckedIndex(vertex.Indices[i], source.Length);
                value += source[index] * vertex.Weights[i];
                sum += vertex.Weights[i];
            }
            return Mathf.Abs(sum) > 1e-12f ? value / sum : fallback;
        }

        private static Color32 WeightedColor32(Color32[] source, MeshVertexSource vertex)
        {
            var value = Color.clear;
            float sum = 0f;
            for (int i = 0; i < vertex.Indices.Length; i++)
            {
                int index = CheckedIndex(vertex.Indices[i], source.Length);
                value += (Color)source[index] * vertex.Weights[i];
                sum += vertex.Weights[i];
            }
            return (Color32)(Mathf.Abs(sum) > 1e-12f ? value / sum : Color.clear);
        }

        private static int CheckedIndex(int index, int length)
        {
            if (index < 0 || index >= length)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Vertex source index is outside the captured mesh.");
            return index;
        }

        private static bool TryGetExactCopy(MeshVertexSource source, out int index)
        {
            if (source.Indices.Length == 1
                && source.Weights.Length == 1
                && source.Weights[0].Equals(1f))
            {
                index = source.Indices[0];
                return true;
            }
            index = -1;
            return false;
        }
    }
}
