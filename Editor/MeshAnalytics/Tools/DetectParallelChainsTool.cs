using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Groups previously-detected sharp-edge chains that run parallel with
    /// roughly equal spacing — trilhos, grades, escadas, janelas em fileira.
    /// Either pass an instanceId (we'll run sharp-edge detection with
    /// defaults and group the result), or pass an explicit list of chain
    /// regionIds to cluster.
    public sealed class DetectParallelChainsTool : IMcpTool
    {
        public string Name => "mesh_detect_parallel_chains";
        public string Description =>
            "Groups edge chains that run approximately parallel with roughly equal spacing — trilhos, grades, escadas, janelas em fileira. Pass instanceId to auto-run sharp-edge detection first, or chainRegionIds to cluster a specific set. Each group reports direction, mean spacing, stddev, and member chain ids. Use render_region_highlight to visualise.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer", ["description"] = "If provided, run sharp-edge detection on this GameObject first. Mutually exclusive with chainRegionIds." },
                ["chainRegionIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "Specific edge_chain regionIds to cluster (from a previous mesh_detect_sharp_edges call). Mutually exclusive with instanceId.",
                },
                ["dihedralAngleMin"] = new JObject { ["type"] = "number", ["default"] = 30.0, ["description"] = "Used only when instanceId is provided." },
                ["directionAngleTolerance"] = new JObject { ["type"] = "number", ["default"] = 10.0, ["minimum"] = 0.5, ["maximum"] = 45.0 },
                ["spacingTolerance"] = new JObject { ["type"] = "number", ["default"] = 0.25, ["minimum"] = 0.01, ["maximum"] = 1.0, ["description"] = "Relative — successive-spacing stddev/mean must be below this." },
                ["minSpacing"] = new JObject { ["type"] = "number", ["default"] = 0.05, ["minimum"] = 0.0 },
                ["minMembers"] = new JObject { ["type"] = "integer", ["default"] = 2, ["minimum"] = 2 },
                ["maxGroups"] = new JObject { ["type"] = "integer", ["default"] = 16, ["minimum"] = 1, ["maximum"] = 128 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            // Resolve the source chain set + owner mesh (for meshKey/paramsHash).
            int instanceId = (int?)args["instanceId"] ?? 0;
            JArray idsArr = args["chainRegionIds"] as JArray;

            List<EdgeChainRegion> chains;
            int ownerInstance;
            Mesh ownerMesh;
            string sourceTag;
            if (idsArr != null && idsArr.Count > 0)
            {
                chains = ResolveChainsFromIds(idsArr, out ownerInstance, out ownerMesh, out var err);
                if (chains == null) return ToolResult.Error("BAD_ARGS", err);
                sourceTag = $"explicit:{idsArr.Count}";
            }
            else if (instanceId != 0)
            {
                if (!MeshResolver.TryGetMeshAndTransforms(instanceId, out _, out ownerMesh, out var ltw))
                    return ToolResult.Error("NOT_FOUND",
                        $"no MeshFilter/SkinnedMeshRenderer mesh on GameObject {instanceId}");
                if (!MeshResolver.IsReadable(ownerMesh, out var notReadable))
                    return ToolResult.Error("MESH_NOT_READABLE", notReadable);

                // Run sharp-edge detection on the fly with sensible defaults.
                float dihedral = (float?)args["dihedralAngleMin"] ?? 30f;
                var sep = new SharpEdgeDetector.Params { DihedralAngleMinDeg = dihedral };
                string meshKeyEdges = MeshResolver.MakeMeshKey(ownerMesh);
                string edgesHash = $"edge:{sep.DihedralAngleMinDeg:F2}:{sep.MinChainLength:F3}:{sep.MaxChains}";
                string edgesCacheKey = MeshAnalysisCache.MakeKey(meshKeyEdges, edgesHash);
                if (!MeshAnalysisCache.TryGet(edgesCacheKey, out var edgesBundle))
                {
                    edgesBundle = new AnalysisResult { MeshGuid = meshKeyEdges, OwnerInstanceId = instanceId };
                    edgesBundle.EdgeChains.AddRange(SharpEdgeDetector.Detect(ownerMesh, ltw, meshKeyEdges, edgesHash, instanceId, sep));
                    MeshAnalysisCache.Store(edgesCacheKey, edgesBundle);
                }
                chains = edgesBundle.EdgeChains;
                ownerInstance = instanceId;
                sourceTag = $"auto:{chains.Count}";
            }
            else
            {
                return ToolResult.Error("BAD_ARGS", "provide either instanceId or chainRegionIds");
            }

            var p = new ParallelChainDetector.Params
            {
                DirectionAngleToleranceDeg = (float?)args["directionAngleTolerance"] ?? 10f,
                SpacingTolerance = (float?)args["spacingTolerance"] ?? 0.25f,
                MinSpacing = (float?)args["minSpacing"] ?? 0.05f,
                MinMembers = (int?)args["minMembers"] ?? 2,
                MaxGroups = (int?)args["maxGroups"] ?? 16,
            };

            string meshKey = MeshResolver.MakeMeshKey(ownerMesh);
            string paramsHash = $"parallel:{sourceTag}:{p.DirectionAngleToleranceDeg:F2}:{p.SpacingTolerance:F3}:{p.MinSpacing:F3}:{p.MinMembers}:{p.MaxGroups}";
            string cacheKey = MeshAnalysisCache.MakeKey(meshKey, paramsHash);

            if (!MeshAnalysisCache.TryGet(cacheKey, out var bundle))
            {
                bundle = new AnalysisResult { MeshGuid = meshKey, OwnerInstanceId = ownerInstance };
                bundle.ParallelGroups.AddRange(ParallelChainDetector.Detect(chains, meshKey, paramsHash, ownerInstance, p));
                MeshAnalysisCache.Store(cacheKey, bundle);
            }

            var arr = new JArray();
            foreach (var g in bundle.ParallelGroups)
            {
                arr.Add(new JObject
                {
                    ["regionId"] = g.RegionId,
                    ["direction"] = new JArray { g.DirectionWorld.x, g.DirectionWorld.y, g.DirectionWorld.z },
                    ["meanSpacing"] = g.MeanSpacing,
                    ["spacingStdDev"] = g.SpacingStdDev,
                    ["instanceCount"] = g.InstanceCount,
                    ["childChainRegionIds"] = new JArray(g.ChildChainRegionIds),
                    ["worldBounds"] = BoundsJson(g.WorldBounds),
                });
            }
            var payload = new JObject
            {
                ["sourceChainCount"] = chains.Count,
                ["groupCount"] = bundle.ParallelGroups.Count,
                ["groups"] = arr,
            };
            return ToolResult.Ok(
                $"{bundle.ParallelGroups.Count} parallel group(s) from {chains.Count} chain(s)",
                payload);
        }

        private static List<EdgeChainRegion> ResolveChainsFromIds(JArray idsArr,
            out int ownerInstance, out Mesh ownerMesh, out string err)
        {
            ownerInstance = 0; ownerMesh = null; err = null;
            var chains = new List<EdgeChainRegion>();
            foreach (var t in idsArr)
            {
                var id = (string)t;
                if (string.IsNullOrEmpty(id)) continue;
                var region = MeshAnalysisCache.FindRegion(id, out _);
                if (region == null)
                {
                    err = $"chainRegionId '{id}' not in cache — run mesh_detect_sharp_edges first";
                    return null;
                }
                if (!(region is EdgeChainRegion ec))
                {
                    err = $"regionId '{id}' is not an edge_chain (kind={region.Kind})";
                    return null;
                }
                chains.Add(ec);
                ownerInstance = ec.OwnerInstanceId;
            }
            if (chains.Count == 0) { err = "chainRegionIds was empty after filtering"; return null; }

            ownerMesh = MeshResolver.ResolveMesh(ownerInstance);
            if (ownerMesh == null) { err = "owner GameObject is no longer in scene"; return null; }
            return chains;
        }

        private static JObject BoundsJson(Bounds b) => new JObject
        {
            ["center"] = new JArray { b.center.x, b.center.y, b.center.z },
            ["size"] = new JArray { b.size.x, b.size.y, b.size.z },
        };
    }
}
