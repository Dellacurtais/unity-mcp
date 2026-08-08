using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SceneUx
{
    internal static class AssetModelerMeshDiagnostics
    {
        private const int DrawBudget = 4000;
        private const float UvSeamEpsilon = 1e-4f;
        private const float PositionEpsilon = 1e-5f;

        private static readonly Color BoundaryColor = new Color(0.25f, 0.55f, 1f, 1f);
        private static readonly Color NonManifoldColor = new Color(1f, 0.2f, 0.2f, 1f);
        private static readonly Color FlippedColor = new Color(1f, 0.55f, 0.1f, 1f);
        private static readonly Color UvSeamColor = new Color(1f, 0.25f, 0.85f, 1f);

        private sealed class Cache
        {
            public HalfEdgeTopology Topology;
            public Mesh Mesh;
            public MeshRevision Revision;
            public List<MeshEdgeKey> UvSeams = new List<MeshEdgeKey>();
            public List<int> FlippedFaces = new List<int>();
        }

        private static Cache _cache;

        public static int UvSeamCount => _cache?.UvSeams.Count ?? 0;
        public static int FlippedCount => _cache?.FlippedFaces.Count ?? 0;

        public static void Draw(
            Renderer renderer,
            HalfEdgeTopology topology,
            IReadOnlyList<Vector3> vertices,
            MeshRevision revision)
        {
            if (renderer == null || topology == null || vertices == null) return;
            var state = AssetModelerModelingState.ShowBoundaries || AssetModelerModelingState.ShowNonManifold
                || AssetModelerModelingState.ShowFlippedNormals || AssetModelerModelingState.ShowUvSeams;
            if (!state) return;

            EnsureCache(renderer, topology, revision);
            var localToWorld = renderer.localToWorldMatrix;

            if (AssetModelerModelingState.ShowBoundaries)
                DrawEdges(localToWorld, vertices, topology.BoundaryEdges, BoundaryColor, 2.5f);
            if (AssetModelerModelingState.ShowNonManifold)
                DrawEdges(localToWorld, vertices, topology.NonManifoldEdges, NonManifoldColor, 4f);
            if (_cache != null && AssetModelerModelingState.ShowUvSeams)
                DrawEdges(localToWorld, vertices, _cache.UvSeams, UvSeamColor, 3f);
            if (_cache != null && AssetModelerModelingState.ShowFlippedNormals)
                DrawFlippedFaces(localToWorld, topology, vertices, _cache.FlippedFaces);
        }

        public static void Refresh(Renderer renderer, HalfEdgeTopology topology, MeshRevision revision)
        {
            if (renderer == null || topology == null) { _cache = null; return; }
            if (_cache != null && ReferenceEquals(_cache.Topology, topology) && _cache.Revision.Equals(revision)) return;
            EnsureCache(renderer, topology, revision);
        }

        private static void EnsureCache(Renderer renderer, HalfEdgeTopology topology, MeshRevision revision)
        {
            var mesh = ResolveMesh(renderer);
            if (_cache != null
                && ReferenceEquals(_cache.Topology, topology)
                && ReferenceEquals(_cache.Mesh, mesh)
                && _cache.Revision.Equals(revision)) return;
            _cache = new Cache { Topology = topology, Mesh = mesh, Revision = revision };
            if (mesh == null) return;
            ComputeFlippedFaces(mesh, topology, _cache.FlippedFaces);
            _cache.UvSeams.AddRange(FindUvSeams(mesh, topology));
        }

        private static Mesh ResolveMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        private static void ComputeFlippedFaces(Mesh mesh, HalfEdgeTopology topology, List<int> output)
        {
            var normals = mesh.normals;
            var vertices = mesh.vertices;
            if (normals == null || normals.Length != vertices.Length) return;
            for (int i = 0; i < topology.Faces.Count; i++)
            {
                var face = topology.Faces[i];
                if (face.A >= vertices.Length || face.B >= vertices.Length || face.C >= vertices.Length) continue;
                var geometric = Vector3.Cross(vertices[face.B] - vertices[face.A], vertices[face.C] - vertices[face.A]);
                if (geometric.sqrMagnitude < 1e-14f) continue;
                var stored = normals[face.A] + normals[face.B] + normals[face.C];
                if (Vector3.Dot(geometric, stored) < 0f) output.Add(i);
            }
        }

        internal static List<MeshEdgeKey> FindUvSeams(Mesh mesh, HalfEdgeTopology topology)
        {
            var output = new List<MeshEdgeKey>();
            if (mesh == null || topology == null) return output;
            var uv = mesh.uv;
            var vertices = mesh.vertices;
            if (uv == null || uv.Length != mesh.vertexCount || vertices.Length != mesh.vertexCount) return output;

            var groups = new Dictionary<GeometricEdgeKey, List<UvEdgeSample>>();
            foreach (var pair in topology.EdgeFaces)
            {
                var indexed = pair.Key;
                if (indexed.A < 0 || indexed.B < 0 || indexed.A >= vertices.Length || indexed.B >= vertices.Length) continue;
                var first = PositionKey.From(vertices[indexed.A]);
                var second = PositionKey.From(vertices[indexed.B]);
                bool forward = first.CompareTo(second) <= 0;
                var geometric = forward ? new GeometricEdgeKey(first, second) : new GeometricEdgeKey(second, first);
                var sample = forward
                    ? new UvEdgeSample(indexed, uv[indexed.A], uv[indexed.B])
                    : new UvEdgeSample(indexed, uv[indexed.B], uv[indexed.A]);
                if (!groups.TryGetValue(geometric, out var samples)) groups.Add(geometric, samples = new List<UvEdgeSample>(2));
                samples.Add(sample);
            }

            float squaredEpsilon = UvSeamEpsilon * UvSeamEpsilon;
            var seams = new HashSet<MeshEdgeKey>();
            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;
                for (int left = 0; left < group.Count - 1; left++)
                for (int right = left + 1; right < group.Count; right++)
                {
                    if (group[left].Indexed.Equals(group[right].Indexed)) continue;
                    bool differs = (group[left].First - group[right].First).sqrMagnitude > squaredEpsilon
                        || (group[left].Second - group[right].Second).sqrMagnitude > squaredEpsilon;
                    if (!differs) continue;
                    seams.Add(group[left].Indexed);
                    seams.Add(group[right].Indexed);
                }
            }
            output.AddRange(seams);
            output.Sort((left, right) => left.A != right.A ? left.A.CompareTo(right.A) : left.B.CompareTo(right.B));
            return output;
        }

        private readonly struct UvEdgeSample
        {
            public MeshEdgeKey Indexed { get; }
            public Vector2 First { get; }
            public Vector2 Second { get; }

            public UvEdgeSample(MeshEdgeKey indexed, Vector2 first, Vector2 second)
            {
                Indexed = indexed;
                First = first;
                Second = second;
            }
        }

        private readonly struct GeometricEdgeKey : IEquatable<GeometricEdgeKey>
        {
            private readonly PositionKey _first;
            private readonly PositionKey _second;

            public GeometricEdgeKey(PositionKey first, PositionKey second) { _first = first; _second = second; }
            public bool Equals(GeometricEdgeKey other) => _first.Equals(other._first) && _second.Equals(other._second);
            public override bool Equals(object obj) => obj is GeometricEdgeKey other && Equals(other);
            public override int GetHashCode() => unchecked((_first.GetHashCode() * 397) ^ _second.GetHashCode());
        }

        private readonly struct PositionKey : IEquatable<PositionKey>, IComparable<PositionKey>
        {
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            private PositionKey(int x, int y, int z) { _x = x; _y = y; _z = z; }

            public static PositionKey From(Vector3 value)
                => new PositionKey(
                    Mathf.RoundToInt(value.x / PositionEpsilon),
                    Mathf.RoundToInt(value.y / PositionEpsilon),
                    Mathf.RoundToInt(value.z / PositionEpsilon));

            public int CompareTo(PositionKey other)
            {
                int x = _x.CompareTo(other._x);
                if (x != 0) return x;
                int y = _y.CompareTo(other._y);
                return y != 0 ? y : _z.CompareTo(other._z);
            }

            public bool Equals(PositionKey other) => _x == other._x && _y == other._y && _z == other._z;
            public override bool Equals(object obj) => obj is PositionKey other && Equals(other);
            public override int GetHashCode() => unchecked(((_x * 397) ^ _y) * 397 ^ _z);
        }

        private static void DrawEdges(
            Matrix4x4 localToWorld,
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<MeshEdgeKey> edges,
            Color color,
            float thickness)
        {
            using (new UnityEditor.Handles.DrawingScope(color, localToWorld))
            {
                int drawn = 0;
                for (int i = 0; i < edges.Count && drawn < DrawBudget; i++, drawn++)
                {
                    var key = edges[i];
                    if (key.A >= vertices.Count || key.B >= vertices.Count) continue;
                    UnityEditor.Handles.DrawAAPolyLine(thickness, vertices[key.A], vertices[key.B]);
                }
            }
        }

        private static void DrawFlippedFaces(
            Matrix4x4 localToWorld,
            HalfEdgeTopology topology,
            IReadOnlyList<Vector3> vertices,
            List<int> faces)
        {
            using (new UnityEditor.Handles.DrawingScope(FlippedColor, localToWorld))
            {
                int drawn = 0;
                for (int i = 0; i < faces.Count && drawn < DrawBudget; i++, drawn++)
                {
                    var face = topology.Faces[faces[i]];
                    if (face.A >= vertices.Count || face.B >= vertices.Count || face.C >= vertices.Count) continue;
                    UnityEditor.Handles.DrawAAPolyLine(3f, vertices[face.A], vertices[face.B], vertices[face.C], vertices[face.A]);
                }
            }
        }
    }
}
