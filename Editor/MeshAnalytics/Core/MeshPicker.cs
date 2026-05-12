using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Converts an image-space (u,v) pick on a render produced by
    /// `render_object_views` back into a 3D point on the target mesh.
    ///
    /// Uses Unity's built-in `MeshCollider` + `Physics.Raycast` rather than
    /// rolling our own Möller–Trumbore against `mesh.triangles`: the
    /// collider gives us `RaycastHit.triangleIndex` for free, handles
    /// submesh boundaries correctly, and is faster (BVH). When the target
    /// GameObject lacks a MeshCollider we attach a temporary one, raycast,
    /// then strip it — so authoring is non-destructive.
    public static class MeshPicker
    {
        public sealed class Pick
        {
            public Vector3 WorldPoint;
            public Vector3 WorldNormal;
            public int TriangleIndex;        // global triangle index into mesh.triangles
            public int SubmeshIndex;
            public float DistanceFromCamera;
            public string[] RegionIds;       // regionIds in the cache that contain TriangleIndex
        }

        /// Build a world-space ray from a render's camera matrices and an
        /// image-space (u,v) ∈ [0,1]². Origin = camera position; direction =
        /// through the image plane. Uses inverse(projection) to back out the
        /// camera-space ray for the given clip-space point, then
        /// cameraToWorld to bring it to world space. Matches OffscreenRenderer
        /// exactly because we cached the same matrices it returned.
        public static Ray RayFromUv(Matrix4x4 cameraToWorld, Matrix4x4 projection, float u, float v)
        {
            // Convert (u,v) ∈ [0,1] to clip-space (x,y) ∈ [-1,1]. y is
            // flipped because OpenGL/Unity image origin is bottom-left
            // *unless* the projection already accounts for it; we mirror
            // the screenshot orientation (top-left visual origin).
            float clipX = u * 2f - 1f;
            float clipY = (1f - v) * 2f - 1f;

            // A point on the near plane in clip space (z = -1) and on the
            // far plane (z = +1). Invert projection to bring both into
            // camera space, then cameraToWorld into world space. The ray
            // is (far - near) normalised.
            var inv = projection.inverse;
            var near = inv.MultiplyPoint(new Vector3(clipX, clipY, -1f));
            var far  = inv.MultiplyPoint(new Vector3(clipX, clipY,  1f));
            var dirCam = (far - near).normalized;

            var origin = cameraToWorld.MultiplyPoint3x4(Vector3.zero);
            // cameraToWorld is the inverse of worldToCamera; multiplying a
            // direction vector (w=0) gives the world-space direction.
            var dirWorld = cameraToWorld.MultiplyVector(dirCam).normalized;
            return new Ray(origin, dirWorld);
        }

        /// Raycast against a single GameObject's mesh, transparently
        /// attaching a temporary MeshCollider when necessary. The ray must
        /// be in world space.
        public static bool Pick(GameObject target, Ray ray, float maxDistance, out Pick result)
        {
            result = null;
            if (target == null) return false;

            var mesh = MeshResolver.ResolveMesh(target);
            if (mesh == null) return false;

            // Attach a temporary collider if the target doesn't have one.
            // We only restrict the raycast to this object's collider so we
            // don't accidentally hit something else in the scene.
            var existing = target.GetComponent<MeshCollider>();
            MeshCollider temp = null;
            try
            {
                MeshCollider collider;
                if (existing != null)
                {
                    collider = existing;
                }
                else
                {
                    temp = target.AddComponent<MeshCollider>();
                    temp.sharedMesh = mesh;
                    temp.convex = false;
                    collider = temp;
                }

                if (!collider.Raycast(ray, out var hit, maxDistance > 0 ? maxDistance : 10_000f))
                    return false;

                int triIdx = hit.triangleIndex;
                int submeshIdx = ResolveSubmesh(mesh, triIdx);
                var regions = FindRegionsContainingTriangle(mesh, triIdx);

                result = new Pick
                {
                    WorldPoint = hit.point,
                    WorldNormal = hit.normal,
                    TriangleIndex = triIdx,
                    SubmeshIndex = submeshIdx,
                    DistanceFromCamera = hit.distance,
                    RegionIds = regions.ToArray(),
                };
                return true;
            }
            finally
            {
                if (temp != null) Object.DestroyImmediate(temp);
            }
        }

        /// Maps a global triangle index back to its submesh by walking the
        /// submesh ranges. Unity stores submesh face spans contiguously in
        /// `mesh.triangles`; `SubMeshDescriptor.indexStart / indexCount`
        /// gives the range in indices (= 3 × triangle count).
        private static int ResolveSubmesh(Mesh mesh, int triIdx)
        {
            int indexBase = triIdx * 3;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                var sub = mesh.GetSubMesh(sm);
                if (indexBase >= sub.indexStart && indexBase < sub.indexStart + sub.indexCount)
                    return sm;
            }
            return -1;
        }

        /// Walks all cached AnalysisResults to find regions whose
        /// FaceIndices contain `triIdx`. Linear in the number of regions
        /// the user has materialised; bounded by their tool-call history.
        private static List<string> FindRegionsContainingTriangle(Mesh mesh, int triIdx)
        {
            var result = new List<string>();
            foreach (var bundle in MeshAnalysisCache.AllResults())
            {
                AppendIfHit(bundle.Submeshes, triIdx, result);
                AppendIfHit(bundle.EdgeChains, triIdx, result);
                AppendIfHit(bundle.Planes, triIdx, result);
                AppendIfHit(bundle.SmoothPatches, triIdx, result);
                AppendIfHit(bundle.ParallelGroups, triIdx, result);
            }
            return result;
        }

        private static void AppendIfHit<T>(List<T> regions, int triIdx, List<string> outList) where T : MeshRegion
        {
            foreach (var r in regions)
            {
                if (r.FaceIndices == null) continue;
                for (int i = 0; i < r.FaceIndices.Length; i++)
                {
                    if (r.FaceIndices[i] == triIdx) { outList.Add(r.RegionId); break; }
                }
            }
        }
    }
}
