using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Detects "sharp" edges of a mesh — edges shared by two triangles whose
    /// face normals form a dihedral angle greater than the threshold. These
    /// are the visual contours / creases a viewer notices (e.g. the outline
    /// of a sidewalk where it meets the road).
    ///
    /// Connected sharp edges are merged into polylines via Union-Find on
    /// vertices. The resulting EdgeChainRegions carry world-space vertex
    /// positions in chain order so the LLM (or the highlight renderer) can
    /// draw them directly.
    public static class SharpEdgeDetector
    {
        public sealed class Params
        {
            public float DihedralAngleMinDeg = 30f;
            public float MinChainLength = 0.1f;     // metres in world space
            public int MaxChains = 64;              // safety cap
        }

        public static List<EdgeChainRegion> Detect(Mesh mesh, Matrix4x4 localToWorld,
            string meshKey, string paramsHash, int ownerInstanceId, Params p)
        {
            var result = new List<EdgeChainRegion>();
            if (mesh == null) return result;

            var verts = mesh.vertices;
            var tris = mesh.triangles;
            if (verts.Length == 0 || tris.Length < 3) return result;

            // Compute angles in world space: non-uniform Transform scale can
            // change dihedral angles, so local-space normals are insufficient.
            int triCount = tris.Length / 3;
            var faceNormals = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var a = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 0]]);
                var b = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 1]]);
                var c = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 2]]);
                faceNormals[t] = Vector3.Cross(b - a, c - a).normalized;
            }

            // 2. Build edge → triangle adjacency. Each edge stored as the
            //    sorted pair (minVertexIndex, maxVertexIndex). We accept the
            //    common case of an edge shared by exactly two triangles; edges
            //    on the boundary (single triangle) are treated as sharp by
            //    convention (they're a silhouette).
            var edgeToTris = new Dictionary<long, (int t0, int t1)>(triCount * 3);
            for (int t = 0; t < triCount; t++)
            {
                AddEdge(edgeToTris, tris[3 * t + 0], tris[3 * t + 1], t);
                AddEdge(edgeToTris, tris[3 * t + 1], tris[3 * t + 2], t);
                AddEdge(edgeToTris, tris[3 * t + 2], tris[3 * t + 0], t);
            }

            // 3. Filter to sharp edges. Cos of angle is faster than acos.
            float cosThresh = Mathf.Cos(p.DihedralAngleMinDeg * Mathf.Deg2Rad);
            var sharpEdges = new List<(int v0, int v1, int t0, int t1)>();
            foreach (var kv in edgeToTris)
            {
                var (a, b) = UnpackEdge(kv.Key);
                var (t0, t1) = kv.Value;
                bool isSharp;
                if (t1 < 0)
                {
                    isSharp = true;   // boundary edge — always a silhouette
                }
                else
                {
                    float cosAngle = Vector3.Dot(faceNormals[t0], faceNormals[t1]);
                    // cos decreases as angle grows; below cosThresh means angle > threshold
                    isSharp = cosAngle < cosThresh;
                }
                if (isSharp) sharpEdges.Add((a, b, t0, t1));
            }

            if (sharpEdges.Count == 0) return result;

            // 4. Chain edges via Union-Find on vertex indices. Two sharp edges
            //    that share a vertex go in the same chain. This produces
            //    connected components; loops vs open chains are determined
            //    by per-vertex degree.
            var uf = new UnionFind(verts.Length);
            foreach (var e in sharpEdges) uf.Union(e.v0, e.v1);

            // 5. Group edges by root component.
            var byRoot = new Dictionary<int, List<(int v0, int v1)>>();
            foreach (var e in sharpEdges)
            {
                int root = uf.Find(e.v0);
                if (!byRoot.TryGetValue(root, out var list))
                {
                    list = new List<(int, int)>();
                    byRoot[root] = list;
                }
                list.Add((e.v0, e.v1));
            }

            // 6. For each component: order vertices along the chain via a
            //    simple traversal of the vertex-adjacency graph. Detects loops.
            int chainIndex = 0;
            foreach (var kv in byRoot)
            {
                var orderedLocal = OrderChain(kv.Value, out bool isLoop);
                if (orderedLocal == null || orderedLocal.Count < 2) continue;

                // Transform to world space and measure length.
                var world = new Vector3[orderedLocal.Count];
                float length = 0f;
                for (int i = 0; i < orderedLocal.Count; i++)
                {
                    world[i] = localToWorld.MultiplyPoint3x4(verts[orderedLocal[i]]);
                    if (i > 0) length += Vector3.Distance(world[i - 1], world[i]);
                }
                if (isLoop && orderedLocal.Count >= 2)
                    length += Vector3.Distance(world[world.Length - 1], world[0]);

                if (length < p.MinChainLength) continue;

                // Collect adjacent face indices for the chain (used by
                // render_region_highlight to dim non-adjacent geometry).
                var faceSet = new HashSet<int>();
                foreach (var e in kv.Value)
                {
                    // Re-derive the adjacency from edgeToTris — kv.Value only
                    // has (v0,v1); look the edge up to get (t0,t1).
                    if (edgeToTris.TryGetValue(PackEdge(e.v0, e.v1), out var ts))
                    {
                        if (ts.t0 >= 0) faceSet.Add(ts.t0);
                        if (ts.t1 >= 0) faceSet.Add(ts.t1);
                    }
                }
                var faceArr = new int[faceSet.Count];
                faceSet.CopyTo(faceArr);

                var bounds = ComputeBounds(world);

                var chain = new EdgeChainRegion
                {
                    Kind = RegionKind.EdgeChain,
                    OwnerInstanceId = ownerInstanceId,
                    RegionId = MeshAnalysisCache.MakeRegionId(meshKey, paramsHash, RegionKind.EdgeChain, chainIndex),
                    FaceIndices = faceArr,
                    WorldBounds = bounds,
                    VertexPositionsWorld = world,
                    TotalLength = length,
                    IsLoop = isLoop,
                };
                result.Add(chain);
                chainIndex++;
                if (chainIndex >= p.MaxChains) break;
            }

            // Sort by descending total length — longest chains (most likely
            // "the borda da calçada") come first.
            result.Sort((a, b) => b.TotalLength.CompareTo(a.TotalLength));
            // Re-stamp indices to match the sorted order so the LLM sees
            // deterministic ids "edge/0" = longest.
            for (int i = 0; i < result.Count; i++)
                result[i].RegionId = MeshAnalysisCache.MakeRegionId(meshKey, paramsHash, RegionKind.EdgeChain, i);

            return result;
        }

        // ---- internals ----

        private static void AddEdge(Dictionary<long, (int t0, int t1)> dict, int a, int b, int t)
        {
            long key = PackEdge(a, b);
            if (dict.TryGetValue(key, out var existing))
            {
                if (existing.t1 < 0) dict[key] = (existing.t0, t);
                // else: non-manifold (>2 tris on an edge) — keep first two.
            }
            else
            {
                dict[key] = (t, -1);
            }
        }

        private static long PackEdge(int a, int b)
        {
            int lo = Mathf.Min(a, b);
            int hi = Mathf.Max(a, b);
            return ((long)hi << 32) | (uint)lo;
        }

        private static (int a, int b) UnpackEdge(long key)
        {
            int lo = (int)(key & 0xFFFFFFFFL);
            int hi = (int)(key >> 32);
            return (lo, hi);
        }

        private static Bounds ComputeBounds(Vector3[] pts)
        {
            if (pts.Length == 0) return new Bounds();
            var b = new Bounds(pts[0], Vector3.zero);
            for (int i = 1; i < pts.Length; i++) b.Encapsulate(pts[i]);
            return b;
        }

        /// Greedy traversal: pick an endpoint (degree 1) if any, else any
        /// vertex; walk along the edge graph consuming visited edges. Returns
        /// the longest path; if all vertices have even degree we treat the
        /// component as a loop.
        private static List<int> OrderChain(List<(int v0, int v1)> edges, out bool isLoop)
        {
            isLoop = false;
            if (edges.Count == 0) return null;

            var adj = new Dictionary<int, List<int>>();
            foreach (var e in edges)
            {
                if (!adj.ContainsKey(e.v0)) adj[e.v0] = new List<int>();
                if (!adj.ContainsKey(e.v1)) adj[e.v1] = new List<int>();
                adj[e.v0].Add(e.v1);
                adj[e.v1].Add(e.v0);
            }

            // Find start: prefer a degree-1 vertex (open chain endpoint).
            int start = -1;
            foreach (var kv in adj)
            {
                if (kv.Value.Count == 1) { start = kv.Key; break; }
            }
            if (start == -1)
            {
                foreach (var kv in adj) { start = kv.Key; break; }
                isLoop = true;
            }
            if (start == -1) return null;

            // Track visited edges (undirected) via a set of sorted pairs.
            var visited = new HashSet<long>();
            var path = new List<int> { start };
            int current = start;
            while (true)
            {
                if (!adj.TryGetValue(current, out var neighbors)) break;
                int next = -1;
                foreach (var n in neighbors)
                {
                    long key = PackEdge(current, n);
                    if (!visited.Contains(key)) { next = n; break; }
                }
                if (next == -1) break;
                visited.Add(PackEdge(current, next));
                path.Add(next);
                current = next;
                if (isLoop && current == start) break;
            }
            return path;
        }

        /// Tiny Union-Find with path compression. Sized for vertex count.
        private sealed class UnionFind
        {
            private readonly int[] _parent;
            public UnionFind(int n)
            {
                _parent = new int[n];
                for (int i = 0; i < n; i++) _parent[i] = i;
            }
            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }
            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) _parent[ra] = rb;
            }
        }
    }
}
