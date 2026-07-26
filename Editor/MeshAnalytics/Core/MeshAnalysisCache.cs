using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
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

        public static AnalysisResult CreateResult(string meshKey, string analysisKey,
            GameObject owner, Matrix4x4 localToWorld)
        {
            var result = new AnalysisResult
            {
                MeshGuid = meshKey,
                AnalysisKey = analysisKey,
                OwnerInstanceId = owner != null ? owner.GetInstanceID() : 0,
                OwnerTarget = ToolTargetRef.Capture(owner),
                OwnerLocalToWorld = localToWorld,
            };
            return result;
        }

        public static bool TryGet(string key, out AnalysisResult result)
            => _byKey.TryGetValue(key, out result);

        /// Memory-then-disk lookup. On memory miss, attempts to load the
        /// disk sidecar at (meshKey, paramsHash). The cached entry is only
        /// reused if its OwnerLocalToWorld matches the runtime instance's
        /// transform (otherwise world-space data would be wrong).
        public static bool TryGetWithFallback(string analysisKey, string paramsHash,
            GameObject owner, Matrix4x4 expectedXf, out AnalysisResult result)
        {
            string key = MakeKey(analysisKey, paramsHash);
            if (_byKey.TryGetValue(key, out result))
            {
                if (Approximately(result.OwnerLocalToWorld, expectedXf))
                {
                    BindOwner(result, analysisKey, owner);
                    return true;
                }
                RemoveEntry(key, result);
                result = null;
            }
            if (!analysisKey.StartsWith("runtime:", System.StringComparison.Ordinal)
                && MeshAnalysisDiskCache.TryLoad(analysisKey, paramsHash, expectedXf, out result))
            {
                BindOwner(result, analysisKey, owner);
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
            if (_byKey.TryGetValue(key, out var previous) && !ReferenceEquals(previous, result))
                RemoveRegions(previous);
            _byKey[key] = result;
            AddRegions(result);
        }

        /// Convenience: in-memory Store + write to the disk sidecar so the
        /// next session / next domain reload starts warm. Tools call this
        /// instead of Store when they want the result to survive.
        public static void StoreAndPersist(string analysisKey, string paramsHash, AnalysisResult result)
        {
            Store(MakeKey(analysisKey, paramsHash), result);
            if (!analysisKey.StartsWith("runtime:", System.StringComparison.Ordinal))
                MeshAnalysisDiskCache.Save(analysisKey, paramsHash, result);
        }

        public static GameObject ResolveOwner(AnalysisResult result, out string error)
        {
            error = null;
            if (result == null) { error = "analysis result is missing"; return null; }
            if (result.OwnerTarget != null)
            {
                var resolved = ToolTargetResolver.Resolve(result.OwnerTarget, out error);
                var gameObject = resolved as GameObject ?? (resolved as Component)?.gameObject;
                if (gameObject != null)
                {
                    BindOwner(result, result.AnalysisKey, gameObject);
                    return gameObject;
                }
            }
            var fallback = MeshResolver.ResolveGameObject(result.OwnerInstanceId);
            if (fallback != null)
            {
                BindOwner(result, result.AnalysisKey, fallback);
                return fallback;
            }
            if (error == null) error = "analysis owner no longer resolves; rerun the producing mesh tool";
            return null;
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

        private static void BindOwner(AnalysisResult result, string analysisKey, GameObject owner)
        {
            if (result == null || owner == null) return;
            result.AnalysisKey = analysisKey;
            result.OwnerInstanceId = owner.GetInstanceID();
            result.OwnerTarget = ToolTargetRef.Capture(owner);
            foreach (var region in Regions(result)) region.OwnerInstanceId = result.OwnerInstanceId;
        }

        private static bool Approximately(Matrix4x4 left, Matrix4x4 right)
        {
            const float epsilon = 1e-4f;
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(left[i] - right[i]) > epsilon) return false;
            return true;
        }

        private static void RemoveEntry(string key, AnalysisResult result)
        {
            _byKey.Remove(key);
            RemoveRegions(result);
        }

        private static void AddRegions(AnalysisResult result)
        {
            foreach (var region in Regions(result)) _byRegionId[region.RegionId] = result;
        }

        private static void RemoveRegions(AnalysisResult result)
        {
            foreach (var region in Regions(result))
                if (_byRegionId.TryGetValue(region.RegionId, out var owner) && ReferenceEquals(owner, result))
                    _byRegionId.Remove(region.RegionId);
        }

        private static IEnumerable<MeshRegion> Regions(AnalysisResult result)
        {
            foreach (var region in result.Submeshes) yield return region;
            foreach (var region in result.EdgeChains) yield return region;
            foreach (var region in result.Planes) yield return region;
            foreach (var region in result.SmoothPatches) yield return region;
            foreach (var region in result.ParallelGroups) yield return region;
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
