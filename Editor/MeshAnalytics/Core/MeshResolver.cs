using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Centralises "GameObject → Mesh" resolution so every tool agrees on which
    /// mesh "belongs" to an instanceId. Pattern lifted from GeometryAnalyzer.cs
    /// (private ResolveMesh there) and promoted to a shared helper.
    ///
    /// Returns the *shared* mesh — we never want to mutate it, and analysis is
    /// read-only.
    public static class MeshResolver
    {
        public static GameObject ResolveGameObject(int instanceId)
            => EditorUtility.InstanceIDToObject(instanceId) as GameObject;

        public static Mesh ResolveMesh(GameObject go)
        {
            if (go == null) return null;
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) return smr.sharedMesh;
            var mf = go.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        public static Mesh ResolveMesh(int instanceId)
            => ResolveMesh(ResolveGameObject(instanceId));

        /// Returns the renderer's worldToLocal / localToWorld matrices and the
        /// mesh in one call. Convenience for detectors that need to transform
        /// local-space mesh data into world space.
        public static bool TryGetMeshAndTransforms(int instanceId,
            out GameObject go, out Mesh mesh, out Matrix4x4 localToWorld)
        {
            go = ResolveGameObject(instanceId);
            mesh = ResolveMesh(go);
            localToWorld = go != null ? go.transform.localToWorldMatrix : Matrix4x4.identity;
            return mesh != null;
        }

        /// True when the mesh's CPU-side data (vertices / triangles) is
        /// reachable. Imported FBX/glTF with "Read/Write Enabled" off can
        /// strip CPU data for builds; the Editor *usually* keeps it but not
        /// always (notably, meshes uploaded via `Mesh.UploadMeshData(true)`).
        /// Detectors call this and return a clean error instead of letting
        /// Unity throw the cryptic "Not allowed to access vertices…".
        public static bool IsReadable(Mesh mesh, out string reason)
        {
            if (mesh == null) { reason = "mesh is null"; return false; }
            if (!mesh.isReadable)
            {
                reason = $"mesh '{mesh.name}' has Read/Write disabled at import — re-import with 'Read/Write Enabled' to allow analysis";
                return false;
            }
            reason = null;
            return true;
        }

        /// Stable key for cache buckets. Prefers the asset GUID; falls back to
        /// the runtime instance id when the mesh isn't an imported asset (e.g.
        /// procedural). The runtime fallback is fine within a session since
        /// the cache invalidates on domain reload anyway.
        public static string MakeMeshKey(Mesh mesh)
        {
            if (mesh == null) return "null";
            var path = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(path))
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid)) return guid;
            }
            return $"runtime:{mesh.GetInstanceID()}";
        }
    }
}
