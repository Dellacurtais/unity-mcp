using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Detects sharp edges of a mesh — edges shared by two triangles whose
    /// face normals form a dihedral angle exceeding the threshold (default
    /// 30°). Connected sharp edges are merged into polylines (regions of
    /// kind 'edge'). Each polyline returns its world-space vertex chain,
    /// total length, loop flag, and adjacent face indices.
    ///
    /// This is the tool that resolves the user's "the orange line is the
    /// edge of the sidewalk" example — the longest chain typically traces
    /// the contour. Results are sorted longest-first so regionId 'edge/0'
    /// is the most visually significant chain.
    public sealed class DetectSharpEdgesTool : IMcpTool
    {
        public string Name => "mesh_detect_sharp_edges";
        public string Description =>
            "Finds edges of a mesh whose dihedral angle exceeds the threshold (default 30°), groups connected sharp edges into polylines, and returns them as 'edge' regions with world-space vertex chains. Sorted longest-first. Use to locate sidewalk borders, building corners, door frames, stair noses — anything with a visible crease. RegionIds can be passed to render_region_highlight.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["dihedralAngleMin"] = new JObject { ["type"] = "number", ["default"] = 30.0, ["minimum"] = 1.0, ["maximum"] = 179.0, ["description"] = "Degrees. Edges with face normals diverging more than this are 'sharp'." },
                ["minChainLength"] = new JObject { ["type"] = "number", ["default"] = 0.1, ["minimum"] = 0.0, ["description"] = "World-space metres. Chains shorter than this are dropped." },
                ["maxChains"] = new JObject { ["type"] = "integer", ["default"] = 64, ["minimum"] = 1, ["maximum"] = 512 },
                ["maxChainVerticesInResponse"] = new JObject { ["type"] = "integer", ["default"] = 128, ["minimum"] = 8, ["maximum"] = 4096, ["description"] = "Cap on vertex positions echoed back per chain; full chain is kept in the cache for render_region_highlight." },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            int instanceId = (int?)args["instanceId"] ?? 0;
            if (!MeshResolver.TryGetMeshAndTransforms(instanceId, out var go, out var mesh, out var ltw))
                return ToolResult.Error("NOT_FOUND",
                    $"no MeshFilter/SkinnedMeshRenderer mesh on GameObject {instanceId}");

            var p = new SharpEdgeDetector.Params
            {
                DihedralAngleMinDeg = (float?)args["dihedralAngleMin"] ?? 30f,
                MinChainLength = (float?)args["minChainLength"] ?? 0.1f,
                MaxChains = (int?)args["maxChains"] ?? 64,
            };
            int vertexCap = (int?)args["maxChainVerticesInResponse"] ?? 128;

            string meshKey = MeshResolver.MakeMeshKey(mesh);
            string paramsHash = $"edge:{p.DihedralAngleMinDeg:F2}:{p.MinChainLength:F3}:{p.MaxChains}";
            string cacheKey = MeshAnalysisCache.MakeKey(meshKey, paramsHash);

            if (!MeshAnalysisCache.TryGet(cacheKey, out var bundle))
            {
                bundle = new AnalysisResult { MeshGuid = meshKey, OwnerInstanceId = instanceId };
                bundle.EdgeChains.AddRange(SharpEdgeDetector.Detect(mesh, ltw, meshKey, paramsHash, instanceId, p));
                MeshAnalysisCache.Store(cacheKey, bundle);
            }

            var arr = new JArray();
            foreach (var ec in bundle.EdgeChains)
            {
                var pts = new JArray();
                int n = Mathf.Min(ec.VertexPositionsWorld.Length, vertexCap);
                for (int i = 0; i < n; i++)
                {
                    var v = ec.VertexPositionsWorld[i];
                    pts.Add(new JArray { v.x, v.y, v.z });
                }
                arr.Add(new JObject
                {
                    ["regionId"] = ec.RegionId,
                    ["totalLength"] = ec.TotalLength,
                    ["isLoop"] = ec.IsLoop,
                    ["vertexCount"] = ec.VertexPositionsWorld.Length,
                    ["adjacentFaceCount"] = ec.FaceIndices.Length,
                    ["worldBounds"] = BoundsJson(ec.WorldBounds),
                    ["vertexPositionsWorld"] = pts,
                    ["vertexPositionsTruncated"] = ec.VertexPositionsWorld.Length > n,
                });
            }
            var payload = new JObject
            {
                ["meshName"] = mesh.name,
                ["chainCount"] = bundle.EdgeChains.Count,
                ["dihedralAngleMin"] = p.DihedralAngleMinDeg,
                ["chains"] = arr,
            };
            return ToolResult.Ok(
                $"{bundle.EdgeChains.Count} sharp-edge chain(s) on '{go.name}' (>{p.DihedralAngleMinDeg:F0}°)",
                payload);
        }

        private static JObject BoundsJson(Bounds b) => new JObject
        {
            ["center"] = new JArray { b.center.x, b.center.y, b.center.z },
            ["size"] = new JArray { b.size.x, b.size.y, b.size.z },
        };
    }
}
