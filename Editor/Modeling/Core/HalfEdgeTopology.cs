using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    public readonly struct MeshEdgeKey : IEquatable<MeshEdgeKey>
    {
        public int A { get; }
        public int B { get; }

        public MeshEdgeKey(int a, int b)
        {
            A = Math.Min(a, b);
            B = Math.Max(a, b);
        }

        public bool Equals(MeshEdgeKey other) => A == other.A && B == other.B;
        public override bool Equals(object obj) => obj is MeshEdgeKey other && Equals(other);
        public override int GetHashCode() => unchecked((A * 397) ^ B);
        public override string ToString() => $"{A}-{B}";
    }

    public sealed class HalfEdgeTopology
    {
        public sealed class HalfEdge
        {
            public int Index { get; internal set; }
            public int Origin { get; internal set; }
            public int Destination { get; internal set; }
            public int Face { get; internal set; }
            public int Next { get; internal set; }
            public int Twin { get; internal set; } = -1;
            public MeshEdgeKey EdgeKey => new MeshEdgeKey(Origin, Destination);
        }

        public sealed class Face
        {
            public int Index { get; internal set; }
            public int SubMesh { get; internal set; }
            public int TriangleIndex { get; internal set; }
            public int A { get; internal set; }
            public int B { get; internal set; }
            public int C { get; internal set; }
            public int HalfEdge { get; internal set; }
            public int[] Neighbors { get; internal set; } = Array.Empty<int>();
        }

        public int VertexCount { get; private set; }
        public IReadOnlyList<Face> Faces => _faces;
        public IReadOnlyList<HalfEdge> HalfEdges => _halfEdges;
        public IReadOnlyDictionary<MeshEdgeKey, int[]> EdgeFaces => _edgeFaces;
        public IReadOnlyList<MeshEdgeKey> BoundaryEdges => _boundaryEdges;
        public IReadOnlyList<MeshEdgeKey> NonManifoldEdges => _nonManifoldEdges;

        private readonly List<Face> _faces = new List<Face>();
        private readonly List<HalfEdge> _halfEdges = new List<HalfEdge>();
        private readonly Dictionary<MeshEdgeKey, int[]> _edgeFaces = new Dictionary<MeshEdgeKey, int[]>();
        private readonly List<MeshEdgeKey> _boundaryEdges = new List<MeshEdgeKey>();
        private readonly List<MeshEdgeKey> _nonManifoldEdges = new List<MeshEdgeKey>();

        public static HalfEdgeTopology Build(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var topology = new HalfEdgeTopology { VertexCount = mesh.vertexCount };
            var edgeLists = new Dictionary<MeshEdgeKey, List<int>>();
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(subMesh, true);
                for (int triangle = 0; triangle < indices.Length; triangle += 3)
                {
                    var face = new Face
                    {
                        Index = topology._faces.Count,
                        SubMesh = subMesh,
                        TriangleIndex = triangle / 3,
                        A = indices[triangle],
                        B = indices[triangle + 1],
                        C = indices[triangle + 2],
                    };
                    int firstHalfEdge = topology._halfEdges.Count;
                    face.HalfEdge = firstHalfEdge;
                    topology._faces.Add(face);
                    topology._halfEdges.Add(new HalfEdge { Index = firstHalfEdge, Origin = face.A, Destination = face.B, Face = face.Index, Next = firstHalfEdge + 1 });
                    topology._halfEdges.Add(new HalfEdge { Index = firstHalfEdge + 1, Origin = face.B, Destination = face.C, Face = face.Index, Next = firstHalfEdge + 2 });
                    topology._halfEdges.Add(new HalfEdge { Index = firstHalfEdge + 2, Origin = face.C, Destination = face.A, Face = face.Index, Next = firstHalfEdge });
                    AddEdge(edgeLists, new MeshEdgeKey(face.A, face.B), face.Index);
                    AddEdge(edgeLists, new MeshEdgeKey(face.B, face.C), face.Index);
                    AddEdge(edgeLists, new MeshEdgeKey(face.C, face.A), face.Index);
                }
            }

            var neighborSets = new HashSet<int>[topology._faces.Count];
            for (int i = 0; i < neighborSets.Length; i++) neighborSets[i] = new HashSet<int>();
            foreach (var pair in edgeLists)
            {
                var faces = pair.Value.ToArray();
                topology._edgeFaces.Add(pair.Key, faces);
                if (faces.Length == 1) topology._boundaryEdges.Add(pair.Key);
                if (faces.Length > 2) topology._nonManifoldEdges.Add(pair.Key);
                for (int left = 0; left < faces.Length; left++)
                    for (int right = left + 1; right < faces.Length; right++)
                    {
                        neighborSets[faces[left]].Add(faces[right]);
                        neighborSets[faces[right]].Add(faces[left]);
                    }
            }
            var halfEdgesByKey = new Dictionary<MeshEdgeKey, List<int>>();
            for (int i = 0; i < topology._halfEdges.Count; i++)
            {
                var key = topology._halfEdges[i].EdgeKey;
                if (!halfEdgesByKey.TryGetValue(key, out var list)) halfEdgesByKey.Add(key, list = new List<int>(2));
                list.Add(i);
            }
            foreach (var pair in halfEdgesByKey)
            {
                if (pair.Value.Count != 2) continue;
                var left = topology._halfEdges[pair.Value[0]];
                var right = topology._halfEdges[pair.Value[1]];
                if (left.Origin == right.Destination && left.Destination == right.Origin)
                {
                    left.Twin = right.Index;
                    right.Twin = left.Index;
                }
            }
            for (int i = 0; i < topology._faces.Count; i++)
                topology._faces[i].Neighbors = new List<int>(neighborSets[i]).ToArray();
            return topology;
        }

        public IReadOnlyList<int[]> ConnectedFaceComponents()
        {
            var components = new List<int[]>();
            var visited = new bool[_faces.Count];
            var queue = new Queue<int>();
            for (int start = 0; start < _faces.Count; start++)
            {
                if (visited[start]) continue;
                var component = new List<int>();
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int faceIndex = queue.Dequeue();
                    component.Add(faceIndex);
                    var neighbors = _faces[faceIndex].Neighbors;
                    for (int i = 0; i < neighbors.Length; i++)
                    {
                        int neighbor = neighbors[i];
                        if (visited[neighbor]) continue;
                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
                components.Add(component.ToArray());
            }
            return components;
        }

        public IReadOnlyList<int[]> BoundaryLoops()
        {
            var outgoing = new Dictionary<int, List<int>>();
            for (int i = 0; i < _halfEdges.Count; i++)
            {
                var edge = _halfEdges[i];
                if (edge.Twin >= 0 || !_edgeFaces.TryGetValue(edge.EdgeKey, out var faces) || faces.Length != 1) continue;
                if (!outgoing.TryGetValue(edge.Origin, out var list)) outgoing.Add(edge.Origin, list = new List<int>());
                list.Add(edge.Index);
            }
            foreach (var pair in outgoing) pair.Value.Sort((a, b) => _halfEdges[a].Destination.CompareTo(_halfEdges[b].Destination));
            var visited = new HashSet<int>();
            var loops = new List<int[]>();
            for (int start = 0; start < _halfEdges.Count; start++)
            {
                if (visited.Contains(start) || _halfEdges[start].Twin >= 0) continue;
                var current = _halfEdges[start];
                if (!_edgeFaces.TryGetValue(current.EdgeKey, out var currentFaces) || currentFaces.Length != 1) continue;
                var vertices = new List<int>();
                int edgeIndex = start;
                while (!visited.Contains(edgeIndex))
                {
                    visited.Add(edgeIndex);
                    var edge = _halfEdges[edgeIndex];
                    vertices.Add(edge.Origin);
                    if (!outgoing.TryGetValue(edge.Destination, out var candidates)) break;
                    int next = -1;
                    for (int i = 0; i < candidates.Count; i++)
                        if (!visited.Contains(candidates[i])) { next = candidates[i]; break; }
                    if (next < 0) break;
                    edgeIndex = next;
                }
                if (vertices.Count >= 2) loops.Add(vertices.ToArray());
            }
            return loops;
        }

        public MeshValidationReport Validate()
        {
            var report = new MeshValidationReport();
            if (_nonManifoldEdges.Count > 0)
                report.Warning("NON_MANIFOLD_EDGES", $"Mesh contains {_nonManifoldEdges.Count} edges shared by more than two faces.");
            if (_boundaryEdges.Count > 0)
                report.Warning("BOUNDARY_EDGES", $"Mesh contains {_boundaryEdges.Count} open boundary edges.");
            return report;
        }

        private static void AddEdge(Dictionary<MeshEdgeKey, List<int>> edges, MeshEdgeKey key, int face)
        {
            if (!edges.TryGetValue(key, out var faces)) edges.Add(key, faces = new List<int>(2));
            faces.Add(face);
        }
    }
}
