using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Converts an image-space (u,v) pick on a render produced by
    /// `render_object_views` back into a 3D point on the target mesh.
    ///
    /// Intersects the target mesh directly on the CPU. This avoids adding a
    /// temporary MeshCollider to the scene, so the read-only MCP tool never
    /// mutates hierarchy, prefab, physics, or scene-dirty state.
    public static class MeshPicker
    {
        public sealed class PickResult
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
        public static bool Pick(GameObject target, Ray ray, float maxDistance, out PickResult result)
        {
            result = null;
            if (target == null) return false;

            var mesh = MeshResolver.ResolveMesh(target);
            if (mesh == null) return false;

            if (!MeshResolver.HasTriangleTopology(mesh, out _)) return false;

            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            var localToWorld = target.transform.localToWorldMatrix;
            float closestDistance = maxDistance > 0f ? maxDistance : float.PositiveInfinity;
            int closestTriangle = -1;
            Vector3 closestPoint = default;
            Vector3 closestNormal = default;

            for (int triangle = 0; triangle < triangles.Length / 3; triangle++)
            {
                var a = localToWorld.MultiplyPoint3x4(vertices[triangles[triangle * 3]]);
                var b = localToWorld.MultiplyPoint3x4(vertices[triangles[triangle * 3 + 1]]);
                var c = localToWorld.MultiplyPoint3x4(vertices[triangles[triangle * 3 + 2]]);
                if (!IntersectTriangle(ray, a, b, c, out var distance)) continue;
                if (distance >= closestDistance) continue;
                closestDistance = distance;
                closestTriangle = triangle;
                closestPoint = ray.GetPoint(distance);
                closestNormal = Vector3.Cross(b - a, c - a).normalized;
            }

            if (closestTriangle < 0) return false;
            int submeshIdx = ResolveSubmesh(mesh, closestTriangle);
            var regions = FindRegionsContainingTriangle(target, mesh, closestTriangle);
            result = new PickResult
            {
                WorldPoint = closestPoint,
                WorldNormal = closestNormal,
                TriangleIndex = closestTriangle,
                SubmeshIndex = submeshIdx,
                DistanceFromCamera = closestDistance,
                RegionIds = regions.ToArray(),
            };
            return true;
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
        private static List<string> FindRegionsContainingTriangle(GameObject target, Mesh mesh, int triIdx)
        {
            var result = new List<string>();
            var analysisKey = MeshResolver.MakeAnalysisKey(mesh, target);
            foreach (var bundle in MeshAnalysisCache.AllResults())
            {
                if (!string.Equals(bundle.AnalysisKey, analysisKey, System.StringComparison.Ordinal)) continue;
                AppendIfHit(bundle.Submeshes, triIdx, result);
                AppendIfHit(bundle.EdgeChains, triIdx, result);
                AppendIfHit(bundle.Planes, triIdx, result);
                AppendIfHit(bundle.SmoothPatches, triIdx, result);
                AppendIfHit(bundle.ParallelGroups, triIdx, result);
            }
            return result;
        }

        private static bool IntersectTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            var edge1 = b - a;
            var edge2 = c - a;
            var p = Vector3.Cross(ray.direction, edge2);
            float determinant = Vector3.Dot(edge1, p);
            if (Mathf.Abs(determinant) < 1e-8f) return false;
            float inverse = 1f / determinant;
            var t = ray.origin - a;
            float u = Vector3.Dot(t, p) * inverse;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(t, edge1);
            float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(edge2, q) * inverse;
            return distance > 0f;
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
