using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Session-scoped cache of mesh analysis results keyed by
    /// (meshGuid|meshInstanceID, paramsHash). RegionIds are stable for the
    /// lifetime of an entry; a domain reload or explicit invalidation wipes
    /// everything and the next call recomputes with fresh ids.
    ///
    /// Lookup by regionId is the main consumer pattern (render_region_highlight,
    /// sample_points_on_region) — we maintain a reverse index so the LLM can
    /// pass a regionId verbatim without remembering which detector produced it.
    public static class MeshAnalysisCache
    {
        private static readonly Dictionary<string, AnalysisResult> _byKey = new();
        private static readonly Dictionary<string, AnalysisResult> _byRegionId = new();

        static MeshAnalysisCache()
        {
            // The static ctor runs again on every domain reload with a clean
            // dictionary, so this subscription is just defensive (clears the
            // cache before reload in case anything else holds onto refs).
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        public static string MakeKey(string meshKey, string paramsHash)
            => $"{meshKey}|{paramsHash}";

        public static bool TryGet(string key, out AnalysisResult result)
            => _byKey.TryGetValue(key, out result);

        /// Memory-then-disk lookup. On memory miss, attempts to load the
        /// disk sidecar at (meshKey, paramsHash). The cached entry is only
        /// reused if its OwnerLocalToWorld matches the runtime instance's
        /// transform (otherwise world-space data would be wrong).
        public static bool TryGetWithFallback(string meshKey, string paramsHash,
            Matrix4x4 expectedXf, out AnalysisResult result)
        {
            string key = MakeKey(meshKey, paramsHash);
            if (_byKey.TryGetValue(key, out result))
            {
                // In-memory entry may also be stale if the same mesh was
                // re-cached under a different instance; honour the same
                // transform check for safety.
                const float eps = 1e-4f;
                bool match = true;
                for (int i = 0; i < 16 && match; i++)
                    if (Mathf.Abs(result.OwnerLocalToWorld[i] - expectedXf[i]) > eps) match = false;
                if (match) return true;
                result = null;
            }
            if (MeshAnalysisDiskCache.TryLoad(meshKey, paramsHash, expectedXf, out result))
            {
                // Promote disk hit into memory for the rest of the session.
                Store(key, result);
                return true;
            }
            return false;
        }

        /// Enumerates every materialised bundle in the cache. Used by
        /// `MeshPicker` to find which regions (from any prior detector call)
        /// contain a given triangle index.
        public static IEnumerable<AnalysisResult> AllResults() => _byKey.Values;

        public static void Store(string key, AnalysisResult result)
        {
            _byKey[key] = result;
            foreach (var r in result.Submeshes) _byRegionId[r.RegionId] = result;
            foreach (var r in result.EdgeChains) _byRegionId[r.RegionId] = result;
            foreach (var r in result.Planes) _byRegionId[r.RegionId] = result;
            foreach (var r in result.SmoothPatches) _byRegionId[r.RegionId] = result;
            foreach (var r in result.ParallelGroups) _byRegionId[r.RegionId] = result;
        }

        /// Convenience: in-memory Store + write to the disk sidecar so the
        /// next session / next domain reload starts warm. Tools call this
        /// instead of Store when they want the result to survive.
        public static void StoreAndPersist(string meshKey, string paramsHash, AnalysisResult result)
        {
            Store(MakeKey(meshKey, paramsHash), result);
            MeshAnalysisDiskCache.Save(meshKey, paramsHash, result);
        }

        /// Look up the bundle that owns a given regionId. Returns null if the
        /// id is unknown (cache was cleared, or the id was never produced by
        /// any detector). Callers must handle null with a clear error.
        public static MeshRegion FindRegion(string regionId, out AnalysisResult owner)
        {
            if (_byRegionId.TryGetValue(regionId, out owner))
                return owner.FindById(regionId);
            owner = null;
            return null;
        }

        public static void Clear()
        {
            _byKey.Clear();
            _byRegionId.Clear();
        }

        /// Build a stable region id of the form
        /// "<meshKey>@<paramsHash>/<kind>/<index>". Including the paramsHash
        /// disambiguates re-runs of the same detector with different settings
        /// — without it, the second run's regionIds collide with the first's
        /// in the lookup index. MeshKey is the asset GUID when available;
        /// otherwise the runtime mesh.GetInstanceID() (still stable within a
        /// session). The kind segment lets the LLM tell submesh/edge/plane
        /// regions apart at a glance.
        public static string MakeRegionId(string meshKey, string paramsHash, RegionKind kind, int index)
            => $"{meshKey}@{paramsHash}/{KindToken(kind)}/{index}";

        private static string KindToken(RegionKind k) => k switch
        {
            RegionKind.Submesh => "submesh",
            RegionKind.EdgeChain => "edge",
            RegionKind.Plane => "plane",
            RegionKind.Smooth => "smooth",
            RegionKind.ParallelGroup => "parallel",
            _ => "region",
        };
    }
}
