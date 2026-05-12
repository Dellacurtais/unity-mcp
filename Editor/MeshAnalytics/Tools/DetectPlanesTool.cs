using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Segments a mesh into dominant planes via iterative RANSAC and returns
    /// each as a 'plane' region with normal, point, world bounds, area,
    /// orientation label, and a light heuristic classification
    /// (ground_candidate / platform_candidate / wall_candidate / ...).
    ///
    /// The classification is geometric only — no ML, no asset-name guessing —
    /// so the LLM can confirm visually via render_region_highlight when the
    /// label matters. Use this tool to find "the top of a sidewalk", "the
    /// road surface", "a wall facing east", etc.
    public sealed class DetectPlanesTool : IMcpTool
    {
        public string Name => "mesh_detect_planes";
        public string Description =>
            "Segments a mesh into dominant planes via RANSAC. Each plane region has a world-space normal, a point on the plane, an orientation label (horizontal_up/horizontal_down/vertical:bearing), and a geometric classification (ground_candidate/platform_candidate/wall_candidate/door_or_window_candidate/ceiling_candidate/other). Use for sidewalks, roads, walls, floors. RegionIds work with mesh_sample_points_on_region and render_region_highlight.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["minFacesPerPlane"] = new JObject { ["type"] = "integer", ["default"] = 50, ["minimum"] = 4 },
                ["angleTolerance"] = new JObject { ["type"] = "number", ["default"] = 5.0, ["minimum"] = 0.1, ["maximum"] = 45.0, ["description"] = "Degrees. Inliers must have face normals within this of the plane normal." },
                ["distanceTolerance"] = new JObject { ["type"] = "number", ["default"] = -1.0, ["description"] = "Metres. Inliers must lie within this of the plane. -1 = auto (1% of bounds diagonal)." },
                ["maxPlanes"] = new JObject { ["type"] = "integer", ["default"] = 16, ["minimum"] = 1, ["maximum"] = 64 },
                ["maxFaceIndicesInResponse"] = new JObject { ["type"] = "integer", ["default"] = 0, ["minimum"] = 0, ["maximum"] = 50000, ["description"] = "Cap on faceIndices echoed back per plane. 0 = omit (full set is kept in cache for sampling/highlighting)." },
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
            if (!MeshResolver.IsReadable(mesh, out var notReadable))
                return ToolResult.Error("MESH_NOT_READABLE", notReadable);

            var p = new PlaneDetector.Params
            {
                MinFacesPerPlane = (int?)args["minFacesPerPlane"] ?? 50,
                AngleToleranceDeg = (float?)args["angleTolerance"] ?? 5f,
                DistanceTolerance = (float?)args["distanceTolerance"] ?? -1f,
                MaxPlanes = (int?)args["maxPlanes"] ?? 16,
            };
            int faceCap = (int?)args["maxFaceIndicesInResponse"] ?? 0;

            string meshKey = MeshResolver.MakeMeshKey(mesh);
            string paramsHash = $"plane:{p.MinFacesPerPlane}:{p.AngleToleranceDeg:F2}:{p.DistanceTolerance:F4}:{p.MaxPlanes}";

            if (!MeshAnalysisCache.TryGetWithFallback(meshKey, paramsHash, ltw, out var bundle))
            {
                bundle = new AnalysisResult
                {
                    MeshGuid = meshKey,
                    OwnerInstanceId = instanceId,
                    OwnerLocalToWorld = ltw,
                };
                bundle.Planes.AddRange(PlaneDetector.Detect(mesh, ltw, meshKey, paramsHash, instanceId, p));
                MeshAnalysisCache.StoreAndPersist(meshKey, paramsHash, bundle);
            }

            var arr = new JArray();
            foreach (var pl in bundle.Planes)
            {
                var entry = new JObject
                {
                    ["regionId"] = pl.RegionId,
                    ["normal"] = new JArray { pl.NormalWorld.x, pl.NormalWorld.y, pl.NormalWorld.z },
                    ["point"] = new JArray { pl.PointWorld.x, pl.PointWorld.y, pl.PointWorld.z },
                    ["areaApprox"] = pl.AreaApprox,
                    ["faceCount"] = pl.FaceCount,
                    ["orientationLabel"] = pl.OrientationLabel,
                    ["classification"] = pl.Classification,
                    ["worldBounds"] = BoundsJson(pl.WorldBounds),
                };
                if (faceCap > 0 && pl.FaceIndices != null)
                {
                    var faces = new JArray();
                    int n = Mathf.Min(pl.FaceIndices.Length, faceCap);
                    for (int i = 0; i < n; i++) faces.Add(pl.FaceIndices[i]);
                    entry["faceIndices"] = faces;
                    entry["faceIndicesTruncated"] = pl.FaceIndices.Length > n;
                }
                arr.Add(entry);
            }
            var payload = new JObject
            {
                ["meshName"] = mesh.name,
                ["planeCount"] = bundle.Planes.Count,
                ["planes"] = arr,
            };
            return ToolResult.Ok(
                $"{bundle.Planes.Count} plane(s) on '{go.name}' (RANSAC, ±{p.AngleToleranceDeg:F1}°)",
                payload);
        }

        private static JObject BoundsJson(Bounds b) => new JObject
        {
            ["center"] = new JArray { b.center.x, b.center.y, b.center.z },
            ["size"] = new JArray { b.size.x, b.size.y, b.size.z },
        };
    }
}
