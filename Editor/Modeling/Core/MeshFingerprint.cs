using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    public static class MeshFingerprint
    {
        public static string Compute(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(mesh.name ?? string.Empty);
                writer.Write(mesh.vertexCount);
                writer.Write(mesh.subMeshCount);
                writer.Write((int)mesh.indexFormat);
                writer.Write(snapshot.Manifest.VertexAttributes.Count);
                foreach (var attribute in snapshot.Manifest.VertexAttributes)
                {
                    writer.Write((int)attribute.attribute);
                    writer.Write((int)attribute.format);
                    writer.Write(attribute.dimension);
                    writer.Write(attribute.stream);
                }
                writer.Write(snapshot.VertexStreams.Count);
                for (int streamIndex = 0; streamIndex < snapshot.VertexStreams.Count; streamIndex++)
                {
                    writer.Write(snapshot.VertexStrides[streamIndex]);
                    writer.Write(snapshot.VertexStreams[streamIndex].Length);
                    writer.Write(snapshot.VertexStreams[streamIndex]);
                }
                Write(writer, snapshot.Bounds.center);
                Write(writer, snapshot.Bounds.size);
                Write(writer, snapshot.Positions);
                Write(writer, snapshot.Normals);
                Write(writer, snapshot.Tangents);
                writer.Write(snapshot.ColorsAre32Bit);
                if (snapshot.ColorsAre32Bit)
                {
                    writer.Write(snapshot.Colors32.Length);
                    for (int i = 0; i < snapshot.Colors32.Length; i++)
                    {
                        writer.Write(snapshot.Colors32[i].r);
                        writer.Write(snapshot.Colors32[i].g);
                        writer.Write(snapshot.Colors32[i].b);
                        writer.Write(snapshot.Colors32[i].a);
                    }
                }
                else
                {
                    writer.Write(snapshot.Colors.Length);
                    for (int i = 0; i < snapshot.Colors.Length; i++)
                    {
                        writer.Write(snapshot.Colors[i].r);
                        writer.Write(snapshot.Colors[i].g);
                        writer.Write(snapshot.Colors[i].b);
                        writer.Write(snapshot.Colors[i].a);
                    }
                }
                for (int channel = 0; channel < snapshot.Uvs.Length; channel++)
                {
                    writer.Write(snapshot.Uvs[channel].Dimension);
                    Write(writer, snapshot.Uvs[channel].Values);
                }
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    writer.Write((int)mesh.GetTopology(submesh));
                    writer.Write(mesh.GetBaseVertex(submesh));
                    var indices = mesh.GetIndices(submesh, true);
                    writer.Write(indices.Length);
                    for (int i = 0; i < indices.Length; i++) writer.Write(indices[i]);
                }
                writer.Write(snapshot.BonesPerVertex.Length);
                for (int i = 0; i < snapshot.BonesPerVertex.Length; i++) writer.Write(snapshot.BonesPerVertex[i]);
                writer.Write(snapshot.BoneWeights.Length);
                for (int i = 0; i < snapshot.BoneWeights.Length; i++)
                {
                    writer.Write(snapshot.BoneWeights[i].boneIndex);
                    writer.Write(snapshot.BoneWeights[i].weight);
                }
                writer.Write(snapshot.BindPoses.Length);
                for (int i = 0; i < snapshot.BindPoses.Length; i++) Write(writer, snapshot.BindPoses[i]);
                writer.Write(snapshot.BlendShapes.Count);
                for (int shapeIndex = 0; shapeIndex < snapshot.BlendShapes.Count; shapeIndex++)
                {
                    var shape = snapshot.BlendShapes[shapeIndex];
                    writer.Write(shape.Name);
                    writer.Write(shape.Frames.Count);
                    for (int frameIndex = 0; frameIndex < shape.Frames.Count; frameIndex++)
                    {
                        var frame = shape.Frames[frameIndex];
                        writer.Write(frame.Weight);
                        Write(writer, frame.DeltaVertices);
                        Write(writer, frame.DeltaNormals);
                        Write(writer, frame.DeltaTangents);
                    }
                }
                writer.Flush();
                stream.Position = 0;
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void Write(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static void Write(BinaryWriter writer, Vector3[] values)
        {
            writer.Write(values?.Length ?? 0);
            if (values == null) return;
            for (int i = 0; i < values.Length; i++) Write(writer, values[i]);
        }

        private static void Write(BinaryWriter writer, Vector4[] values)
        {
            writer.Write(values?.Length ?? 0);
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                writer.Write(values[i].x);
                writer.Write(values[i].y);
                writer.Write(values[i].z);
                writer.Write(values[i].w);
            }
        }

        private static void Write(BinaryWriter writer, Matrix4x4 value)
        {
            for (int i = 0; i < 16; i++) writer.Write(value[i]);
        }
    }
}
