using System;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Renders a GameObject from one camera angle with a specific region
    /// drawn on top: edge chains as orange polylines, plane/submesh regions
    /// as a translucent orange face overlay. The visual analogue to the
    /// user's "orange line" reference — lets the LLM confirm what a detector
    /// found by literally seeing it.
    public sealed class RenderRegionHighlightTool : IMcpTool
    {
        public string Name => "render_region_highlight";
        public string Description =>
            "Renders a GameObject with a specific region highlighted in orange (polyline for edge chains, translucent overlay for planes/submeshes). Use after mesh_detect_sharp_edges / mesh_detect_planes / mesh_inspect_submeshes to visually confirm what the detector found — the LLM then describes the highlighted area in natural language. Single view per call.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["regionId"] = new JObject { ["type"] = "string" },
                ["view"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "iso", "front", "back", "top", "left", "right" }, ["default"] = "iso" },
                ["width"] = new JObject { ["type"] = "integer", ["default"] = 1024, ["minimum"] = 64, ["maximum"] = 2048 },
                ["height"] = new JObject { ["type"] = "integer", ["default"] = 768, ["minimum"] = 64, ["maximum"] = 2048 },
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
                    $"regionId '{regionId}' not in cache. Re-run the producing detector first.");

            var go = MeshResolver.ResolveGameObject(region.OwnerInstanceId);
            if (go == null)
                return ToolResult.Error("NOT_FOUND",
                    $"GameObject for region (instanceId={region.OwnerInstanceId}) is no longer in scene");

            int width = Mathf.Clamp((int?)args["width"] ?? 1024, 64, 2048);
            int height = Mathf.Clamp((int?)args["height"] ?? 768, 64, 2048);

            var viewReq = new OffscreenRenderer.ViewRequest
            {
                Preset = ParsePreset((string)args["view"]),
            };

            System.Collections.Generic.List<OffscreenRenderer.Result> renders;
            try
            {
                renders = OffscreenRenderer.Render(go, new[] { viewReq }, width, height,
                    OffscreenRenderer.Overlay.LitDefault, region);
            }
            catch (Exception e)
            {
                return ToolResult.Error("RENDER_FAILED", e.Message);
            }
            if (renders.Count == 0)
                return ToolResult.Error("NO_GEOMETRY", $"GameObject '{go.name}' has no enabled renderer to frame");

            var r = renders[0];
            var summary = DescribeRegion(region);
            return ToolResult.Image(
                Convert.ToBase64String(r.Png),
                "image/png",
                $"{summary} on '{go.name}' — view {r.PresetName} ({width}×{height})");
        }

        private static OffscreenRenderer.ViewPreset ParsePreset(string s)
        {
            return (s ?? "iso").ToLowerInvariant() switch
            {
                "front" => OffscreenRenderer.ViewPreset.Front,
                "back" => OffscreenRenderer.ViewPreset.Back,
                "top" => OffscreenRenderer.ViewPreset.Top,
                "left" => OffscreenRenderer.ViewPreset.Left,
                "right" => OffscreenRenderer.ViewPreset.Right,
                _ => OffscreenRenderer.ViewPreset.Iso,
            };
        }

        private static string DescribeRegion(MeshRegion r) => r switch
        {
            EdgeChainRegion ec => $"edge chain (len={ec.TotalLength:F2}m, loop={ec.IsLoop})",
            PlaneRegion pl => $"plane ({pl.OrientationLabel}, {pl.Classification}, area≈{pl.AreaApprox:F2}m²)",
            SubmeshRegion sm => $"submesh {sm.SubmeshIndex} ({sm.MaterialName}, {sm.TriangleCount} tris)",
            _ => $"region {r?.Kind}",
        };
    }
}
