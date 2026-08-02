using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    internal sealed class SplitPartBuildData
    {
        public SmartBodySplitPartProposal Proposal;
        public Mesh Mesh;
        public string AssetPath;
        public List<int> SourceVertexIndices;
    }

    public static class SplitFidelityValidator
    {
        internal static MeshValidationReport Validate(
            SmartBodySplitProposal proposal,
            MeshChannelSnapshot source,
            IReadOnlyList<SplitPartBuildData> parts)
        {
            var report = new MeshValidationReport();
            if (proposal == null || source == null || parts == null)
            {
                report.Error("NULL_INPUT", "Split validation received null input.");
                return report;
            }

            int sourceTriangleCount = 0;
            for (int submesh = 0; submesh < proposal.SourceMesh.subMeshCount; submesh++)
                sourceTriangleCount += (int)proposal.SourceMesh.GetIndexCount(submesh) / 3;
            int resultTriangleCount = 0;

            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];
                if (part?.Mesh == null)
                {
                    report.Error("MISSING_PART_MESH", $"Part index {partIndex} has no mesh.");
                    continue;
                }
                if (part.SourceVertexIndices == null || part.Mesh.vertexCount != part.SourceVertexIndices.Count)
                {
                    report.Error("VERTEX_MAP_MISMATCH", $"Part '{part.Mesh.name}' has an invalid source vertex map.");
                    continue;
                }
                if (part.Mesh.subMeshCount != proposal.SourceMesh.subMeshCount)
                    report.Error("SUBMESH_COUNT_MISMATCH", $"Part '{part.Mesh.name}' changed the submesh count.");

                for (int submesh = 0; submesh < part.Mesh.subMeshCount; submesh++)
                    resultTriangleCount += (int)part.Mesh.GetIndexCount(submesh) / 3;

                ValidateVertexChannels(source, part, report);
                ValidateSkinning(source, part, report);
                ValidateBlendShapes(source, part, report);
            }

            if (resultTriangleCount != sourceTriangleCount)
                report.Error(
                    "TRIANGLE_COVERAGE_MISMATCH",
                    $"Split contains {resultTriangleCount} triangles but source contains {sourceTriangleCount}.");
            if (proposal.SourceTriangleCount != sourceTriangleCount)
                report.Error(
                    "PROPOSAL_COVERAGE_MISMATCH",
                    $"Proposal assigned {proposal.SourceTriangleCount} of {sourceTriangleCount} source triangles.");
            return report;
        }

        private static void ValidateVertexChannels(
            MeshChannelSnapshot source,
            SplitPartBuildData part,
            MeshValidationReport report)
        {
            var vertices = part.Mesh.vertices;
            var normals = source.Manifest.HasNormals ? part.Mesh.normals : Array.Empty<Vector3>();
            var tangents = source.Manifest.HasTangents ? part.Mesh.tangents : Array.Empty<Vector4>();
            var colors32 = source.ColorsAre32Bit ? part.Mesh.colors32 : Array.Empty<Color32>();
            var colors = !source.ColorsAre32Bit && source.Colors.Length > 0 ? part.Mesh.colors : Array.Empty<Color>();
            for (int destinationIndex = 0; destinationIndex < part.SourceVertexIndices.Count; destinationIndex++)
            {
                int sourceIndex = part.SourceVertexIndices[destinationIndex];
                if (sourceIndex < 0 || sourceIndex >= source.Positions.Length)
                {
                    report.Error("SOURCE_VERTEX_OUT_OF_RANGE", $"Part '{part.Mesh.name}' maps to source vertex {sourceIndex}.");
                    continue;
                }
                if (!vertices[destinationIndex].Equals(source.Positions[sourceIndex]))
                    report.Error("POSITION_CHANGED", $"Part '{part.Mesh.name}' changed source vertex {sourceIndex}.");
                if (source.Normals.Length == source.Positions.Length
                    && (normals.Length != vertices.Length || !normals[destinationIndex].Equals(source.Normals[sourceIndex])))
                    report.Error("NORMAL_CHANGED", $"Part '{part.Mesh.name}' changed normal {sourceIndex}.");
                if (source.Tangents.Length == source.Positions.Length
                    && (tangents.Length != vertices.Length || !tangents[destinationIndex].Equals(source.Tangents[sourceIndex])))
                    report.Error("TANGENT_CHANGED", $"Part '{part.Mesh.name}' changed tangent {sourceIndex}.");
                if (source.ColorsAre32Bit && source.Colors32.Length == source.Positions.Length
                    && (colors32.Length != vertices.Length || !colors32[destinationIndex].Equals(source.Colors32[sourceIndex])))
                    report.Error("COLOR_CHANGED", $"Part '{part.Mesh.name}' changed color {sourceIndex}.");
                if (!source.ColorsAre32Bit && source.Colors.Length == source.Positions.Length
                    && (colors.Length != vertices.Length || !colors[destinationIndex].Equals(source.Colors[sourceIndex])))
                    report.Error("COLOR_CHANGED", $"Part '{part.Mesh.name}' changed color {sourceIndex}.");
            }

            for (int channel = 0; channel < 8; channel++)
            {
                var sourceUv = source.Uvs[channel];
                if (sourceUv.Dimension <= 0) continue;
                var destinationUv = new List<Vector4>();
                part.Mesh.GetUVs(channel, destinationUv);
                if (destinationUv.Count != vertices.Length)
                {
                    report.Error("UV_COUNT_MISMATCH", $"Part '{part.Mesh.name}' UV{channel} count does not match vertices.");
                    continue;
                }
                for (int destinationIndex = 0; destinationIndex < destinationUv.Count; destinationIndex++)
                {
                    var expected = sourceUv.Values[part.SourceVertexIndices[destinationIndex]];
                    var actual = destinationUv[destinationIndex];
                    if (!UvEquals(actual, expected, sourceUv.Dimension))
                    {
                        report.Error("UV_CHANGED", $"Part '{part.Mesh.name}' changed UV{channel} at vertex {destinationIndex}.");
                        break;
                    }
                }
            }
        }

        private static void ValidateSkinning(
            MeshChannelSnapshot source,
            SplitPartBuildData part,
            MeshValidationReport report)
        {
            if (source.BonesPerVertex.Length == 0) return;
            var destination = MeshChannelSnapshot.Capture(part.Mesh);
            if (destination.BindPoses.Length != source.BindPoses.Length)
                report.Error("BINDPOSE_COUNT_MISMATCH", $"Part '{part.Mesh.name}' changed bind pose count.");
            else
                for (int i = 0; i < source.BindPoses.Length; i++)
                    if (!destination.BindPoses[i].Equals(source.BindPoses[i]))
                        report.Error("BINDPOSE_CHANGED", $"Part '{part.Mesh.name}' changed bind pose {i}.");

            for (int destinationVertex = 0; destinationVertex < part.SourceVertexIndices.Count; destinationVertex++)
            {
                int sourceVertex = part.SourceVertexIndices[destinationVertex];
                int sourceStart = source.BoneWeightOffsets[sourceVertex];
                int sourceCount = source.BoneWeightOffsets[sourceVertex + 1] - sourceStart;
                int destinationStart = destination.BoneWeightOffsets[destinationVertex];
                int destinationCount = destination.BoneWeightOffsets[destinationVertex + 1] - destinationStart;
                if (sourceCount != destinationCount)
                {
                    report.Error("BONE_WEIGHT_COUNT_MISMATCH", $"Part '{part.Mesh.name}' changed influences for source vertex {sourceVertex}.");
                    continue;
                }
                for (int i = 0; i < sourceCount; i++)
                {
                    var expected = source.BoneWeights[sourceStart + i];
                    var actual = destination.BoneWeights[destinationStart + i];
                    if (expected.boneIndex != actual.boneIndex || !expected.weight.Equals(actual.weight))
                    {
                        report.Error("BONE_WEIGHT_CHANGED", $"Part '{part.Mesh.name}' changed weight {i} for source vertex {sourceVertex}.");
                        break;
                    }
                }
            }
        }

        private static void ValidateBlendShapes(
            MeshChannelSnapshot source,
            SplitPartBuildData part,
            MeshValidationReport report)
        {
            if (part.Mesh.blendShapeCount != source.BlendShapes.Count)
            {
                report.Error("BLEND_SHAPE_COUNT_MISMATCH", $"Part '{part.Mesh.name}' changed blend shape count.");
                return;
            }
            for (int shapeIndex = 0; shapeIndex < source.BlendShapes.Count; shapeIndex++)
            {
                var shape = source.BlendShapes[shapeIndex];
                if (!string.Equals(part.Mesh.GetBlendShapeName(shapeIndex), shape.Name, StringComparison.Ordinal))
                    report.Error("BLEND_SHAPE_NAME_MISMATCH", $"Part '{part.Mesh.name}' changed blend shape {shapeIndex} name.");
                if (part.Mesh.GetBlendShapeFrameCount(shapeIndex) != shape.Frames.Count)
                {
                    report.Error("BLEND_SHAPE_FRAME_COUNT_MISMATCH", $"Part '{part.Mesh.name}' changed frame count for '{shape.Name}'.");
                    continue;
                }
                for (int frameIndex = 0; frameIndex < shape.Frames.Count; frameIndex++)
                {
                    var actualVertices = new Vector3[part.Mesh.vertexCount];
                    var actualNormals = new Vector3[part.Mesh.vertexCount];
                    var actualTangents = new Vector3[part.Mesh.vertexCount];
                    part.Mesh.GetBlendShapeFrameVertices(
                        shapeIndex,
                        frameIndex,
                        actualVertices,
                        actualNormals,
                        actualTangents);
                    for (int destinationVertex = 0; destinationVertex < part.SourceVertexIndices.Count; destinationVertex++)
                    {
                        int sourceVertex = part.SourceVertexIndices[destinationVertex];
                        var frame = shape.Frames[frameIndex];
                        if (!actualVertices[destinationVertex].Equals(frame.DeltaVertices[sourceVertex])
                            || !actualNormals[destinationVertex].Equals(frame.DeltaNormals[sourceVertex])
                            || !actualTangents[destinationVertex].Equals(frame.DeltaTangents[sourceVertex]))
                        {
                            report.Error("BLEND_SHAPE_DELTA_CHANGED", $"Part '{part.Mesh.name}' changed '{shape.Name}' frame {frameIndex}.");
                            break;
                        }
                    }
                }
            }
        }

        private static bool UvEquals(Vector4 actual, Vector4 expected, int dimension)
        {
            if (!actual.x.Equals(expected.x) || !actual.y.Equals(expected.y)) return false;
            if (dimension >= 3 && !actual.z.Equals(expected.z)) return false;
            return dimension < 4 || actual.w.Equals(expected.w);
        }
    }
}
