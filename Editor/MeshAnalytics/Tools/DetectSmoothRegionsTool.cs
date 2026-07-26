using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Region-growing segmentation. Two adjacent faces belong to the same
    /// region iff their dihedral angle is below the threshold (default 15°).
    /// The result is the complement of mesh_detect_sharp_edges: smooth
    /// patches separated by sharp creases. Use this when geometry isn't
    /// strictly planar (terrain, sculpted props, rounded shapes) and
    /// mesh_detect_planes would miss the bulk of the surface.
    public sealed class DetectSmoothRegionsTool : IMcpTool
    {
        public string Name => "mesh_detect_smooth_regions";
        public string Description =>
            "Segments a mesh into contiguous smooth-shaded patches by flood-filling across non-sharp edges (dihedral angle below threshold). Each region has area-weighted normal, roughness metric (0=flat), orientation label, and world bounds. Complements mesh_detect_planes for non-planar surfaces (terrain, organic shapes, rounded handrails). Sorted largest-first.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = MeshToolSchemas.Target,
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["frontierAngleTolerance"] = new JObject { ["type"] = "number", ["default"] = 15.0, ["minimum"] = 1.0, ["maximum"] = 89.0, ["description"] = "Degrees. Two adjacent faces with dihedral angle below this are joined." },
                ["minFacesPerRegion"] = new JObject { ["type"] = "integer", ["default"] = 16, ["minimum"] = 1 },
                ["maxRegions"] = new JObject { ["type"] = "integer", ["default"] = 64, ["minimum"] = 1, ["maximum"] = 512 },
                ["maxFaceIndicesInResponse"] = new JObject { ["type"] = "integer", ["default"] = 0, ["minimum"] = 0, ["maximum"] = 50000 },
            },
            ["oneOf"] = MeshToolSchemas.TargetOrInstance,
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => MeshToolSchemas.SmoothRegionsOutput;

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            if (!MeshResolver.TryGetMeshAndTransforms(args, out var go, out var mesh, out var ltw, out var targetError))
                return ToolResult.Error("NOT_FOUND", targetError);
            int instanceId = go.GetInstanceID();
            if (!MeshResolver.IsReadable(mesh, out var notReadable))
                return ToolResult.Error("MESH_NOT_READABLE", notReadable);
            if (!MeshResolver.HasTriangleTopology(mesh, out var topologyError))
                return ToolResult.Error("UNSUPPORTED_TOPOLOGY", topologyError);

            var p = new SmoothRegionDetector.Params
            {
                FrontierAngleToleranceDeg = (float?)args["frontierAngleTolerance"] ?? 15f,
                MinFacesPerRegion = (int?)args["minFacesPerRegion"] ?? 16,
                MaxRegions = (int?)args["maxRegions"] ?? 64,
            };
            int faceCap = (int?)args["maxFaceIndicesInResponse"] ?? 0;

            string meshKey = MeshResolver.MakeMeshKey(mesh);
            string analysisKey = MeshResolver.MakeAnalysisKey(mesh, go);
            string paramsHash = $"smooth:{p.FrontierAngleToleranceDeg:F2}:{p.MinFacesPerRegion}:{p.MaxRegions}";

            if (!MeshAnalysisCache.TryGetWithFallback(analysisKey, paramsHash, go, ltw, out var bundle))
            {
                bundle = MeshAnalysisCache.CreateResult(meshKey, analysisKey, go, ltw);
                bundle.SmoothPatches.AddRange(SmoothRegionDetector.Detect(mesh, ltw, analysisKey, paramsHash, instanceId, p));
                MeshAnalysisCache.StoreAndPersist(analysisKey, paramsHash, bundle);
            }

            var arr = new JArray();
            foreach (var s in bundle.SmoothPatches)
            {
                var entry = new JObject
                {
                    ["regionId"] = s.RegionId,
                    ["avgNormal"] = new JArray { s.AvgNormalWorld.x, s.AvgNormalWorld.y, s.AvgNormalWorld.z },
                    ["areaApprox"] = s.AreaApprox,
                    ["faceCount"] = s.FaceCount,
                    ["roughness"] = s.RoughnessMetric,
                    ["orientationLabel"] = s.OrientationLabel,
                    ["worldBounds"] = BoundsJson(s.WorldBounds),
                };
                if (faceCap > 0 && s.FaceIndices != null)
                {
                    var faces = new JArray();
                    int n = Mathf.Min(s.FaceIndices.Length, faceCap);
                    for (int i = 0; i < n; i++) faces.Add(s.FaceIndices[i]);
                    entry["faceIndices"] = faces;
                    entry["faceIndicesTruncated"] = s.FaceIndices.Length > n;
                }
                arr.Add(entry);
            }
            var payload = new JObject
            {
                ["meshName"] = mesh.name,
                ["regionCount"] = bundle.SmoothPatches.Count,
                ["regions"] = arr,
            };
            return ToolResult.Ok(
                $"{bundle.SmoothPatches.Count} smooth region(s) on '{go.name}' (frontier ≥{p.FrontierAngleToleranceDeg:F0}°)",
                payload);
        }

        private static JObject BoundsJson(Bounds b) => new JObject
        {
            ["center"] = new JArray { b.center.x, b.center.y, b.center.z },
            ["size"] = new JArray { b.size.x, b.size.y, b.size.z },
        };
    }
}
