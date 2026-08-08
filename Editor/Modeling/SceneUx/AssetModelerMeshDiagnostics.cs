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

        private static readonly Color BoundaryColor = new Color(0.25f, 0.55f, 1f, 1f);
        private static readonly Color NonManifoldColor = new Color(1f, 0.2f, 0.2f, 1f);
        private static readonly Color FlippedColor = new Color(1f, 0.55f, 0.1f, 1f);
        private static readonly Color UvSeamColor = new Color(1f, 0.25f, 0.85f, 1f);

        private sealed class Cache
        {
            public HalfEdgeTopology Topology;
            public Mesh Mesh;
            public List<MeshEdgeKey> UvSeams = new List<MeshEdgeKey>();
            public List<int> FlippedFaces = new List<int>();
        }

        private static Cache _cache;

        public static int UvSeamCount => _cache?.UvSeams.Count ?? 0;
        public static int FlippedCount => _cache?.FlippedFaces.Count ?? 0;

        public static void Draw(Renderer renderer, HalfEdgeTopology topology, IReadOnlyList<Vector3> vertices)
        {
            if (renderer == null || topology == null || vertices == null) return;
            var state = AssetModelerModelingState.ShowBoundaries || AssetModelerModelingState.ShowNonManifold
                || AssetModelerModelingState.ShowFlippedNormals || AssetModelerModelingState.ShowUvSeams;
            if (!state) return;

            EnsureCache(renderer, topology);
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

        public static void Refresh(Renderer renderer, HalfEdgeTopology topology)
        {
            if (renderer == null || topology == null) { _cache = null; return; }
            if (_cache != null && ReferenceEquals(_cache.Topology, topology)) return;
            EnsureCache(renderer, topology);
        }

        private static void EnsureCache(Renderer renderer, HalfEdgeTopology topology)
        {
            var mesh = ResolveMesh(renderer);
            if (_cache != null && ReferenceEquals(_cache.Topology, topology) && ReferenceEquals(_cache.Mesh, mesh)) return;
            _cache = new Cache { Topology = topology, Mesh = mesh };
            if (mesh == null) return;
            ComputeFlippedFaces(mesh, topology, _cache.FlippedFaces);
            ComputeUvSeams(mesh, topology, _cache.UvSeams);
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

        private static void ComputeUvSeams(Mesh mesh, HalfEdgeTopology topology, List<MeshEdgeKey> output)
        {
            var uv = mesh.uv;
            if (uv == null || uv.Length != mesh.vertexCount) return;
            var subMeshTriangles = new int[mesh.subMeshCount][];
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                subMeshTriangles[subMesh] = mesh.GetTopology(subMesh) == MeshTopology.Triangles
                    ? mesh.GetTriangles(subMesh)
                    : Array.Empty<int>();

            foreach (var pair in topology.EdgeFaces)
            {
                var faces = pair.Value;
                if (faces.Length != 2) continue;
                if (IsSeam(topology, subMeshTriangles, uv, faces[0], faces[1], pair.Key.A)
                    || IsSeam(topology, subMeshTriangles, uv, faces[0], faces[1], pair.Key.B))
                    output.Add(pair.Key);
            }
        }

        private static bool IsSeam(
            HalfEdgeTopology topology,
            int[][] subMeshTriangles,
            Vector2[] uv,
            int faceA,
            int faceB,
            int vertex)
        {
            int cornerA = FindCorner(topology.Faces[faceA], subMeshTriangles, vertex);
            int cornerB = FindCorner(topology.Faces[faceB], subMeshTriangles, vertex);
            if (cornerA < 0 || cornerB < 0) return false;
            return (uv[cornerA] - uv[cornerB]).sqrMagnitude > UvSeamEpsilon * UvSeamEpsilon;
        }

        private static int FindCorner(HalfEdgeTopology.Face face, int[][] subMeshTriangles, int vertex)
        {
            var triangles = subMeshTriangles[face.SubMesh];
            int start = face.TriangleIndex * 3;
            for (int i = 0; i < 3; i++)
                if (start + i < triangles.Length && triangles[start + i] == vertex) return triangles[start + i];
            return -1;
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
