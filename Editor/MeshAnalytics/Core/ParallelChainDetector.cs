using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Clusters edge chains that run approximately parallel with roughly
    /// equal spacing — the structural signature of trilhos, grades, escadas,
    /// janelas em fileira. Operates on chains produced by SharpEdgeDetector
    /// (or any compatible list).
    ///
    /// Algorithm:
    ///   1. Restrict to non-loop chains (loops aren't "linear" in a useful
    ///      sense for parallelism — a sidewalk perimeter is a loop and we
    ///      don't want to call it "parallel to itself").
    ///   2. For each chain compute a dominant direction (endpoint-to-endpoint
    ///      unit vector) and centroid.
    ///   3. Group chains whose directions are within `angleTolerance`. Two
    ///      chains heading 0° and 180° are treated as the same direction
    ///      (anti-parallel) — a single rail observed from either end.
    ///   4. Within each direction-cluster, project centroids onto the
    ///      perpendicular plane, sort, and look for runs of ≥`minMembers`
    ///      with successive spacing within `spacingTolerance` of the mean.
    public static class ParallelChainDetector
    {
        public sealed class Params
        {
            public float DirectionAngleToleranceDeg = 10f;
            public float SpacingTolerance = 0.25f;     // relative — stddev/mean must be below this
            public float MinSpacing = 0.05f;           // metres; reject coincident chains
            public int MinMembers = 2;
            public int MaxGroups = 16;
            public float MinChainLengthRatio = 0.5f;   // member chain length / longest in group
        }

        public static List<ParallelGroupRegion> Detect(List<EdgeChainRegion> chains,
            string meshKey, string paramsHash, int ownerInstanceId, Params p)
        {
            var result = new List<ParallelGroupRegion>();
            if (chains == null || chains.Count < p.MinMembers) return result;

            // 1. Reduce each chain to (id, direction, centroid, length).
            var probes = new List<Probe>();
            foreach (var c in chains)
            {
                if (c.IsLoop) continue;
                if (c.VertexPositionsWorld == null || c.VertexPositionsWorld.Length < 2) continue;
                var first = c.VertexPositionsWorld[0];
                var last = c.VertexPositionsWorld[c.VertexPositionsWorld.Length - 1];
                var dir = (last - first);
                float len = dir.magnitude;
                if (len < 1e-4f) continue;
                dir /= len;
                var centroid = Vector3.zero;
                foreach (var v in c.VertexPositionsWorld) centroid += v;
                centroid /= c.VertexPositionsWorld.Length;
                probes.Add(new Probe { Region = c, Direction = dir, Centroid = centroid, Length = len });
            }
            if (probes.Count < p.MinMembers) return result;

            // 2. Greedy clustering by direction (treat ±dir as the same axis).
            float cosTol = Mathf.Cos(p.DirectionAngleToleranceDeg * Mathf.Deg2Rad);
            var clusters = new List<List<Probe>>();
            var assigned = new bool[probes.Count];
            for (int i = 0; i < probes.Count; i++)
            {
                if (assigned[i]) continue;
                var cluster = new List<Probe> { probes[i] };
                assigned[i] = true;
                for (int j = i + 1; j < probes.Count; j++)
                {
                    if (assigned[j]) continue;
                    float dot = Mathf.Abs(Vector3.Dot(probes[i].Direction, probes[j].Direction));
                    if (dot >= cosTol) { cluster.Add(probes[j]); assigned[j] = true; }
                }
                if (cluster.Count >= p.MinMembers) clusters.Add(cluster);
            }

            // 3. For each cluster, find an axis perpendicular to its mean
            //    direction and check spacing regularity along it.
            int groupIndex = 0;
            foreach (var cluster in clusters)
            {
                // Filter cluster by length similarity to the longest member
                // (avoid grouping a 10m rail with a 30cm bolt).
                float maxLen = 0f;
                foreach (var pr in cluster) if (pr.Length > maxLen) maxLen = pr.Length;
                var lenFiltered = new List<Probe>();
                foreach (var pr in cluster)
                    if (pr.Length / maxLen >= p.MinChainLengthRatio) lenFiltered.Add(pr);
                if (lenFiltered.Count < p.MinMembers) continue;

                // Mean direction (flip antiparallel members to match the seed).
                var seedDir = lenFiltered[0].Direction;
                var meanDir = Vector3.zero;
                foreach (var pr in lenFiltered)
                {
                    var d = Vector3.Dot(pr.Direction, seedDir) >= 0 ? pr.Direction : -pr.Direction;
                    meanDir += d;
                }
                meanDir = (meanDir / lenFiltered.Count).normalized;

                // Pick a perpendicular axis. Prefer world-up; fall back to
                // world-right if the chain direction IS approximately up.
                var perp = Vector3.Cross(meanDir, Vector3.up);
                if (perp.sqrMagnitude < 1e-4f) perp = Vector3.Cross(meanDir, Vector3.right);
                perp.Normalize();

                // Project centroids onto perp axis (signed scalar) and sort.
                var entries = new List<(Probe pr, float coord)>();
                foreach (var pr in lenFiltered)
                    entries.Add((pr, Vector3.Dot(pr.Centroid, perp)));
                entries.Sort((a, b) => a.coord.CompareTo(b.coord));

                // Compute successive deltas; if their stddev is small relative
                // to mean (and mean >= MinSpacing), accept as a parallel run.
                int n = entries.Count;
                if (n < p.MinMembers) continue;
                float meanDelta = 0f;
                for (int k = 1; k < n; k++) meanDelta += entries[k].coord - entries[k - 1].coord;
                meanDelta /= (n - 1);
                if (meanDelta < p.MinSpacing) continue;

                float var = 0f;
                for (int k = 1; k < n; k++)
                {
                    var d = (entries[k].coord - entries[k - 1].coord) - meanDelta;
                    var += d * d;
                }
                float stddev = Mathf.Sqrt(var / Mathf.Max(1, n - 1));
                if (stddev / meanDelta > p.SpacingTolerance) continue;

                // 4. Commit the group.
                var childIds = new string[n];
                var faceUnion = new HashSet<int>();
                var bounds = entries[0].pr.Region.WorldBounds;
                for (int k = 0; k < n; k++)
                {
                    childIds[k] = entries[k].pr.Region.RegionId;
                    bounds.Encapsulate(entries[k].pr.Region.WorldBounds);
                    if (entries[k].pr.Region.FaceIndices != null)
                        foreach (var f in entries[k].pr.Region.FaceIndices) faceUnion.Add(f);
                }
                var faceArr = new int[faceUnion.Count];
                faceUnion.CopyTo(faceArr);

                var group = new ParallelGroupRegion
                {
                    Kind = RegionKind.ParallelGroup,
                    OwnerInstanceId = ownerInstanceId,
                    FaceIndices = faceArr,
                    WorldBounds = bounds,
                    ChildChainRegionIds = childIds,
                    DirectionWorld = meanDir,
                    MeanSpacing = meanDelta,
                    SpacingStdDev = stddev,
                    InstanceCount = n,
                };
                result.Add(group);
                if (result.Count >= p.MaxGroups) break;
                groupIndex++;
            }

            // Sort by descending instance count then mean spacing; stamp ids.
            result.Sort((a, b) =>
            {
                int byCount = b.InstanceCount.CompareTo(a.InstanceCount);
                return byCount != 0 ? byCount : a.MeanSpacing.CompareTo(b.MeanSpacing);
            });
            for (int i = 0; i < result.Count; i++)
                result[i].RegionId = MeshAnalysisCache.MakeRegionId(meshKey, paramsHash, RegionKind.ParallelGroup, i);

            return result;
        }

        private struct Probe
        {
            public EdgeChainRegion Region;
            public Vector3 Direction;
            public Vector3 Centroid;
            public float Length;
        }
    }
}
