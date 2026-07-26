using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Lists submeshes of a GameObject's mesh with per-submesh metadata
    /// (material name, triangle count, local + world bounds, topology). Each
    /// submesh gets a stable regionId so `render_region_highlight` and
    /// `mesh_sample_points_on_region` can target it directly. Useful when the
    /// FBX author already separated geometry by intent (e.g. "Walls",
    /// "Floor", "DoorFrame" as submeshes).
    public sealed class InspectSubmeshesTool : IMcpTool
    {
        public string Name => "mesh_inspect_submeshes";
        public string Description =>
            "Lists submeshes of a GameObject's mesh with per-submesh region IDs, material names, triangle counts, and bounds (local + world). Use to discover semantic structure when the artist separated geometry by intent. Submesh regionIds can be passed to mesh_sample_points_on_region or render_region_highlight.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = MeshToolSchemas.Target,
                ["instanceId"] = new JObject { ["type"] = "integer", ["description"] = "GameObject instance id (from read_hierarchy)." },
            },
            ["oneOf"] = MeshToolSchemas.TargetOrInstance,
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => MeshToolSchemas.SubmeshesOutput;

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            if (!MeshResolver.TryGetMeshAndTransforms(args, out var go, out var mesh, out var ltw, out var targetError))
                return ToolResult.Error("NOT_FOUND", targetError);
            int instanceId = go.GetInstanceID();
            if (!MeshResolver.IsReadable(mesh, out var notReadable))
                return ToolResult.Error("MESH_NOT_READABLE", notReadable);

            string meshKey = MeshResolver.MakeMeshKey(mesh);
            string analysisKey = MeshResolver.MakeAnalysisKey(mesh, go);
            string paramsHash = "submesh";
            if (!MeshAnalysisCache.TryGetWithFallback(analysisKey, paramsHash, go, ltw, out var bundle))
            {
                bundle = MeshAnalysisCache.CreateResult(meshKey, analysisKey, go, ltw);
                BuildSubmeshes(go, mesh, ltw, analysisKey, paramsHash, instanceId, bundle);
                MeshAnalysisCache.StoreAndPersist(analysisKey, paramsHash, bundle);
            }

            var arr = new JArray();
            foreach (var s in bundle.Submeshes)
            {
                arr.Add(new JObject
                {
                    ["regionId"] = s.RegionId,
                    ["submeshIndex"] = s.SubmeshIndex,
                    ["materialName"] = s.MaterialName,
                    ["triangleCount"] = s.TriangleCount,
                    ["topology"] = s.Topology.ToString(),
                    ["localBounds"] = BoundsJson(s.LocalBounds),
                    ["worldBounds"] = BoundsJson(s.WorldBounds),
                });
            }
            var payload = new JObject
            {
                ["meshName"] = mesh.name,
                ["submeshCount"] = bundle.Submeshes.Count,
                ["submeshes"] = arr,
            };
            return ToolResult.Ok($"{bundle.Submeshes.Count} submesh(es) on '{go.name}' / '{mesh.name}'", payload);
        }

        private static void BuildSubmeshes(GameObject go, Mesh mesh, Matrix4x4 ltw,
            string meshKey, string paramsHash, int instanceId, AnalysisResult bundle)
        {
            var renderer = go.GetComponent<Renderer>();
            var materials = renderer != null ? renderer.sharedMaterials : null;

            // We need world bounds per submesh — Mesh.GetSubMesh(i).bounds is
            // local, so transform its 8 corners by localToWorld and Encapsulate.
            int globalTriangleStart = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var sub = mesh.GetSubMesh(i);
                var local = sub.bounds;
                var world = TransformBounds(local, ltw);

                string matName = (materials != null && i < materials.Length && materials[i] != null)
                    ? materials[i].name
                    : "(no material)";

                // Build the global triangle index array for this submesh.
                // GetIndices returns vertex indices; convert to global triangle
                // indices (sub.indexStart / 3 .. + sub.indexCount / 3).
                int triCount = sub.topology == MeshTopology.Triangles ? sub.indexCount / 3 : 0;
                var faceIdx = new int[triCount];
                for (int t = 0; t < triCount; t++) faceIdx[t] = globalTriangleStart + t;
                globalTriangleStart += triCount;

                bundle.Submeshes.Add(new SubmeshRegion
                {
                    Kind = RegionKind.Submesh,
                    OwnerInstanceId = instanceId,
                    RegionId = MeshAnalysisCache.MakeRegionId(meshKey, paramsHash, RegionKind.Submesh, i),
                    SubmeshIndex = i,
                    MaterialName = matName,
                    TriangleCount = triCount,
                    LocalBounds = local,
                    WorldBounds = world,
                    Topology = sub.topology,
                    FaceIndices = faceIdx,
                });
            }
        }

        private static Bounds TransformBounds(Bounds local, Matrix4x4 m)
        {
            var c = local.center;
            var e = local.extents;
            Vector3 wc = m.MultiplyPoint3x4(c);
            // Project extents onto each row of |M| → tight AABB after transform.
            float ex = Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z;
            float ey = Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z;
            float ez = Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z;
            return new Bounds(wc, new Vector3(ex * 2, ey * 2, ez * 2));
        }

        private static JObject BoundsJson(Bounds b) => new JObject
        {
            ["center"] = new JArray { b.center.x, b.center.y, b.center.z },
            ["size"] = new JArray { b.size.x, b.size.y, b.size.z },
            ["min"] = new JArray { b.min.x, b.min.y, b.min.z },
            ["max"] = new JArray { b.max.x, b.max.y, b.max.z },
        };
    }
}
