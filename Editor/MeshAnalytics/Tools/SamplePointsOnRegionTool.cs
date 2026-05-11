using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Generates 3D sample points on the triangles of a region (submesh,
    /// plane, or anything else with FaceIndices). Provide either `count` or
    /// `spacing` (metres). `inset` keeps points away from the region's
    /// world-AABB borders — useful for "don't put a lamp right on the kerb".
    ///
    /// This is the bridge from "I found the sidewalk plane" to "give me coords
    /// to place 15 lamp posts". The LLM then feeds each point into
    /// instantiate_prefab (or any other authoring tool).
    public sealed class SamplePointsOnRegionTool : IMcpTool
    {
        public string Name => "mesh_sample_points_on_region";
        public string Description =>
            "Samples world-space points (and per-point surface normals) on the triangles of a given region (regionId from inspect_submeshes / detect_planes). Pass either 'count' or 'spacing' (metres). 'inset' (metres) shrinks the sampling area away from the region's outer edges. Distribution: 'poisson_disk' (default, evenly spaced), 'uniform_random' (fastest), or 'grid'. Use to place waypoints on sidewalks, scatter props, distribute NPCs.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["regionId"] = new JObject { ["type"] = "string", ["description"] = "From mesh_inspect_submeshes / mesh_detect_planes / mesh_detect_sharp_edges." },
                ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["description"] = "Target number of points. Mutually exclusive with 'spacing'." },
                ["spacing"] = new JObject { ["type"] = "number", ["minimum"] = 0.01, ["description"] = "Metres between points. Mutually exclusive with 'count'." },
                ["inset"] = new JObject { ["type"] = "number", ["default"] = 0.0, ["minimum"] = 0.0, ["description"] = "Metres to shrink the sampling area inward from the region bounds." },
                ["distribution"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "poisson_disk", "uniform_random", "grid" }, ["default"] = "poisson_disk" },
                ["seed"] = new JObject { ["type"] = "integer", ["default"] = 1234 },
            },
            ["required"] = new JArray { "regionId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            string regionId = (string)args["regionId"];
            if (string.IsNullOrEmpty(regionId))
                return ToolResult.Error("BAD_ARGS", "regionId is required");

            var region = MeshAnalysisCache.FindRegion(regionId, out _);
            if (region == null)
                return ToolResult.Error("NOT_FOUND",
                    $"regionId '{regionId}' not in cache. Re-run the detector that produced it (mesh_inspect_submeshes / mesh_detect_planes / mesh_detect_sharp_edges).");

            if (region is EdgeChainRegion)
                return ToolResult.Error("BAD_REGION_KIND",
                    "sample_points_on_region works on submesh/plane regions (surface). Edge chains have no area; use their vertexPositionsWorld directly.");

            if (!MeshResolver.TryGetMeshAndTransforms(region.OwnerInstanceId, out _, out var mesh, out var ltw))
                return ToolResult.Error("NOT_FOUND",
                    $"GameObject for regionId '{regionId}' is no longer in the scene (instanceId={region.OwnerInstanceId}).");

            var p = new RegionSampler.Params
            {
                Count = (int?)args["count"],
                Spacing = (float?)args["spacing"],
                Inset = (float?)args["inset"] ?? 0f,
                Distribution = ParseDist((string)args["distribution"]),
                Seed = (int?)args["seed"] ?? 1234,
            };
            if (p.Count == null && p.Spacing == null)
                p.Count = 32;
            if (p.Count != null && p.Spacing != null)
                return ToolResult.Error("BAD_ARGS", "provide either 'count' or 'spacing', not both");

            var samples = RegionSampler.Sample(region, mesh, ltw, p);

            var arr = new JArray();
            foreach (var s in samples)
            {
                arr.Add(new JObject
                {
                    ["position"] = new JArray { s.Position.x, s.Position.y, s.Position.z },
                    ["normal"] = new JArray { s.Normal.x, s.Normal.y, s.Normal.z },
                });
            }
            var payload = new JObject
            {
                ["regionId"] = regionId,
                ["sampleCount"] = samples.Count,
                ["distribution"] = p.Distribution.ToString().ToLowerInvariant(),
                ["samples"] = arr,
            };
            return ToolResult.Ok($"{samples.Count} sample(s) on region '{regionId}' ({p.Distribution})", payload);
        }

        private static RegionSampler.Distribution ParseDist(string s)
        {
            return (s ?? "poisson_disk").ToLowerInvariant() switch
            {
                "uniform_random" => RegionSampler.Distribution.UniformRandom,
                "grid" => RegionSampler.Distribution.Grid,
                _ => RegionSampler.Distribution.PoissonDisk,
            };
        }
    }
}
