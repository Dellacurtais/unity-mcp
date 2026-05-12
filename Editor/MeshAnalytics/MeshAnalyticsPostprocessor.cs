using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics
{
    /// Pre-warms the on-disk mesh analytics cache when a model asset is
    /// (re-)imported. Each unique Mesh sub-asset is analysed once with
    /// default params at identity transform; results land under
    /// `<project>/Library/MeshAnalytics/<guid>/...`. A runtime call on a
    /// GameObject instanced at origin then hits the disk cache instead of
    /// recomputing — instant response for the LLM.
    ///
    /// Opt-out: set `EnablePreWarm = false` at editor load (e.g. from a
    /// future settings UI) to skip this hook. Heavy meshes
    /// (`triangleCount > TriangleCeiling`) are also skipped to avoid
    /// blocking the import for seconds.
    public sealed class MeshAnalyticsPostprocessor : AssetPostprocessor
    {
        public static bool EnablePreWarm = true;
        public const long TriangleCeiling = 200_000;     // skip heavier meshes

        /// Unity calls this once per imported model after the prefab tree
        /// is constructed. The `g` GameObject is the import root — we walk
        /// its children, dedupe Mesh references, and run a default battery
        /// of detectors on each.
        public void OnPostprocessModel(GameObject g)
        {
            if (!EnablePreWarm) return;
            if (g == null) return;

            var seen = new HashSet<Mesh>();
            foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null) seen.Add(mf.sharedMesh);
            foreach (var smr in g.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.sharedMesh != null) seen.Add(smr.sharedMesh);

            foreach (var mesh in seen)
            {
                if (mesh == null) continue;
                if (!mesh.isReadable) continue;
                long tris = TriangleCount(mesh);
                if (tris > TriangleCeiling) continue;
                PrewarmOne(mesh);
            }
        }

        private static void PrewarmOne(Mesh mesh)
        {
            // We can't compute world bounds without a Renderer + Transform,
            // so we use the asset path as the meshKey and identity as the
            // canonical transform. That matches what a scene instance
            // dropped at origin will look up at runtime.
            string meshKey = MeshResolver.MakeMeshKey(mesh);
            // Wipe any old disk entries for this mesh — they're stale after
            // a re-import (vertex layout, submesh count, materials may have
            // changed).
            MeshAnalysisDiskCache.InvalidateMesh(meshKey);

            var identity = Matrix4x4.identity;
            int fakeInstanceId = 0;

            // 1. Submeshes — cheap, structural metadata.
            PrewarmSubmeshes(mesh, identity, meshKey, fakeInstanceId);

            // 2. Sharp edges — defaults.
            PrewarmSharpEdges(mesh, identity, meshKey, fakeInstanceId);

            // 3. Planes — defaults.
            PrewarmPlanes(mesh, identity, meshKey, fakeInstanceId);

            // Smooth regions and parallel chains are deferred — the first
            // benefits less from pre-compute (params often tuned per asset)
            // and parallel chains depends on edge chains being in cache,
            // which they now are.
        }

        private static void PrewarmSubmeshes(Mesh mesh, Matrix4x4 ltw, string meshKey, int instanceId)
        {
            const string paramsHash = "submesh";
            var bundle = new AnalysisResult
            {
                MeshGuid = meshKey, OwnerInstanceId = instanceId, OwnerLocalToWorld = ltw,
            };
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var sub = mesh.GetSubMesh(i);
                int triStart = sub.indexStart / 3;
                int triCount = sub.indexCount / 3;
                var faceIdx = new int[triCount];
                for (int t = 0; t < triCount; t++) faceIdx[t] = triStart + t;
                bundle.Submeshes.Add(new SubmeshRegion
                {
                    Kind = RegionKind.Submesh,
                    OwnerInstanceId = instanceId,
                    RegionId = MeshAnalysisCache.MakeRegionId(meshKey, paramsHash, RegionKind.Submesh, i),
                    SubmeshIndex = i,
                    MaterialName = "(prewarmed)",
                    TriangleCount = triCount,
                    LocalBounds = sub.bounds,
                    WorldBounds = sub.bounds,    // identity transform → world == local
                    Topology = sub.topology,
                    FaceIndices = faceIdx,
                });
            }
            MeshAnalysisCache.StoreAndPersist(meshKey, paramsHash, bundle);
        }

        private static void PrewarmSharpEdges(Mesh mesh, Matrix4x4 ltw, string meshKey, int instanceId)
        {
            var p = new SharpEdgeDetector.Params();
            string paramsHash = $"edge:{p.DihedralAngleMinDeg:F2}:{p.MinChainLength:F3}:{p.MaxChains}";
            var bundle = new AnalysisResult
            {
                MeshGuid = meshKey, OwnerInstanceId = instanceId, OwnerLocalToWorld = ltw,
            };
            bundle.EdgeChains.AddRange(SharpEdgeDetector.Detect(mesh, ltw, meshKey, paramsHash, instanceId, p));
            MeshAnalysisCache.StoreAndPersist(meshKey, paramsHash, bundle);
        }

        private static void PrewarmPlanes(Mesh mesh, Matrix4x4 ltw, string meshKey, int instanceId)
        {
            var p = new PlaneDetector.Params();
            string paramsHash = $"plane:{p.MinFacesPerPlane}:{p.AngleToleranceDeg:F2}:{p.DistanceTolerance:F4}:{p.MaxPlanes}";
            var bundle = new AnalysisResult
            {
                MeshGuid = meshKey, OwnerInstanceId = instanceId, OwnerLocalToWorld = ltw,
            };
            bundle.Planes.AddRange(PlaneDetector.Detect(mesh, ltw, meshKey, paramsHash, instanceId, p));
            MeshAnalysisCache.StoreAndPersist(meshKey, paramsHash, bundle);
        }

        private static long TriangleCount(Mesh mesh)
        {
            long tris = 0;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
                tris += mesh.GetIndexCount(sm) / 3;
            return tris;
        }
    }
}
