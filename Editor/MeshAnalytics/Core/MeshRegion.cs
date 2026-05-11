using System.Collections.Generic;
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

    /// Bundle of regions for a single (mesh, params) tuple. Lives in
    /// MeshAnalysisCache. RegionIds inside are stable for the lifetime of the
    /// cache entry; an invalidation (mesh edit, domain reload) wipes the bundle
    /// and assigns fresh ids on recompute.
    public sealed class AnalysisResult
    {
        public string MeshGuid;            // empty if mesh is not an asset (built-in)
        public int OwnerInstanceId;
        public List<SubmeshRegion> Submeshes = new();
        public List<EdgeChainRegion> EdgeChains = new();
        public List<PlaneRegion> Planes = new();

        public MeshRegion FindById(string regionId)
        {
            foreach (var r in Submeshes) if (r.RegionId == regionId) return r;
            foreach (var r in EdgeChains) if (r.RegionId == regionId) return r;
            foreach (var r in Planes) if (r.RegionId == regionId) return r;
            return null;
        }
    }
}
