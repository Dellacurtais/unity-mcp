using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Kind of region a regionId refers to. Encoded into the id string as the
    /// middle segment ("meshGuid/kind/index") so the LLM can validate at a
    /// glance, and so the cache can route lookups to the right collection.
    public enum RegionKind
    {
        Submesh,
        EdgeChain,
        Plane,
        Smooth,         // contiguous smooth-shaded patch (region growing by normal continuity)
        ParallelGroup,  // group of edge chains aligned in parallel (trilhos, grades, escadas)
    }

    /// Common fields for any region produced by a detector or inspector. Subtypes
    /// add their own payload (vertex chain, plane equation, etc.). Faces are
    /// stored as global triangle indices into Mesh.triangles / GetTriangles(sm).
    /// World-space data is recomputed lazily — store local-space + transform.
    public abstract class MeshRegion
    {
        public string RegionId;
        public RegionKind Kind;
        public int[] FaceIndices;          // global triangle indices into combined triangle array
        public Bounds WorldBounds;         // pre-computed when region is created
        public int OwnerInstanceId;        // GameObject this region was computed for
    }

    public sealed class SubmeshRegion : MeshRegion
    {
        public int SubmeshIndex;
        public string MaterialName;
        public int TriangleCount;
        public Bounds LocalBounds;
        public MeshTopology Topology;
    }

    public sealed class EdgeChainRegion : MeshRegion
    {
        public Vector3[] VertexPositionsWorld;  // ordered along the chain
        public float TotalLength;
        public bool IsLoop;
    }

    public sealed class PlaneRegion : MeshRegion
    {
        public Vector3 NormalWorld;
        public Vector3 PointWorld;             // a point on the plane (centroid of inliers)
        public float AreaApprox;               // sum of inlier triangle areas
        public int FaceCount;
        public string OrientationLabel;        // "horizontal_up" / "horizontal_down" / "vertical:N"
        public string Classification;          // "ground_candidate" / "platform_candidate" / etc.
    }

    /// Contiguous patch of similarly-oriented faces grown from a seed via
    /// normal-continuity propagation. Captures organic surfaces that aren't
    /// strictly planar — terrain bumps, sculpted props, rounded rails.
    public sealed class SmoothRegion : MeshRegion
    {
        public Vector3 AvgNormalWorld;
        public float AreaApprox;
        public int FaceCount;
        public float RoughnessMetric;          // mean |1 - dot(faceN, avgN)| over inliers; 0 = perfectly flat
        public string OrientationLabel;        // shared scheme with PlaneRegion
    }

    /// Cluster of EdgeChain regions that run approximately parallel with
    /// roughly equal spacing — trilhos, grades, escadas, janelas em fileira.
    /// Doesn't own FaceIndices directly; references its constituent chains.
    public sealed class ParallelGroupRegion : MeshRegion
    {
        public string[] ChildChainRegionIds;
        public Vector3 DirectionWorld;
        public float MeanSpacing;
        public float SpacingStdDev;
        public int InstanceCount;
    }

    /// Bundle of regions for a single (mesh, params) tuple. Lives in
    /// MeshAnalysisCache. RegionIds inside are stable for the lifetime of the
    /// cache entry; an invalidation (mesh edit, domain reload) wipes the bundle
    /// and assigns fresh ids on recompute.
    public sealed class AnalysisResult
    {
        public string MeshGuid;            // mesh subasset key (GUID + local file id when persistent)
        public string AnalysisKey;         // mesh key scoped to the scene/prefab owner
        public int OwnerInstanceId;
        public ToolTargetRef OwnerTarget;
        /// localToWorld of the GameObject at the moment the regions were
        /// computed. World-space data inside the regions is only valid as
        /// long as the runtime instance's transform matches this. The cache
        /// uses this to decide whether a disk-loaded result can be reused
        /// or must be recomputed.
        public Matrix4x4 OwnerLocalToWorld = Matrix4x4.identity;
        public List<SubmeshRegion> Submeshes = new();
        public List<EdgeChainRegion> EdgeChains = new();
        public List<PlaneRegion> Planes = new();
        public List<SmoothRegion> SmoothPatches = new();
        public List<ParallelGroupRegion> ParallelGroups = new();

        public MeshRegion FindById(string regionId)
        {
            foreach (var r in Submeshes) if (r.RegionId == regionId) return r;
            foreach (var r in EdgeChains) if (r.RegionId == regionId) return r;
            foreach (var r in Planes) if (r.RegionId == regionId) return r;
            foreach (var r in SmoothPatches) if (r.RegionId == regionId) return r;
            foreach (var r in ParallelGroups) if (r.RegionId == regionId) return r;
            return null;
        }
    }
}
