using System.Collections.Generic;
using UnityEditor;

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

        public static void Store(string key, AnalysisResult result)
        {
            _byKey[key] = result;
            foreach (var r in result.Submeshes) _byRegionId[r.RegionId] = result;
            foreach (var r in result.EdgeChains) _byRegionId[r.RegionId] = result;
            foreach (var r in result.Planes) _byRegionId[r.RegionId] = result;
            foreach (var r in result.SmoothPatches) _byRegionId[r.RegionId] = result;
            foreach (var r in result.ParallelGroups) _byRegionId[r.RegionId] = result;
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
