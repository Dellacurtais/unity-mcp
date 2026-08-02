using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    internal sealed class SmartBodySplitCapBuildResult
    {
        public int LoopCount;
        public int TriangleCount;
        public readonly List<MeshEdgeKey> LocalCutEdges = new List<MeshEdgeKey>();
    }

    internal static class SmartBodySplitCapBuilder
    {
        private readonly struct EdgeUse
        {
            public readonly SemanticBodyPart Part;
            public readonly int From;
            public readonly int To;
            public readonly int SubMesh;

            public EdgeUse(SemanticBodyPart part, int from, int to, int subMesh)
            {
                Part = part;
                From = from;
                To = to;
                SubMesh = subMesh;
            }
        }

        private sealed class BoundaryEdge
        {
            public int From;
            public int To;
            public int SubMesh;
        }

        public static SmartBodySplitCapBuildResult AppendCaps(
            SmartBodySplitProposal proposal,
            SmartBodySplitPartProposal part,
            IReadOnlyDictionary<int, int> sourceToDestination,
            IReadOnlyList<Vector3> sourcePositions,
            IReadOnlyList<List<int>> subMeshIndices)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            if (part == null) throw new ArgumentNullException(nameof(part));
            var result = new SmartBodySplitCapBuildResult();
            var cutEdges = FindCutEdges(proposal, part.Part);
            if (cutEdges.Count == 0) return result;
            var loops = BuildLoops(cutEdges);
            for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
            {
                var loop = loops[loopIndex];
                if (!TryTriangulate(loop.vertices, sourcePositions, out var triangles, out var reason))
                    throw new InvalidOperationException(
                        $"SPLIT_CAP_TRIANGULATION_FAILED: part '{part.SlotId}', loop {loopIndex}: {reason}");
                int subMesh = Mathf.Clamp(loop.subMesh, 0, subMeshIndices.Count - 1);
                var output = subMeshIndices[subMesh];
                for (int triangle = 0; triangle < triangles.Count; triangle += 3)
                {
                    // Boundary edges follow the source face winding. The cap must use
                    // the opposite winding so every cut edge receives an opposing face.
                    output.Add(sourceToDestination[triangles[triangle]]);
                    output.Add(sourceToDestination[triangles[triangle + 2]]);
                    output.Add(sourceToDestination[triangles[triangle + 1]]);
                    result.TriangleCount++;
                }
                for (int edge = 0; edge < loop.vertices.Count; edge++)
                {
                    int next = (edge + 1) % loop.vertices.Count;
                    result.LocalCutEdges.Add(new MeshEdgeKey(
                        sourceToDestination[loop.vertices[edge]],
                        sourceToDestination[loop.vertices[next]]));
                }
                result.LoopCount++;
            }
            return result;
        }

        private static List<BoundaryEdge> FindCutEdges(SmartBodySplitProposal proposal, SemanticBodyPart targetPart)
        {
            var uses = new Dictionary<MeshEdgeKey, List<EdgeUse>>();
            for (int partIndex = 0; partIndex < proposal.Parts.Count; partIndex++)
            {
                var part = proposal.Parts[partIndex];
                for (int triangleIndex = 0; triangleIndex < part.Triangles.Count; triangleIndex++)
                {
                    var triangle = part.Triangles[triangleIndex];
                    AddUse(uses, part.Part, triangle.A, triangle.B, triangle.SubMesh);
                    AddUse(uses, part.Part, triangle.B, triangle.C, triangle.SubMesh);
                    AddUse(uses, part.Part, triangle.C, triangle.A, triangle.SubMesh);
                }
            }

            var result = new List<BoundaryEdge>();
            foreach (var pair in uses)
            {
                bool hasTarget = false, hasOther = false;
                EdgeUse target = default;
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (pair.Value[i].Part == targetPart)
                    {
                        if (!hasTarget) target = pair.Value[i];
                        hasTarget = true;
                    }
                    else hasOther = true;
                }
                if (!hasTarget || !hasOther) continue;
                result.Add(new BoundaryEdge { From = target.From, To = target.To, SubMesh = target.SubMesh });
            }
            result.Sort((left, right) =>
            {
                int compare = Math.Min(left.From, left.To).CompareTo(Math.Min(right.From, right.To));
                return compare != 0 ? compare : Math.Max(left.From, left.To).CompareTo(Math.Max(right.From, right.To));
            });
            return result;
        }

        private static void AddUse(
            IDictionary<MeshEdgeKey, List<EdgeUse>> uses,
            SemanticBodyPart part,
            int from,
            int to,
            int subMesh)
        {
            var key = new MeshEdgeKey(from, to);
            if (!uses.TryGetValue(key, out var list)) uses.Add(key, list = new List<EdgeUse>(2));
            list.Add(new EdgeUse(part, from, to, subMesh));
        }

        private static List<(List<int> vertices, int subMesh)> BuildLoops(IReadOnlyList<BoundaryEdge> edges)
        {
            var incident = new Dictionary<int, List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                AddIncident(incident, edges[i].From, i);
                AddIncident(incident, edges[i].To, i);
            }
            foreach (var pair in incident)
                if (pair.Value.Count != 2)
                    throw new InvalidOperationException(
                        $"SPLIT_CAP_BOUNDARY_AMBIGUOUS: cut vertex {pair.Key} has degree {pair.Value.Count}; refine the split boundary before closing it.");

            var used = new bool[edges.Count];
            var loops = new List<(List<int> vertices, int subMesh)>();
            for (int startEdge = 0; startEdge < edges.Count; startEdge++)
            {
                if (used[startEdge]) continue;
                var first = edges[startEdge];
                var vertices = new List<int> { first.From };
                var subMeshVotes = new Dictionary<int, int>();
                int currentVertex = first.To;
                int currentEdge = startEdge;
                int guard = 0;
                while (guard++ <= edges.Count)
                {
                    if (used[currentEdge])
                        throw new InvalidOperationException("SPLIT_CAP_BOUNDARY_REPEATED: cut loop reused an edge.");
                    used[currentEdge] = true;
                    Vote(subMeshVotes, edges[currentEdge].SubMesh);
                    if (currentVertex == vertices[0]) break;
                    vertices.Add(currentVertex);
                    var candidates = incident[currentVertex];
                    int nextEdge = candidates[0] == currentEdge ? candidates[1] : candidates[0];
                    if (used[nextEdge])
                        throw new InvalidOperationException("SPLIT_CAP_BOUNDARY_OPEN: cut boundary did not form a closed loop.");
                    var next = edges[nextEdge];
                    currentVertex = next.From == currentVertex ? next.To : next.From;
                    currentEdge = nextEdge;
                }
                if (currentVertex != vertices[0] || vertices.Count < 3)
                    throw new InvalidOperationException("SPLIT_CAP_BOUNDARY_OPEN: cut boundary did not form a valid closed loop.");
                loops.Add((vertices, MostFrequent(subMeshVotes)));
            }
            return loops;
        }

        private static void AddIncident(IDictionary<int, List<int>> incident, int vertex, int edge)
        {
            if (!incident.TryGetValue(vertex, out var list)) incident.Add(vertex, list = new List<int>(2));
            list.Add(edge);
        }

        private static void Vote(IDictionary<int, int> votes, int subMesh)
        {
            votes.TryGetValue(subMesh, out int count);
            votes[subMesh] = count + 1;
        }

        private static int MostFrequent(IReadOnlyDictionary<int, int> votes)
        {
            int best = 0, bestCount = -1;
            foreach (var pair in votes)
                if (pair.Value > bestCount || pair.Value == bestCount && pair.Key < best)
                {
                    best = pair.Key;
                    bestCount = pair.Value;
                }
            return best;
        }

        private static bool TryTriangulate(
            IReadOnlyList<int> loop,
            IReadOnlyList<Vector3> positions,
            out List<int> triangles,
            out string reason)
        {
            triangles = new List<int>();
            reason = null;
            if (loop == null || loop.Count < 3) { reason = "loop contains fewer than three vertices"; return false; }
            var unique = new HashSet<int>();
            for (int i = 0; i < loop.Count; i++)
                if (!unique.Add(loop[i])) { reason = $"vertex {loop[i]} occurs more than once"; return false; }

            var normal = Vector3.zero;
            for (int i = 0; i < loop.Count; i++)
            {
                var current = positions[loop[i]];
                var next = positions[loop[(i + 1) % loop.Count]];
                normal.x += (current.y - next.y) * (current.z + next.z);
                normal.y += (current.z - next.z) * (current.x + next.x);
                normal.z += (current.x - next.x) * (current.y + next.y);
            }
            if (normal.sqrMagnitude < 1e-14f) { reason = "loop is degenerate or nearly collinear"; return false; }
            int dropAxis = Mathf.Abs(normal.x) >= Mathf.Abs(normal.y) && Mathf.Abs(normal.x) >= Mathf.Abs(normal.z)
                ? 0
                : Mathf.Abs(normal.y) >= Mathf.Abs(normal.z) ? 1 : 2;
            var projected = new Vector2[loop.Count];
            for (int i = 0; i < loop.Count; i++) projected[i] = Project(positions[loop[i]], dropAxis);
            float area = SignedArea(projected);
            if (Mathf.Abs(area) < 1e-10f) { reason = "projected loop has zero area"; return false; }
            float orientation = Mathf.Sign(area);
            var remaining = new List<int>(loop.Count);
            for (int i = 0; i < loop.Count; i++) remaining.Add(i);
            int guard = 0;
            while (remaining.Count > 3 && guard++ < loop.Count * loop.Count)
            {
                bool clipped = false;
                for (int cursor = 0; cursor < remaining.Count; cursor++)
                {
                    int previous = remaining[(cursor - 1 + remaining.Count) % remaining.Count];
                    int current = remaining[cursor];
                    int next = remaining[(cursor + 1) % remaining.Count];
                    if (orientation * Cross(projected[previous], projected[current], projected[next]) <= 1e-9f) continue;
                    bool contains = false;
                    for (int test = 0; test < remaining.Count; test++)
                    {
                        int candidate = remaining[test];
                        if (candidate == previous || candidate == current || candidate == next) continue;
                        if (PointInTriangle(projected[candidate], projected[previous], projected[current], projected[next], orientation))
                        { contains = true; break; }
                    }
                    if (contains) continue;
                    triangles.Add(loop[previous]);
                    triangles.Add(loop[current]);
                    triangles.Add(loop[next]);
                    remaining.RemoveAt(cursor);
                    clipped = true;
                    break;
                }
                if (!clipped) { reason = "ear clipping could not resolve a simple polygon"; return false; }
            }
            if (remaining.Count != 3) { reason = "triangulation ended with an invalid polygon"; return false; }
            triangles.Add(loop[remaining[0]]);
            triangles.Add(loop[remaining[1]]);
            triangles.Add(loop[remaining[2]]);
            return true;
        }

        private static Vector2 Project(Vector3 value, int dropAxis)
            => dropAxis == 0 ? new Vector2(value.y, value.z)
                : dropAxis == 1 ? new Vector2(value.x, value.z)
                : new Vector2(value.x, value.y);

        private static float SignedArea(IReadOnlyList<Vector2> points)
        {
            double area = 0d;
            for (int i = 0; i < points.Count; i++)
            {
                var next = points[(i + 1) % points.Count];
                area += points[i].x * next.y - next.x * points[i].y;
            }
            return (float)(area * 0.5d);
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        private static bool PointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c, float orientation)
        {
            const float epsilon = -1e-8f;
            return orientation * Cross(a, b, point) >= epsilon
                && orientation * Cross(b, c, point) >= epsilon
                && orientation * Cross(c, a, point) >= epsilon;
        }
    }
}
