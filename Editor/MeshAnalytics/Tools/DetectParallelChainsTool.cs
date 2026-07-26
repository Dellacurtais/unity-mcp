using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
                ["target"] = MeshToolSchemas.Target,
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
            ["oneOf"] = MeshToolSchemas.TargetInstanceOrRegionIds,
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => MeshToolSchemas.ParallelGroupsOutput;

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            JArray idsArr = args["chainRegionIds"] as JArray;
            bool hasExplicitIds = idsArr != null && idsArr.Count > 0;
            bool hasTarget = args["target"] != null || (int?)args["instanceId"] != null;
            if (hasExplicitIds == hasTarget)
                return ToolResult.Error("BAD_ARGS", "provide exactly one of target/instanceId or chainRegionIds");

            List<EdgeChainRegion> chains;
            GameObject ownerGo;
            Mesh ownerMesh;
            Matrix4x4 ownerLtw;
            string sourceTag;
            if (hasExplicitIds)
            {
                chains = ResolveChainsFromIds(idsArr, out ownerGo, out ownerMesh, out var err);
                if (chains == null) return ToolResult.Error("BAD_ARGS", err);
                ownerLtw = ownerGo.transform.localToWorldMatrix;
                sourceTag = "explicit:" + HashRegionIds(idsArr);
            }
            else
            {
                if (!MeshResolver.TryGetMeshAndTransforms(args, out ownerGo, out ownerMesh, out ownerLtw, out var targetError))
                    return ToolResult.Error("NOT_FOUND", targetError);
                if (!MeshResolver.IsReadable(ownerMesh, out var notReadable))
                    return ToolResult.Error("MESH_NOT_READABLE", notReadable);
                if (!MeshResolver.HasTriangleTopology(ownerMesh, out var topologyError))
                    return ToolResult.Error("UNSUPPORTED_TOPOLOGY", topologyError);

                float dihedral = (float?)args["dihedralAngleMin"] ?? 30f;
                var sep = new SharpEdgeDetector.Params { DihedralAngleMinDeg = dihedral };
                string meshKeyEdges = MeshResolver.MakeMeshKey(ownerMesh);
                string analysisKeyEdges = MeshResolver.MakeAnalysisKey(ownerMesh, ownerGo);
                string edgesHash = $"edge:{sep.DihedralAngleMinDeg:F2}:{sep.MinChainLength:F3}:{sep.MaxChains}";
                if (!MeshAnalysisCache.TryGetWithFallback(analysisKeyEdges, edgesHash, ownerGo, ownerLtw, out var edgesBundle))
                {
                    edgesBundle = MeshAnalysisCache.CreateResult(meshKeyEdges, analysisKeyEdges, ownerGo, ownerLtw);
                    edgesBundle.EdgeChains.AddRange(SharpEdgeDetector.Detect(ownerMesh, ownerLtw,
                        analysisKeyEdges, edgesHash, ownerGo.GetInstanceID(), sep));
                    MeshAnalysisCache.StoreAndPersist(analysisKeyEdges, edgesHash, edgesBundle);
                }
                chains = edgesBundle.EdgeChains;
                sourceTag = $"auto:{chains.Count}";
            }
            int ownerInstance = ownerGo.GetInstanceID();

            var p = new ParallelChainDetector.Params
            {
                DirectionAngleToleranceDeg = (float?)args["directionAngleTolerance"] ?? 10f,
                SpacingTolerance = (float?)args["spacingTolerance"] ?? 0.25f,
                MinSpacing = (float?)args["minSpacing"] ?? 0.05f,
                MinMembers = (int?)args["minMembers"] ?? 2,
                MaxGroups = (int?)args["maxGroups"] ?? 16,
            };

            string meshKey = MeshResolver.MakeMeshKey(ownerMesh);
            string analysisKey = MeshResolver.MakeAnalysisKey(ownerMesh, ownerGo);
            string paramsHash = $"parallel:{sourceTag}:{p.DirectionAngleToleranceDeg:F2}:{p.SpacingTolerance:F3}:{p.MinSpacing:F3}:{p.MinMembers}:{p.MaxGroups}";

            if (!MeshAnalysisCache.TryGetWithFallback(analysisKey, paramsHash, ownerGo, ownerLtw, out var bundle))
            {
                bundle = MeshAnalysisCache.CreateResult(meshKey, analysisKey, ownerGo, ownerLtw);
                bundle.ParallelGroups.AddRange(ParallelChainDetector.Detect(chains, analysisKey, paramsHash, ownerInstance, p));
                MeshAnalysisCache.StoreAndPersist(analysisKey, paramsHash, bundle);
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
            out GameObject ownerGo, out Mesh ownerMesh, out string err)
        {
            ownerGo = null; ownerMesh = null; err = null;
            AnalysisResult commonOwner = null;
            var chains = new List<EdgeChainRegion>();
            foreach (var token in idsArr)
            {
                var id = (string)token;
                if (string.IsNullOrEmpty(id)) continue;
                var region = MeshAnalysisCache.FindRegion(id, out var owner);
                if (region == null)
                {
                    err = $"chainRegionId '{id}' not in cache — run mesh_detect_sharp_edges first";
                    return null;
                }
                if (!(region is EdgeChainRegion edgeChain))
                {
                    err = $"regionId '{id}' is not an edge_chain (kind={region.Kind})";
                    return null;
                }
                if (commonOwner != null && !string.Equals(commonOwner.AnalysisKey, owner.AnalysisKey, System.StringComparison.Ordinal))
                {
                    err = "all chainRegionIds must belong to the same mesh instance";
                    return null;
                }
                commonOwner = owner;
                chains.Add(edgeChain);
            }
            if (chains.Count == 0) { err = "chainRegionIds was empty after filtering"; return null; }

            ownerGo = MeshAnalysisCache.ResolveOwner(commonOwner, out err);
            if (ownerGo == null) return null;
            ownerMesh = MeshResolver.ResolveMesh(ownerGo);
            if (ownerMesh == null) { err = "analysis owner no longer has a mesh"; return null; }
            return chains;
        }

        private static string HashRegionIds(JArray ids)
        {
            var values = new List<string>();
            foreach (var token in ids)
            {
                var value = (string)token;
                if (!string.IsNullOrEmpty(value)) values.Add(value);
            }
            values.Sort(System.StringComparer.Ordinal);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", values)));
            var builder = new StringBuilder(16);
            for (int i = 0; i < 8; i++) builder.Append(bytes[i].ToString("x2"));
            return builder.ToString();
        }

        private static JObject BoundsJson(Bounds b) => new JObject
        {
            ["center"] = new JArray { b.center.x, b.center.y, b.center.z },
            ["size"] = new JArray { b.size.x, b.size.y, b.size.z },
        };
    }
}
