using System;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Validation
{
    public static class MeshValidator
    {
        public static MeshValidationReport Validate(Mesh mesh)
        {
            var report = new MeshValidationReport();
            if (mesh == null)
            {
                report.Error("MESH_NULL", "Mesh is null.");
                return report;
            }

            Vector3[] vertices;
            try { vertices = mesh.vertices; }
            catch (Exception exception)
            {
                report.Error("MESH_NOT_READABLE", exception.Message);
                return report;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                if (!Finite(vertices[i]))
                {
                    report.Error("VERTEX_NOT_FINITE", $"Vertex {i} contains NaN or Infinity.");
                    break;
                }
            }

            ValidateChannel(report, "NORMAL_COUNT", mesh.normals.Length, vertices.Length);
            ValidateChannel(report, "TANGENT_COUNT", mesh.tangents.Length, vertices.Length);
            ValidateChannel(report, "COLOR_COUNT", mesh.colors.Length, vertices.Length);
            ValidateChannel(report, "COLOR32_COUNT", mesh.colors32.Length, vertices.Length);
            for (int channel = 0; channel < 8; channel++)
            {
                var values = new System.Collections.Generic.List<Vector4>();
                mesh.GetUVs(channel, values);
                ValidateChannel(report, $"UV{channel}_COUNT", values.Count, vertices.Length);
            }

            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                var indices = mesh.GetIndices(subMesh, true);
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] < 0 || indices[i] >= vertices.Length)
                    {
                        report.Error("INDEX_OUT_OF_RANGE", $"Submesh {subMesh} index {i} references vertex {indices[i]} of {vertices.Length}.");
                        break;
                    }
                }
                if (mesh.GetTopology(subMesh) == MeshTopology.Triangles && indices.Length % 3 != 0)
                    report.Error("TRIANGLE_INDEX_COUNT", $"Submesh {subMesh} has {indices.Length} triangle indices.");
            }

            try
            {
                using (var counts = mesh.GetBonesPerVertex())
                using (var weights = mesh.GetAllBoneWeights())
                {
                    if (counts.Length > 0 && counts.Length != vertices.Length)
                        report.Error("BONE_VERTEX_COUNT", $"Bone count channel has {counts.Length} entries for {vertices.Length} vertices.");
                    int weightIndex = 0;
                    for (int vertex = 0; vertex < counts.Length; vertex++)
                    {
                        float sum = 0f;
                        for (int influence = 0; influence < counts[vertex]; influence++, weightIndex++)
                        {
                            if (weightIndex >= weights.Length)
                            {
                                report.Error("BONE_WEIGHT_COUNT", "Bone weight stream ended before bones-per-vertex stream.");
                                break;
                            }
                            var weight = weights[weightIndex];
                            if (!Finite(weight.weight) || weight.weight < 0f)
                                report.Error("BONE_WEIGHT_INVALID", $"Vertex {vertex} has invalid bone weight {weight.weight}.");
                            if (weight.boneIndex < 0 || (mesh.bindposes.Length > 0 && weight.boneIndex >= mesh.bindposes.Length))
                                report.Error("BONE_INDEX_INVALID", $"Vertex {vertex} references bone {weight.boneIndex}.");
                            sum += weight.weight;
                        }
                        if (counts[vertex] > 0 && Mathf.Abs(sum - 1f) > 0.002f)
                            report.Error("BONE_WEIGHT_SUM", $"Vertex {vertex} bone weights sum to {sum:R}.");
                    }
                    if (weightIndex != weights.Length)
                        report.Error("BONE_WEIGHT_TRAILING", $"Bone weight stream contains {weights.Length - weightIndex} trailing entries.");
                }
            }
            catch (Exception exception)
            {
                report.Error("BONE_CHANNEL_READ_FAILED", exception.Message);
            }

            var bounds = mesh.bounds;
            if (!Finite(bounds.center) || !Finite(bounds.size))
                report.Error("BOUNDS_NOT_FINITE", "Mesh bounds contain NaN or Infinity.");
            return report;
        }

        private static void ValidateChannel(MeshValidationReport report, string code, int count, int vertexCount)
        {
            if (count != 0 && count != vertexCount)
                report.Error(code, $"Channel contains {count} values for {vertexCount} vertices.");
        }

        private static bool Finite(Vector3 value)
            => Finite(value.x) && Finite(value.y) && Finite(value.z);

        private static bool Finite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
