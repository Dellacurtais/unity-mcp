using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
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
            => UnityObjectIds.Resolve(instanceId) as GameObject;

        public static GameObject ResolveGameObject(JObject arguments, out string error)
            => ToolTargetResolver.ResolveGameObject(arguments, out error);

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

        public static bool TryGetMeshAndTransforms(JObject arguments,
            out GameObject go, out Mesh mesh, out Matrix4x4 localToWorld, out string error)
        {
            go = ResolveGameObject(arguments, out error);
            mesh = ResolveMesh(go);
            localToWorld = go != null ? go.transform.localToWorldMatrix : Matrix4x4.identity;
            if (go == null) return false;
            if (mesh != null) return true;
            error = $"TARGET_HAS_NO_MESH: GameObject '{go.name}' has no MeshFilter/SkinnedMeshRenderer mesh";
            return false;
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
            if (mesh.isReadable || (!EditorApplication.isPlaying && EditorUtility.IsPersistent(mesh)))
            {
                reason = null;
                return true;
            }
            reason = $"mesh '{mesh.name}' has no CPU-readable vertex/index data. " +
                     "Imported meshes remain readable in Edit Mode in Unity 6000.3; this usually means Play Mode " +
                     "or a procedural mesh uploaded with UploadMeshData(true). For an imported model needed at runtime, " +
                     "call `mesh_enable_read_write`, then retry.";
            return false;
        }

        public static bool HasTriangleTopology(Mesh mesh, out string reason)
        {
            if (mesh == null) { reason = "mesh is null"; return false; }
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                var topology = mesh.GetTopology(submesh);
                if (topology != MeshTopology.Triangles)
                {
                    reason = $"mesh '{mesh.name}' submesh {submesh} uses {topology}; triangle analytics require MeshTopology.Triangles for every submesh";
                    return false;
                }
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
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localFileId)
                && !string.IsNullOrEmpty(guid))
                return $"{guid}:{localFileId}";
            return $"runtime:{mesh.GetMcpInstanceId()}";
        }

        public static string MakeAnalysisKey(Mesh mesh, GameObject owner)
        {
            var meshKey = MakeMeshKey(mesh);
            if (owner == null) return meshKey;
            var target = ToolTargetRef.Capture(owner);
            var ownerKey = !string.IsNullOrEmpty(target?.GlobalObjectId)
                ? target.GlobalObjectId
                : $"instance:{owner.GetMcpInstanceId()}";
            return $"{meshKey}@{ownerKey}";
        }

        public static JObject TargetData(GameObject gameObject)
            => ToolTargetRef.Capture(gameObject)?.ToJson();
    }
}
