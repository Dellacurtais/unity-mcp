using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Region-growing segmentation across face adjacency. Two adjacent faces
    /// belong to the same region iff the dihedral angle between them is
    /// smaller than `frontierAngleTolerance` — i.e. the boundary between
    /// them is NOT a sharp edge.
    ///
    /// This is the dual of SharpEdgeDetector: sharp edges separate smooth
    /// regions. The two are complementary — sharp_edges gives you the
    /// contours, smooth_regions gives you the faces between them. Useful for
    /// organic surfaces (terrain, sculpted props, rounded handrails) where
    /// PlaneDetector wouldn't find a strict plane.
    public static class SmoothRegionDetector
    {
        public sealed class Params
        {
            public float FrontierAngleToleranceDeg = 15f;  // < this is "smooth", >= is "edge"
            public int MinFacesPerRegion = 16;
            public int MaxRegions = 64;
        }

        public static List<SmoothRegion> Detect(Mesh mesh, Matrix4x4 localToWorld,
            string meshKey, string paramsHash, int ownerInstanceId, Params p)
        {
            var result = new List<SmoothRegion>();
            if (mesh == null) return result;

            var verts = mesh.vertices;
            var tris = mesh.triangles;
            int triCount = tris.Length / 3;
            if (triCount == 0) return result;

            // 1. Per-triangle world centroid / normal / area.
            var normalsWorld = new Vector3[triCount];
            var areas = new float[triCount];
            var aWorld = new Vector3[triCount];
            var bWorld = new Vector3[triCount];
            var cWorld = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var a = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 0]]);
                var b = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 1]]);
                var c = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 2]]);
                aWorld[t] = a; bWorld[t] = b; cWorld[t] = c;
                var cross = Vector3.Cross(b - a, c - a);
                float area = 0.5f * cross.magnitude;
                areas[t] = area;
                normalsWorld[t] = area > 1e-9f ? cross / (2f * area) : Vector3.up;
            }

            // 2. Build face-adjacency list (face → neighbours sharing an edge).
            //    Build edge map first, then derive adjacency.
            var edgeToTris = new Dictionary<long, (int t0, int t1)>(triCount * 3);
            for (int t = 0; t < triCount; t++)
            {
                AddEdge(edgeToTris, tris[3 * t + 0], tris[3 * t + 1], t);
                AddEdge(edgeToTris, tris[3 * t + 1], tris[3 * t + 2], t);
                AddEdge(edgeToTris, tris[3 * t + 2], tris[3 * t + 0], t);
            }
            var adj = new List<int>[triCount];
            for (int t = 0; t < triCount; t++) adj[t] = new List<int>(3);
            foreach (var kv in edgeToTris)
            {
                var (t0, t1) = kv.Value;
                if (t1 < 0) continue;
                adj[t0].Add(t1);
                adj[t1].Add(t0);
            }

            // 3. Flood-fill: for each unvisited face, BFS across "smooth"
            //    adjacencies (cos(angle) >= cosThresh). Connected components
            //    are the smooth regions.
            float cosThresh = Mathf.Cos(p.FrontierAngleToleranceDeg * Mathf.Deg2Rad);
            var visited = new bool[triCount];
            var queue = new Queue<int>();

            for (int seed = 0; seed < triCount; seed++)
            {
                if (visited[seed]) continue;
                visited[seed] = true;

                var faces = new List<int> { seed };
                queue.Clear();
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    foreach (var nb in adj[current])
                    {
                        if (visited[nb]) continue;
                        // Smooth adjacency: angle below frontier threshold.
                        float cosAngle = Vector3.Dot(normalsWorld[current], normalsWorld[nb]);
                        if (cosAngle < cosThresh) continue;
                        visited[nb] = true;
                        faces.Add(nb);
                        queue.Enqueue(nb);
                    }
                }

                if (faces.Count < p.MinFacesPerRegion) continue;

                // Average normal (area-weighted) and roughness.
                var avgN = Vector3.zero;
                float totalArea = 0f;
                foreach (var f in faces)
                {
                    avgN += normalsWorld[f] * areas[f];
                    totalArea += areas[f];
                }
                avgN = avgN.normalized;
                float roughness = 0f;
                foreach (var f in faces)
                    roughness += (1f - Mathf.Clamp(Vector3.Dot(normalsWorld[f], avgN), -1f, 1f)) * areas[f];
                roughness = totalArea > 1e-6f ? roughness / totalArea : 0f;

                // World bounds.
                var bounds = new Bounds(aWorld[faces[0]], Vector3.zero);
                foreach (var f in faces)
                {
                    bounds.Encapsulate(aWorld[f]);
                    bounds.Encapsulate(bWorld[f]);
                    bounds.Encapsulate(cWorld[f]);
                }

                var region = new SmoothRegion
                {
                    Kind = RegionKind.Smooth,
                    OwnerInstanceId = ownerInstanceId,
                    FaceIndices = faces.ToArray(),
                    FaceCount = faces.Count,
                    AvgNormalWorld = avgN,
                    AreaApprox = totalArea,
                    RoughnessMetric = roughness,
                    OrientationLabel = OrientationLabel(avgN),
                    WorldBounds = bounds,
                };
                result.Add(region);
                if (result.Count >= p.MaxRegions) break;
            }

            // Sort by descending area; stamp regionIds in sorted order.
            result.Sort((a, b) => b.AreaApprox.CompareTo(a.AreaApprox));
            for (int i = 0; i < result.Count; i++)
                result[i].RegionId = MeshAnalysisCache.MakeRegionId(meshKey, paramsHash, RegionKind.Smooth, i);

            return result;
        }

        private static void AddEdge(Dictionary<long, (int t0, int t1)> dict, int a, int b, int t)
        {
            long key = PackEdge(a, b);
            if (dict.TryGetValue(key, out var existing))
            {
                if (existing.t1 < 0) dict[key] = (existing.t0, t);
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

        private static string OrientationLabel(Vector3 worldNormal)
        {
            const float horizThresholdDeg = 30f;
            float cosHoriz = Mathf.Cos(horizThresholdDeg * Mathf.Deg2Rad);
            float dotUp = Vector3.Dot(worldNormal.normalized, Vector3.up);
            if (dotUp > cosHoriz) return "horizontal_up";
            if (dotUp < -cosHoriz) return "horizontal_down";
            float bearing = Mathf.Atan2(worldNormal.z, worldNormal.x) * Mathf.Rad2Deg;
            if (bearing < 0) bearing += 360f;
            return $"vertical:{Mathf.RoundToInt(bearing)}";
        }
    }
}
