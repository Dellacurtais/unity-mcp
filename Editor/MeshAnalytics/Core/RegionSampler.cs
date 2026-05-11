using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Generates 3D sample points on the triangles of a region. The main use
    /// case is "give me places to put waypoints / lamp posts on this
    /// sidewalk-top plane" — the LLM gets back world-space coords + per-point
    /// surface normals ready to drive instantiate_prefab.
    ///
    /// Three distributions:
    ///   - uniform_random : area-weighted random per triangle (fastest, noisy)
    ///   - grid           : raster on each triangle's bounding rect projected
    ///                      to the dominant plane (cheap regular pattern)
    ///   - poisson_disk   : rejection-sample uniform_random with a min-distance
    ///                      gate. Simpler than Bridson but adequate for ≤2000
    ///                      points on a sidewalk-sized region.
    ///
    /// The `inset` parameter shrinks the sampling region away from the
    /// region's outer edges, keeping points off the kerb. Implemented by
    /// rejecting samples whose distance to the WorldBounds boundary is below
    /// the inset value — coarse but fast.
    public static class RegionSampler
    {
        public enum Distribution { UniformRandom, Grid, PoissonDisk }

        public sealed class Params
        {
            public int? Count;
            public float? Spacing;            // metres; mutually exclusive with Count
            public float Inset = 0f;          // metres
            public Distribution Distribution = Distribution.PoissonDisk;
            public int Seed = 1234;
            public int MaxSamples = 5000;     // safety cap
        }

        public sealed class Sample
        {
            public Vector3 Position;
            public Vector3 Normal;
        }

        public static List<Sample> Sample(MeshRegion region, Mesh mesh, Matrix4x4 localToWorld, Params p)
        {
            var result = new List<Sample>();
            if (region == null || mesh == null || region.FaceIndices == null || region.FaceIndices.Length == 0)
                return result;

            // Resolve target count.
            float regionArea = ComputeAreaWorld(region, mesh, localToWorld, out var triBuf);
            int targetCount = ResolveCount(p, regionArea);
            if (targetCount <= 0 || triBuf == null) return result;
            targetCount = Mathf.Min(targetCount, p.MaxSamples);

            switch (p.Distribution)
            {
                case Distribution.UniformRandom:
                    UniformRandom(region, triBuf, targetCount, p, result);
                    break;
                case Distribution.Grid:
                    GridOnDominantPlane(region, triBuf, p, result);
                    break;
                case Distribution.PoissonDisk:
                default:
                    PoissonDisk(region, triBuf, targetCount, p, result);
                    break;
            }
            return result;
        }

        // ---- internals ----

        private struct TriW
        {
            public Vector3 A, B, C;
            public Vector3 Normal;
            public float Area;
            public float CumArea;
        }

        private static float ComputeAreaWorld(MeshRegion region, Mesh mesh, Matrix4x4 ltw, out TriW[] tris)
        {
            var verts = mesh.vertices;
            var idx = mesh.triangles;
            var faces = region.FaceIndices;
            tris = new TriW[faces.Length];
            float cum = 0f;
            for (int i = 0; i < faces.Length; i++)
            {
                int f = faces[i];
                var a = ltw.MultiplyPoint3x4(verts[idx[3 * f + 0]]);
                var b = ltw.MultiplyPoint3x4(verts[idx[3 * f + 1]]);
                var c = ltw.MultiplyPoint3x4(verts[idx[3 * f + 2]]);
                var cross = Vector3.Cross(b - a, c - a);
                float area = 0.5f * cross.magnitude;
                tris[i] = new TriW
                {
                    A = a, B = b, C = c,
                    Area = area,
                    Normal = area > 1e-9f ? cross / (2f * area) : Vector3.up,
                };
                cum += area;
                tris[i].CumArea = cum;
            }
            return cum;
        }

        private static int ResolveCount(Params p, float areaWorld)
        {
            if (p.Count.HasValue) return p.Count.Value;
            if (p.Spacing.HasValue && p.Spacing.Value > 1e-4f)
                return Mathf.Max(1, Mathf.RoundToInt(areaWorld / (p.Spacing.Value * p.Spacing.Value)));
            return 32;
        }

        private static Vector3 SamplePointOnTri(TriW t, System.Random rng)
        {
            float u = (float)rng.NextDouble();
            float v = (float)rng.NextDouble();
            if (u + v > 1f) { u = 1f - u; v = 1f - v; }
            return t.A + u * (t.B - t.A) + v * (t.C - t.A);
        }

        private static int PickTriByArea(TriW[] tris, float totalArea, System.Random rng)
        {
            float r = (float)rng.NextDouble() * totalArea;
            // Linear scan — fine for region sizes we care about. Could be
            // binary search if regions grow into 100k triangles.
            for (int i = 0; i < tris.Length; i++) if (tris[i].CumArea >= r) return i;
            return tris.Length - 1;
        }

        private static bool PassesInset(MeshRegion region, Vector3 p, float inset)
        {
            if (inset <= 0f) return true;
            // Distance from p to each AABB face. Reject if any is below inset.
            var b = region.WorldBounds;
            if (p.x - b.min.x < inset) return false;
            if (b.max.x - p.x < inset) return false;
            if (p.z - b.min.z < inset) return false;
            if (b.max.z - p.z < inset) return false;
            // Y is intentionally not insetted — for thin regions like the top
            // of a sidewalk, the AABB is essentially planar in Y.
            return true;
        }

        private static void UniformRandom(MeshRegion region, TriW[] tris, int n, Params p, List<Sample> outList)
        {
            var rng = new System.Random(p.Seed);
            float totalArea = tris[tris.Length - 1].CumArea;
            int attempts = 0;
            int maxAttempts = n * 8;
            while (outList.Count < n && attempts < maxAttempts)
            {
                attempts++;
                int ti = PickTriByArea(tris, totalArea, rng);
                var pt = SamplePointOnTri(tris[ti], rng);
                if (!PassesInset(region, pt, p.Inset)) continue;
                outList.Add(new Sample { Position = pt, Normal = tris[ti].Normal });
            }
        }

        /// Plain rejection-sampling Poisson-disk. For each candidate, reject
        /// if any accepted point is within `spacing` metres. Spacing comes
        /// from Params.Spacing when set; otherwise derived from area/count
        /// (sqrt(area/count) is roughly the spacing that produces `count`
        /// uniform points). Adequate for the typical scene-author scale.
        private static void PoissonDisk(MeshRegion region, TriW[] tris, int target, Params p, List<Sample> outList)
        {
            var rng = new System.Random(p.Seed);
            float totalArea = tris[tris.Length - 1].CumArea;
            float spacing = p.Spacing ?? Mathf.Sqrt(totalArea / Mathf.Max(1, target));
            float minDistSq = spacing * spacing;

            int attempts = 0;
            int maxAttempts = target * 30;
            while (outList.Count < target && attempts < maxAttempts)
            {
                attempts++;
                int ti = PickTriByArea(tris, totalArea, rng);
                var pt = SamplePointOnTri(tris[ti], rng);
                if (!PassesInset(region, pt, p.Inset)) continue;

                bool ok = true;
                for (int k = 0; k < outList.Count; k++)
                {
                    if ((outList[k].Position - pt).sqrMagnitude < minDistSq) { ok = false; break; }
                }
                if (ok) outList.Add(new Sample { Position = pt, Normal = tris[ti].Normal });
            }
        }

        /// Coarse grid sampler. Projects the region's WorldBounds onto the
        /// XZ plane (sufficient for the "horizontal sidewalk" case), steps
        /// at `spacing` metres, accepts a cell if its centre's downward
        /// raycast against the region's triangles hits the geometry. Hits
        /// inherit the triangle normal.
        private static void GridOnDominantPlane(MeshRegion region, TriW[] tris, Params p, List<Sample> outList)
        {
            var b = region.WorldBounds;
            float spacing = p.Spacing ?? Mathf.Max(0.1f, Mathf.Sqrt(b.size.x * b.size.z / 64f));

            for (float x = b.min.x + spacing * 0.5f; x <= b.max.x; x += spacing)
            {
                for (float z = b.min.z + spacing * 0.5f; z <= b.max.z; z += spacing)
                {
                    var probeOrigin = new Vector3(x, b.max.y + 1f, z);
                    if (RaycastDown(tris, probeOrigin, out var hitPt, out var hitN))
                    {
                        if (!PassesInset(region, hitPt, p.Inset)) continue;
                        outList.Add(new Sample { Position = hitPt, Normal = hitN });
                        if (outList.Count >= p.MaxSamples) return;
                    }
                }
            }
        }

        /// Cheap ray-vs-triangles for the grid sampler. Tests every triangle;
        /// returns the closest hit. Loop is O(faces) per cell — fine for the
        /// few hundred cells a sidewalk produces.
        private static bool RaycastDown(TriW[] tris, Vector3 origin, out Vector3 hitPt, out Vector3 hitN)
        {
            hitPt = default; hitN = default;
            float closestT = float.PositiveInfinity;
            var dir = Vector3.down;
            for (int i = 0; i < tris.Length; i++)
            {
                if (IntersectTri(origin, dir, tris[i].A, tris[i].B, tris[i].C, out float tHit, out var pt))
                {
                    if (tHit < closestT)
                    {
                        closestT = tHit;
                        hitPt = pt;
                        hitN = tris[i].Normal;
                    }
                }
            }
            return !float.IsPositiveInfinity(closestT);
        }

        /// Möller–Trumbore. Returns true if `origin + t*dir` (t > 0) hits the
        /// triangle.
        private static bool IntersectTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c,
            out float t, out Vector3 hit)
        {
            t = 0; hit = default;
            var e1 = b - a;
            var e2 = c - a;
            var pvec = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pvec);
            if (Mathf.Abs(det) < 1e-8f) return false;
            float inv = 1f / det;
            var tvec = o - a;
            float u = Vector3.Dot(tvec, pvec) * inv;
            if (u < 0 || u > 1) return false;
            var qvec = Vector3.Cross(tvec, e1);
            float v = Vector3.Dot(d, qvec) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, qvec) * inv;
            if (t <= 0) return false;
            hit = o + d * t;
            return true;
        }
    }
}
