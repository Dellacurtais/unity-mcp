using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    public sealed class SmartBodySplitBoundaryRefinementResult
    {
        public int ReassignedTriangleCount { get; internal set; }
        public string PreviewHash { get; internal set; }
        public SemanticBodyPart TargetPart { get; internal set; }
    }

    public static class SmartBodySplitBoundaryRefiner
    {
        public static SmartBodySplitBoundaryRefinementResult Reassign(
            SmartBodySplitProposal proposal,
            int subMesh,
            int triangleIndex,
            SemanticBodyPart targetPart,
            int growRings = 0)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            if (proposal.SourceMesh == null) throw new InvalidOperationException("SOURCE_MISSING: proposal has no source mesh.");
            if (growRings < 0 || growRings > 64) throw new ArgumentOutOfRangeException(nameof(growRings));

            var topology = HalfEdgeTopology.Build(proposal.SourceMesh);
            var bySource = new Dictionary<long, int>();
            for (int faceIndex = 0; faceIndex < topology.Faces.Count; faceIndex++)
            {
                var face = topology.Faces[faceIndex];
                bySource[Key(face.SubMesh, face.TriangleIndex)] = faceIndex;
            }
            if (!bySource.TryGetValue(Key(subMesh, triangleIndex), out var seed))
                throw new InvalidOperationException($"TRIANGLE_NOT_FOUND: triangle {subMesh}:{triangleIndex} does not exist.");

            var selected = new HashSet<int> { seed };
            var frontier = new HashSet<int> { seed };
            for (int ring = 0; ring < growRings; ring++)
            {
                var next = new HashSet<int>();
                foreach (int faceIndex in frontier)
                {
                    var neighbors = topology.Faces[faceIndex].Neighbors;
                    for (int neighborIndex = 0; neighborIndex < neighbors.Length; neighborIndex++)
                    {
                        int neighbor = neighbors[neighborIndex];
                        if (selected.Add(neighbor)) next.Add(neighbor);
                    }
                }
                frontier = next;
                if (frontier.Count == 0) break;
            }

            int reassigned = 0;
            foreach (int faceIndex in selected)
            {
                var face = topology.Faces[faceIndex];
                if (!proposal.TryReassignTriangle(face.SubMesh, face.TriangleIndex, targetPart, out var error))
                    throw new InvalidOperationException("BOUNDARY_REFINEMENT_FAILED: " + error);
                reassigned++;
            }
            return new SmartBodySplitBoundaryRefinementResult
            {
                ReassignedTriangleCount = reassigned,
                PreviewHash = proposal.PreviewHash,
                TargetPart = targetPart,
            };
        }

        public static IReadOnlyList<SourceTriangle> LowConfidenceTriangles(
            SmartBodySplitProposal proposal,
            float threshold,
            int maximum = 256)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            var result = new List<SourceTriangle>();
            for (int partIndex = 0; partIndex < proposal.Parts.Count; partIndex++)
                for (int triangleIndex = 0; triangleIndex < proposal.Parts[partIndex].Triangles.Count; triangleIndex++)
                {
                    var triangle = proposal.Parts[partIndex].Triangles[triangleIndex];
                    if (triangle.Confidence <= threshold) result.Add(triangle);
                }
            result.Sort((left, right) => left.Confidence.CompareTo(right.Confidence));
            if (result.Count > maximum) result.RemoveRange(maximum, result.Count - maximum);
            return result;
        }

        private static long Key(int subMesh, int triangleIndex)
            => ((long)subMesh << 32) | (uint)triangleIndex;
    }
}
