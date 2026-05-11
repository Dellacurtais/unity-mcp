using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Iterative RANSAC plane segmentation over triangles. For each round we
    /// sample N candidate planes (3 random unassigned triangles each), pick
    /// the one with most inliers under (normal, distance) tolerance, commit
    /// those triangles as a region, then repeat on the remainder until the
    /// next best plane is below minFacesPerPlane or we hit maxPlanes.
    ///
    /// After segmentation, each plane gets an orientationLabel (horizontal_up
    /// / horizontal_down / vertical:NNN) and a Classification heuristic
    /// (ground_candidate / platform_candidate / wall_candidate / ...).
    public static class PlaneDetector
    {
        public sealed class Params
        {
            public int MinFacesPerPlane = 50;
            public float AngleToleranceDeg = 5f;
            public float DistanceTolerance = -1f;   // -1 = auto (1% of bounds diag)
            public int MaxPlanes = 16;
            public int RansacIterationsPerPlane = 32;
            public int RandomSeed = 12345;
        }

        public static List<PlaneRegion> Detect(Mesh mesh, Matrix4x4 localToWorld,
            string meshKey, string paramsHash, int ownerInstanceId, Params p)
        {
            var result = new List<PlaneRegion>();
            if (mesh == null) return result;

            var verts = mesh.vertices;
            var tris = mesh.triangles;
            int triCount = tris.Length / 3;
            if (triCount == 0 || verts.Length == 0) return result;

            // 1. Pre-compute per-triangle world-space centroid + normal + area.
            //    Working in world space simplifies orientation classification.
            var centroids = new Vector3[triCount];
            var normals = new Vector3[triCount];
            var areas = new float[triCount];
            var totalBounds = new Bounds(localToWorld.MultiplyPoint3x4(verts[0]), Vector3.zero);

            for (int t = 0; t < triCount; t++)
            {
                var a = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 0]]);
                var b = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 1]]);
                var c = localToWorld.MultiplyPoint3x4(verts[tris[3 * t + 2]]);
                centroids[t] = (a + b + c) / 3f;
                var cross = Vector3.Cross(b - a, c - a);
                areas[t] = 0.5f * cross.magnitude;
                normals[t] = areas[t] > 1e-9f ? cross / (2f * areas[t]) : Vector3.up;
                totalBounds.Encapsulate(a);
                totalBounds.Encapsulate(b);
                totalBounds.Encapsulate(c);
            }

            float diag = totalBounds.size.magnitude;
            float distTol = p.DistanceTolerance > 0 ? p.DistanceTolerance : Mathf.Max(0.001f, diag * 0.01f);
            float cosAngleTol = Mathf.Cos(p.AngleToleranceDeg * Mathf.Deg2Rad);

            // 2. Iterative RANSAC. "assigned" marks tris that already belong to a plane.
            var assigned = new bool[triCount];
            int remaining = triCount;
            var rng = new System.Random(p.RandomSeed);

            int planeIndex = 0;
            while (planeIndex < p.MaxPlanes && remaining >= p.MinFacesPerPlane)
            {
                var (bestInliers, bestNormal, bestPoint) =
                    FindBestPlane(centroids, normals, areas, assigned, remaining, cosAngleTol, distTol, p.RansacIterationsPerPlane, rng);

                if (bestInliers == null || bestInliers.Count < p.MinFacesPerPlane) break;

                // Commit.
                float area = 0f;
                var centroidSum = Vector3.zero;
                foreach (var idx in bestInliers)
                {
                    assigned[idx] = true;
                    area += areas[idx];
                    centroidSum += centroids[idx] * areas[idx];
                }
                remaining -= bestInliers.Count;
                var weighted = area > 1e-6f ? centroidSum / area : bestPoint;

                var plane = new PlaneRegion
                {
                    Kind = RegionKind.Plane,
                    OwnerInstanceId = ownerInstanceId,
                    RegionId = MeshAnalysisCache.MakeRegionId(meshKey, paramsHash, RegionKind.Plane, planeIndex),
                    FaceIndices = bestInliers.ToArray(),
                    NormalWorld = bestNormal,
                    PointWorld = weighted,
                    AreaApprox = area,
                    FaceCount = bestInliers.Count,
                    WorldBounds = ComputeFaceBounds(bestInliers, verts, tris, localToWorld),
                    OrientationLabel = ClassifyOrientation(bestNormal),
                };
                result.Add(plane);
                planeIndex++;
            }

            // 3. Heuristic classification — needs the full plane set in hand
            //    because labels depend on relative position (lowest Y → ground).
            ClassifyAll(result, totalBounds);
            return result;
        }

        private static (List<int> inliers, Vector3 normal, Vector3 point) FindBestPlane(
            Vector3[] centroids, Vector3[] normals, float[] areas, bool[] assigned,
            int remaining, float cosAngleTol, float distTol, int iterations, System.Random rng)
        {
            List<int> best = null;
            Vector3 bestN = Vector3.up;
            Vector3 bestP = Vector3.zero;

            // Build an index of unassigned triangle indices to sample from.
            var unassigned = new int[remaining];
            int k = 0;
            for (int t = 0; t < assigned.Length; t++) if (!assigned[t]) unassigned[k++] = t;

            for (int iter = 0; iter < iterations; iter++)
            {
                // Single-tri "seed" RANSAC: pick one unassigned triangle, use
                // its normal and centroid as the candidate plane. This is the
                // cheap variant — good enough for mesh-aligned planes (i.e.
                // most architectural / urban geometry). Sampling 3 tris would
                // be more robust for noisy point-clouds, but our data is
                // exact triangles already.
                int seed = unassigned[rng.Next(unassigned.Length)];
                var n = normals[seed];
                var p = centroids[seed];

                // Gather inliers.
                var inliers = new List<int>(64);
                for (int i = 0; i < unassigned.Length; i++)
                {
                    int t = unassigned[i];
                    if (Vector3.Dot(normals[t], n) < cosAngleTol) continue;
                    float dist = Mathf.Abs(Vector3.Dot(centroids[t] - p, n));
                    if (dist > distTol) continue;
                    inliers.Add(t);
                }
                if (best == null || inliers.Count > best.Count)
                {
                    best = inliers;
                    bestN = n;
                    bestP = p;
                }
            }
            return (best, bestN, bestP);
        }

        private static Bounds ComputeFaceBounds(List<int> faces, Vector3[] verts, int[] tris, Matrix4x4 ltw)
        {
            if (faces.Count == 0) return new Bounds();
            var first = ltw.MultiplyPoint3x4(verts[tris[3 * faces[0]]]);
            var b = new Bounds(first, Vector3.zero);
            foreach (var f in faces)
            {
                b.Encapsulate(ltw.MultiplyPoint3x4(verts[tris[3 * f + 0]]));
                b.Encapsulate(ltw.MultiplyPoint3x4(verts[tris[3 * f + 1]]));
                b.Encapsulate(ltw.MultiplyPoint3x4(verts[tris[3 * f + 2]]));
            }
            return b;
        }

        private static string ClassifyOrientation(Vector3 worldNormal)
        {
            // Angle from world up. > 60° from up AND > 60° from down → vertical.
            const float horizThresholdDeg = 30f;
            float cosHoriz = Mathf.Cos(horizThresholdDeg * Mathf.Deg2Rad);
            float dotUp = Vector3.Dot(worldNormal.normalized, Vector3.up);
            if (dotUp > cosHoriz) return "horizontal_up";
            if (dotUp < -cosHoriz) return "horizontal_down";
            // Vertical: encode the bearing in the XZ plane as a degree int 0..359
            // (0 = +X, 90 = +Z, 180 = -X, 270 = -Z). Gives the LLM something
            // to filter on for "north wall" vs "east wall".
            float bearing = Mathf.Atan2(worldNormal.z, worldNormal.x) * Mathf.Rad2Deg;
            if (bearing < 0) bearing += 360f;
            return $"vertical:{Mathf.RoundToInt(bearing)}";
        }

        /// Heuristic classification once we know all detected planes. Pure
        /// geometry — no ML, no asset name guessing. Goal: pre-label the
        /// obvious so the LLM does fewer round-trips. The LLM can always
        /// override based on visual inspection.
        private static void ClassifyAll(List<PlaneRegion> planes, Bounds totalBounds)
        {
            if (planes.Count == 0) return;

            // Find the largest horizontal_up plane → ground candidate by area.
            // Anything else horizontal_up at intermediate Y → platform.
            float groundY = float.PositiveInfinity;
            float largestUpArea = 0f;
            PlaneRegion ground = null;
            foreach (var pl in planes)
            {
                if (pl.OrientationLabel == "horizontal_up" && pl.AreaApprox > largestUpArea)
                {
                    largestUpArea = pl.AreaApprox;
                    ground = pl;
                    groundY = pl.PointWorld.y;
                }
            }

            float boundsHeight = Mathf.Max(0.01f, totalBounds.size.y);

            // Median-ish wall area threshold: avg vertical area.
            float verticalAreaSum = 0f;
            int verticalCount = 0;
            foreach (var pl in planes)
                if (pl.OrientationLabel.StartsWith("vertical")) { verticalAreaSum += pl.AreaApprox; verticalCount++; }
            float avgVerticalArea = verticalCount > 0 ? verticalAreaSum / verticalCount : 0f;

            foreach (var pl in planes)
            {
                if (pl == ground)
                {
                    pl.Classification = "ground_candidate";
                    continue;
                }
                if (pl.OrientationLabel == "horizontal_up")
                {
                    // Anything significantly above ground but well below top
                    // → platform/sidewalk. Anything near the top → roof_top.
                    float heightAboveGround = pl.PointWorld.y - groundY;
                    float relHeight = heightAboveGround / boundsHeight;
                    if (relHeight > 0.7f) pl.Classification = "roof_top_candidate";
                    else if (relHeight > 0.02f) pl.Classification = "platform_candidate";
                    else pl.Classification = "ground_candidate"; // small offset, still ground-like
                    continue;
                }
                if (pl.OrientationLabel == "horizontal_down")
                {
                    pl.Classification = "ceiling_candidate";
                    continue;
                }
                if (pl.OrientationLabel.StartsWith("vertical"))
                {
                    // Big vertical = wall; small vertical = door/window.
                    pl.Classification = pl.AreaApprox >= avgVerticalArea * 0.6f
                        ? "wall_candidate"
                        : "door_or_window_candidate";
                    continue;
                }
                pl.Classification = "other";
            }
        }
    }
}
