using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology
{
    public sealed class MeshCleanupComponent
    {
        public int Id { get; internal set; }
        public int FaceCount { get; internal set; }
        public int VertexCount { get; internal set; }
        public int FirstFaceIndex { get; internal set; }
        public int[] SubMeshIndices { get; internal set; }
        public Bounds Bounds { get; internal set; }
    }

    // Connectivity is by shared vertex-index edges, not positional welding.
    // UV seams and intentional separate parts may therefore form separate islands.
    public sealed class MeshCleanupAnalysis
    {
        public int VertexCount { get; private set; }
        public int SubMeshCount { get; private set; }
        public int FaceCount => Faces.Count;
        public int UnreferencedVertexCount { get; private set; }
        public int DegenerateFaceCount { get; private set; }
        public int LooseLineCount { get; private set; }
        public int LoosePointCount { get; private set; }
        public IReadOnlyList<MeshCleanupComponent> Components => _components;
        private readonly List<MeshCleanupComponent> _components = new List<MeshCleanupComponent>();
        internal readonly List<SurfaceFace> Faces = new List<SurfaceFace>();
        internal readonly HashSet<MeshEdgeKey> SurfaceEdges = new HashSet<MeshEdgeKey>();
        internal bool[] SurfaceVertices;

        internal sealed class SurfaceFace
        {
            public int SubMesh;
            public int IndexStart;
            public int[] Vertices;
            public bool Degenerate;
            public bool ExactDegenerate;
            public int ComponentId = -1;
        }

        public static MeshCleanupAnalysis Analyze(Mesh mesh, float areaEpsilon = 0f)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            return Analyze(MeshChannelSnapshot.Capture(mesh), areaEpsilon);
        }

        internal static MeshCleanupAnalysis Analyze(MeshChannelSnapshot snapshot, float areaEpsilon)
        {
            if (areaEpsilon < 0f || float.IsNaN(areaEpsilon) || float.IsInfinity(areaEpsilon))
                throw new InvalidOperationException("CLEANUP_EPSILON_INVALID: minimum area must be finite and nonnegative.");
            var result = new MeshCleanupAnalysis
            {
                VertexCount = snapshot.Positions.Length,
                SubMeshCount = snapshot.SubMeshes.Count,
                SurfaceVertices = new bool[snapshot.Positions.Length],
            };
            var referenced = new bool[result.VertexCount];
            for (int vertex = 0; vertex < snapshot.Positions.Length; vertex++)
            {
                var p = snapshot.Positions[vertex];
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z))
                    throw new InvalidOperationException("CLEANUP_INVALID_MESH: positions must be finite before cleanup.");
            }
            for (int subMesh = 0; subMesh < snapshot.SubMeshes.Count; subMesh++)
            {
                var source = snapshot.SubMeshes[subMesh];
                int arity;
                switch (source.Topology)
                {
                    case MeshTopology.Triangles: arity = 3; break;
                    case MeshTopology.Quads: arity = 4; break;
                    case MeshTopology.Lines: arity = 2; break;
                    case MeshTopology.Points:
                    case MeshTopology.LineStrip: arity = 1; break;
                    default: throw new InvalidOperationException("CLEANUP_TOPOLOGY_UNSUPPORTED: unsupported primitive topology.");
                }
                if (source.Indices.Length % arity != 0)
                    throw new InvalidOperationException("CLEANUP_INVALID_MESH: incomplete primitive indices.");
                foreach (int index in source.Indices)
                {
                    if (index < 0 || index >= referenced.Length)
                        throw new InvalidOperationException("CLEANUP_INVALID_MESH: index is outside the vertex buffer.");
                    referenced[index] = true;
                }
                if (source.Topology != MeshTopology.Triangles && source.Topology != MeshTopology.Quads) continue;
                for (int offset = 0; offset < source.Indices.Length; offset += arity)
                {
                    var vertices = new int[arity];
                    Array.Copy(source.Indices, offset, vertices, 0, arity);
                    var face = new SurfaceFace { SubMesh = subMesh, IndexStart = offset, Vertices = vertices };
                    face.ExactDegenerate = arity == 3 && DegenerateTriangle(snapshot.Positions, vertices, 0f);
                    face.Degenerate = face.ExactDegenerate || (arity == 3 && areaEpsilon > 0f
                        && DegenerateTriangle(snapshot.Positions, vertices, areaEpsilon));
                    result.Faces.Add(face);
                    if (face.Degenerate) { result.DegenerateFaceCount++; continue; }
                    for (int corner = 0; corner < arity; corner++)
                    {
                        result.SurfaceVertices[vertices[corner]] = true;
                        result.SurfaceEdges.Add(new MeshEdgeKey(vertices[corner], vertices[(corner + 1) % arity]));
                    }
                }
            }
            for (int i = 0; i < referenced.Length; i++) if (!referenced[i]) result.UnreferencedVertexCount++;
            foreach (var subMesh in snapshot.SubMeshes)
            {
                if (subMesh.Topology == MeshTopology.Points)
                    foreach (int index in subMesh.Indices) { if (!result.SurfaceVertices[index]) result.LoosePointCount++; }
                else if (subMesh.Topology == MeshTopology.Lines || subMesh.Topology == MeshTopology.LineStrip)
                {
                    int step = subMesh.Topology == MeshTopology.Lines ? 2 : 1;
                    for (int i = 0; i + 1 < subMesh.Indices.Length; i += step)
                        if (!result.SurfaceEdges.Contains(new MeshEdgeKey(subMesh.Indices[i], subMesh.Indices[i + 1]))) result.LooseLineCount++;
                }
            }
            result.BuildComponents(snapshot.Positions);
            return result;
        }

        private void BuildComponents(Vector3[] positions)
        {
            var parent = new int[Faces.Count];
            var owner = new Dictionary<MeshEdgeKey, int>();
            for (int faceIndex = 0; faceIndex < Faces.Count; faceIndex++)
            {
                parent[faceIndex] = faceIndex;
                var face = Faces[faceIndex];
                // Component ids belong to the mesh revision, not the chosen
                // cleanup threshold. Tiny valid faces still define their island.
                if (face.ExactDegenerate) continue;
                for (int i = 0; i < face.Vertices.Length; i++)
                {
                    var edge = new MeshEdgeKey(face.Vertices[i], face.Vertices[(i + 1) % face.Vertices.Length]);
                    if (owner.TryGetValue(edge, out int other)) parent[Root(parent, faceIndex)] = Root(parent, other);
                    else owner.Add(edge, faceIndex);
                }
            }
            var byRoot = new Dictionary<int, int>();
            var vertices = new List<HashSet<int>>();
            var subMeshes = new List<SortedSet<int>>();
            for (int faceIndex = 0; faceIndex < Faces.Count; faceIndex++)
            {
                var face = Faces[faceIndex];
                if (face.ExactDegenerate) continue;
                int root = Root(parent, faceIndex);
                if (!byRoot.TryGetValue(root, out int componentId))
                {
                    componentId = _components.Count;
                    byRoot.Add(root, componentId);
                    _components.Add(new MeshCleanupComponent
                    {
                        Id = componentId, FirstFaceIndex = faceIndex,
                        Bounds = new Bounds(positions[face.Vertices[0]], Vector3.zero),
                    });
                    vertices.Add(new HashSet<int>());
                    subMeshes.Add(new SortedSet<int>());
                }
                face.ComponentId = componentId;
                var component = _components[componentId];
                component.FaceCount++;
                subMeshes[componentId].Add(face.SubMesh);
                var bounds = component.Bounds;
                foreach (int vertex in face.Vertices)
                    if (vertices[componentId].Add(vertex)) bounds.Encapsulate(positions[vertex]);
                component.Bounds = bounds;
            }
            for (int i = 0; i < _components.Count; i++)
            {
                _components[i].VertexCount = vertices[i].Count;
                _components[i].SubMeshIndices = new List<int>(subMeshes[i]).ToArray();
            }
        }

        private static int Root(int[] parent, int item)
        {
            while (parent[item] != item) { parent[item] = parent[parent[item]]; item = parent[item]; }
            return item;
        }

        private static bool DegenerateTriangle(Vector3[] positions, int[] vertices, float minimumArea)
        {
            if (vertices[0] == vertices[1] || vertices[1] == vertices[2] || vertices[0] == vertices[2]) return true;
            var a = positions[vertices[0]];
            var b = positions[vertices[1]];
            var c = positions[vertices[2]];
            // Double precision avoids a Float32 cross-product underflow deleting
            // real, very small faces in assets imported at tiny scales.
            double ux = (double)b.x - a.x, uy = (double)b.y - a.y, uz = (double)b.z - a.z;
            double vx = (double)c.x - a.x, vy = (double)c.y - a.y, vz = (double)c.z - a.z;
            double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
            double threshold = 4d * minimumArea * minimumArea;
            return x * x + y * y + z * z <= threshold;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
