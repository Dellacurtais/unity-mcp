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
            public readonly int CanonicalFrom;
            public readonly int CanonicalTo;
            public readonly int SubMesh;

            public EdgeUse(
                SemanticBodyPart part,
                int from,
                int to,
                int canonicalFrom,
                int canonicalTo,
                int subMesh)
            {
                Part = part;
                From = from;
                To = to;
                CanonicalFrom = canonicalFrom;
                CanonicalTo = canonicalTo;
                SubMesh = subMesh;
            }
        }

        private sealed class BoundaryEdge
        {
            public int From;
            public int To;
            public int CanonicalFrom;
            public int CanonicalTo;
            public int SubMesh;
        }

        private sealed class BoundaryLoop
        {
            public readonly List<BoundaryEdge> Edges = new List<BoundaryEdge>();
            public int SubMesh;
            public bool HasVertexSeam;
        }

        private readonly struct ContinuationCandidate
        {
            public readonly int Incoming;
            public readonly int Outgoing;
            public readonly bool ExactVertex;
            public readonly bool SameSubMesh;
            public readonly float DirectionScore;

            public ContinuationCandidate(
                int incoming,
                int outgoing,
                bool exactVertex,
                bool sameSubMesh,
                float directionScore)
            {
                Incoming = incoming;
                Outgoing = outgoing;
                ExactVertex = exactVertex;
                SameSubMesh = sameSubMesh;
                DirectionScore = directionScore;
            }
        }

        private readonly struct SpatialCell : IEquatable<SpatialCell>
        {
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            public SpatialCell(int x, int y, int z)
            {
                _x = x;
                _y = y;
                _z = z;
            }

            public bool Equals(SpatialCell other) => _x == other._x && _y == other._y && _z == other._z;
            public override bool Equals(object obj) => obj is SpatialCell other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _x;
                    hash = hash * 397 ^ _y;
                    return hash * 397 ^ _z;
                }
            }
        }

        public static SmartBodySplitCapBuildResult AppendCaps(
            SmartBodySplitProposal proposal,
            SmartBodySplitPartProposal part,
            IReadOnlyDictionary<int, int> sourceToDestination,
            IReadOnlyList<Vector3> sourcePositions,
            IReadOnlyList<List<int>> subMeshIndices,
            List<MeshVertexSource> vertexSources,
            List<int> sourceVertexIndices)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            if (part == null) throw new ArgumentNullException(nameof(part));
            if (sourcePositions == null) throw new ArgumentNullException(nameof(sourcePositions));
            if (vertexSources == null) throw new ArgumentNullException(nameof(vertexSources));
            if (sourceVertexIndices == null) throw new ArgumentNullException(nameof(sourceVertexIndices));
            var result = new SmartBodySplitCapBuildResult();
            var canonicalVertices = BuildCanonicalVertices(sourcePositions);
            var cutEdges = FindCutEdges(proposal, part.Part, canonicalVertices);
            if (cutEdges.Count == 0) return result;
            var loops = BuildLoops(cutEdges, sourcePositions);
            for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
            {
                var loop = loops[loopIndex];
                int subMesh = Mathf.Clamp(loop.SubMesh, 0, subMeshIndices.Count - 1);
                var output = subMeshIndices[subMesh];
                if (loop.HasVertexSeam)
                {
                    int center = AppendInterpolatedCenter(loop, vertexSources, sourceVertexIndices);
                    for (int edgeIndex = 0; edgeIndex < loop.Edges.Count; edgeIndex++)
                    {
                        var edge = loop.Edges[edgeIndex];
                        // The target face uses From -> To. From -> center -> To gives
                        // the cap the opposing To -> From boundary edge.
                        output.Add(sourceToDestination[edge.From]);
                        output.Add(center);
                        output.Add(sourceToDestination[edge.To]);
                        result.TriangleCount++;
                    }
                }
                else
                {
                    var vertices = new List<int>(loop.Edges.Count);
                    for (int edgeIndex = 0; edgeIndex < loop.Edges.Count; edgeIndex++)
                        vertices.Add(loop.Edges[edgeIndex].From);
                    if (!TryTriangulate(vertices, sourcePositions, out var triangles, out var reason))
                        throw new InvalidOperationException(
                            $"SPLIT_CAP_TRIANGULATION_FAILED: part '{part.SlotId}', loop {loopIndex}: {reason}");
                    for (int triangle = 0; triangle < triangles.Count; triangle += 3)
                    {
                        // Boundary edges follow the source face winding. The cap must use
                        // the opposite winding so every cut edge receives an opposing face.
                        output.Add(sourceToDestination[triangles[triangle]]);
                        output.Add(sourceToDestination[triangles[triangle + 2]]);
                        output.Add(sourceToDestination[triangles[triangle + 1]]);
                        result.TriangleCount++;
                    }
                }
                for (int edgeIndex = 0; edgeIndex < loop.Edges.Count; edgeIndex++)
                {
                    var edge = loop.Edges[edgeIndex];
                    result.LocalCutEdges.Add(new MeshEdgeKey(
                        sourceToDestination[edge.From],
                        sourceToDestination[edge.To]));
                }
                result.LoopCount++;
            }
            return result;
        }

        private static List<BoundaryEdge> FindCutEdges(
            SmartBodySplitProposal proposal,
            SemanticBodyPart targetPart,
            IReadOnlyList<int> canonicalVertices)
        {
            var uses = new Dictionary<MeshEdgeKey, List<EdgeUse>>();
            for (int partIndex = 0; partIndex < proposal.Parts.Count; partIndex++)
            {
                var part = proposal.Parts[partIndex];
                for (int triangleIndex = 0; triangleIndex < part.Triangles.Count; triangleIndex++)
                {
                    var triangle = part.Triangles[triangleIndex];
                    AddUse(uses, part.Part, triangle.A, triangle.B, triangle.SubMesh, canonicalVertices);
                    AddUse(uses, part.Part, triangle.B, triangle.C, triangle.SubMesh, canonicalVertices);
                    AddUse(uses, part.Part, triangle.C, triangle.A, triangle.SubMesh, canonicalVertices);
                }
            }

            var result = new List<BoundaryEdge>();
            var added = new HashSet<MeshEdgeKey>();
            foreach (var pair in uses)
            {
                bool hasOther = false;
                for (int i = 0; i < pair.Value.Count; i++)
                    if (pair.Value[i].Part != targetPart) { hasOther = true; break; }
                if (!hasOther) continue;
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    var target = pair.Value[i];
                    if (target.Part != targetPart || !added.Add(new MeshEdgeKey(target.From, target.To))) continue;
                    result.Add(new BoundaryEdge
                    {
                        From = target.From,
                        To = target.To,
                        CanonicalFrom = target.CanonicalFrom,
                        CanonicalTo = target.CanonicalTo,
                        SubMesh = target.SubMesh,
                    });
                }
            }
            result.Sort((left, right) =>
            {
                int compare = Math.Min(left.CanonicalFrom, left.CanonicalTo)
                    .CompareTo(Math.Min(right.CanonicalFrom, right.CanonicalTo));
                if (compare != 0) return compare;
                compare = Math.Max(left.CanonicalFrom, left.CanonicalTo)
                    .CompareTo(Math.Max(right.CanonicalFrom, right.CanonicalTo));
                if (compare != 0) return compare;
                compare = Math.Min(left.From, left.To).CompareTo(Math.Min(right.From, right.To));
                return compare != 0 ? compare : Math.Max(left.From, left.To).CompareTo(Math.Max(right.From, right.To));
            });
            return result;
        }

        private static void AddUse(
            IDictionary<MeshEdgeKey, List<EdgeUse>> uses,
            SemanticBodyPart part,
            int from,
            int to,
            int subMesh,
            IReadOnlyList<int> canonicalVertices)
        {
            int canonicalFrom = canonicalVertices[from];
            int canonicalTo = canonicalVertices[to];
            if (canonicalFrom == canonicalTo) return;
            var key = new MeshEdgeKey(canonicalFrom, canonicalTo);
            if (!uses.TryGetValue(key, out var list)) uses.Add(key, list = new List<EdgeUse>(2));
            list.Add(new EdgeUse(part, from, to, canonicalFrom, canonicalTo, subMesh));
        }

        private static List<BoundaryLoop> BuildLoops(
            IReadOnlyList<BoundaryEdge> edges,
            IReadOnlyList<Vector3> sourcePositions)
        {
            var incoming = new Dictionary<int, List<int>>();
            var outgoing = new Dictionary<int, List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                AddIncident(outgoing, edges[i].CanonicalFrom, i);
                AddIncident(incoming, edges[i].CanonicalTo, i);
            }
            var continuation = new int[edges.Count];
            for (int i = 0; i < continuation.Length; i++) continuation[i] = -1;
            var vertices = new SortedSet<int>();
            foreach (int vertex in incoming.Keys) vertices.Add(vertex);
            foreach (int vertex in outgoing.Keys) vertices.Add(vertex);
            foreach (int vertex in vertices)
            {
                incoming.TryGetValue(vertex, out var incomingEdges);
                outgoing.TryGetValue(vertex, out var outgoingEdges);
                int incomingCount = incomingEdges?.Count ?? 0;
                int outgoingCount = outgoingEdges?.Count ?? 0;
                if (incomingCount != outgoingCount || incomingCount == 0)
                    throw new InvalidOperationException(
                        $"SPLIT_CAP_BOUNDARY_ORIENTATION_AMBIGUOUS: cut vertex {vertex} has {incomingCount} incoming and {outgoingCount} outgoing edges; refine the split boundary before closing it.");
                PairContinuations(vertex, incomingEdges, outgoingEdges, edges, sourcePositions, continuation);
            }

            var used = new bool[edges.Count];
            var loops = new List<BoundaryLoop>();
            for (int startEdge = 0; startEdge < edges.Count; startEdge++)
            {
                if (used[startEdge]) continue;
                var loop = new BoundaryLoop();
                var subMeshVotes = new Dictionary<int, int>();
                int currentEdge = startEdge;
                var visitedVertices = new HashSet<int>();
                int guard = 0;
                while (guard++ <= edges.Count)
                {
                    if (used[currentEdge])
                        throw new InvalidOperationException("SPLIT_CAP_BOUNDARY_REPEATED: cut loop reused an edge.");
                    used[currentEdge] = true;
                    var source = edges[currentEdge];
                    if (!visitedVertices.Add(source.CanonicalFrom)) loop.HasVertexSeam = true;
                    loop.Edges.Add(source);
                    Vote(subMeshVotes, source.SubMesh);
                    int nextEdge = continuation[currentEdge];
                    if (nextEdge < 0)
                        throw new InvalidOperationException("SPLIT_CAP_BOUNDARY_OPEN: cut boundary has no directed continuation.");
                    if (nextEdge == startEdge) break;
                    if (used[nextEdge])
                        throw new InvalidOperationException("SPLIT_CAP_BOUNDARY_OPEN: cut boundary did not form a closed loop.");
                    currentEdge = nextEdge;
                }
                if (continuation[currentEdge] != startEdge || loop.Edges.Count < 3)
                    throw new InvalidOperationException("SPLIT_CAP_BOUNDARY_OPEN: cut boundary did not form a valid closed loop.");
                for (int edgeIndex = 0; edgeIndex < loop.Edges.Count; edgeIndex++)
                {
                    var current = loop.Edges[edgeIndex];
                    var next = loop.Edges[(edgeIndex + 1) % loop.Edges.Count];
                    if (current.To != next.From)
                    {
                        loop.HasVertexSeam = true;
                        break;
                    }
                }
                loop.SubMesh = MostFrequent(subMeshVotes);
                loops.Add(loop);
            }
            return loops;
        }

        private static void PairContinuations(
            int canonicalVertex,
            IReadOnlyList<int> incoming,
            IReadOnlyList<int> outgoing,
            IReadOnlyList<BoundaryEdge> edges,
            IReadOnlyList<Vector3> sourcePositions,
            int[] continuation)
        {
            var candidates = new List<ContinuationCandidate>(incoming.Count * outgoing.Count);
            for (int incomingIndex = 0; incomingIndex < incoming.Count; incomingIndex++)
            {
                var previous = edges[incoming[incomingIndex]];
                var incomingDirection = sourcePositions[previous.To] - sourcePositions[previous.From];
                for (int outgoingIndex = 0; outgoingIndex < outgoing.Count; outgoingIndex++)
                {
                    var next = edges[outgoing[outgoingIndex]];
                    var outgoingDirection = sourcePositions[next.To] - sourcePositions[next.From];
                    float directionScore = incomingDirection.sqrMagnitude > 1e-14f && outgoingDirection.sqrMagnitude > 1e-14f
                        ? Vector3.Dot(incomingDirection.normalized, outgoingDirection.normalized)
                        : -1f;
                    candidates.Add(new ContinuationCandidate(
                        incoming[incomingIndex],
                        outgoing[outgoingIndex],
                        previous.To == next.From,
                        previous.SubMesh == next.SubMesh,
                        directionScore));
                }
            }
            candidates.Sort((left, right) =>
            {
                int compare = right.ExactVertex.CompareTo(left.ExactVertex);
                if (compare != 0) return compare;
                compare = right.DirectionScore.CompareTo(left.DirectionScore);
                if (compare != 0) return compare;
                compare = right.SameSubMesh.CompareTo(left.SameSubMesh);
                if (compare != 0) return compare;
                compare = left.Incoming.CompareTo(right.Incoming);
                return compare != 0 ? compare : left.Outgoing.CompareTo(right.Outgoing);
            });

            var pairedIncoming = new HashSet<int>();
            var pairedOutgoing = new HashSet<int>();
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                if (pairedIncoming.Contains(candidate.Incoming) || pairedOutgoing.Contains(candidate.Outgoing)) continue;
                continuation[candidate.Incoming] = candidate.Outgoing;
                pairedIncoming.Add(candidate.Incoming);
                pairedOutgoing.Add(candidate.Outgoing);
            }
            if (pairedIncoming.Count != incoming.Count || pairedOutgoing.Count != outgoing.Count)
                throw new InvalidOperationException(
                    $"SPLIT_CAP_BOUNDARY_PAIRING_FAILED: cut vertex {canonicalVertex} could not be decomposed into continuous loops.");
        }

        private static int AppendInterpolatedCenter(
            BoundaryLoop loop,
            List<MeshVertexSource> vertexSources,
            List<int> sourceVertexIndices)
        {
            var unique = new SortedSet<int>();
            for (int edgeIndex = 0; edgeIndex < loop.Edges.Count; edgeIndex++)
            {
                unique.Add(loop.Edges[edgeIndex].From);
                unique.Add(loop.Edges[edgeIndex].To);
            }
            var indices = new int[unique.Count];
            unique.CopyTo(indices);
            var weights = new float[indices.Length];
            float weight = 1f / indices.Length;
            for (int i = 0; i < weights.Length; i++) weights[i] = weight;
            int destination = vertexSources.Count;
            vertexSources.Add(new MeshVertexSource(indices, weights));
            sourceVertexIndices.Add(-1);
            return destination;
        }

        private static int[] BuildCanonicalVertices(IReadOnlyList<Vector3> positions)
        {
            var result = new int[positions.Count];
            if (positions.Count == 0) return result;
            var minimum = positions[0];
            var maximum = positions[0];
            for (int i = 1; i < positions.Count; i++)
            {
                minimum = Vector3.Min(minimum, positions[i]);
                maximum = Vector3.Max(maximum, positions[i]);
            }
            float tolerance = Mathf.Max((maximum - minimum).magnitude * 1e-7f, 1e-7f);
            float toleranceSquared = tolerance * tolerance;
            var representatives = new List<int>();
            var cells = new Dictionary<SpatialCell, List<int>>();
            for (int vertex = 0; vertex < positions.Count; vertex++)
            {
                var cell = Cell(positions[vertex], tolerance);
                int canonical = -1;
                for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    var neighbor = new SpatialCell(cell.x + x, cell.y + y, cell.z + z);
                    if (!cells.TryGetValue(neighbor, out var candidates)) continue;
                    for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                    {
                        int candidate = candidates[candidateIndex];
                        if ((positions[vertex] - positions[representatives[candidate]]).sqrMagnitude > toleranceSquared) continue;
                        if (canonical < 0 || candidate < canonical) canonical = candidate;
                    }
                }
                if (canonical < 0)
                {
                    canonical = representatives.Count;
                    representatives.Add(vertex);
                    var key = new SpatialCell(cell.x, cell.y, cell.z);
                    if (!cells.TryGetValue(key, out var list)) cells.Add(key, list = new List<int>());
                    list.Add(canonical);
                }
                result[vertex] = canonical;
            }
            return result;
        }

        private static (int x, int y, int z) Cell(Vector3 position, float tolerance)
            => (
                Mathf.FloorToInt(position.x / tolerance),
                Mathf.FloorToInt(position.y / tolerance),
                Mathf.FloorToInt(position.z / tolerance));

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
