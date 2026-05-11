using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Renders a specific GameObject from N camera angles with optional
    /// material overlays. Returns PNG image content blocks plus per-view
    /// camera matrices so the LLM (or a follow-up tool) can correlate
    /// pixels back to 3D points.
    ///
    /// Unlike `screenshot_scene_view`, this tool drives a private off-screen
    /// camera framed around the target's bounds — independent of the user's
    /// SceneView setup. Useful for "show me this house from front/back/iso".
    public sealed class RenderObjectViewsTool : IMcpTool
    {
        public string Name => "render_object_views";
        public string Description =>
            "Renders a GameObject from one or more camera angles (presets: front/back/top/iso/left/right) and returns each as a PNG. Overlay modes: lit_default (normal shading), wireframe (light unlit), submesh_color (per-submesh tint to reveal structure). Each view also returns camera-to-world and projection matrices for 2D→3D correlation.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["views"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["oneOf"] = new JArray
                        {
                            new JObject { ["type"] = "string", ["enum"] = new JArray { "iso", "front", "back", "top", "left", "right" } },
                            new JObject { ["type"] = "object", ["properties"] = new JObject {
                                ["cameraPos"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                                ["lookAt"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                            }},
                        },
                    },
                    ["default"] = new JArray { "iso" },
                    ["description"] = "List of preset strings or custom poses. Defaults to ['iso']. Max 6 views per call.",
                },
                ["width"] = new JObject { ["type"] = "integer", ["default"] = 1024, ["minimum"] = 64, ["maximum"] = 2048 },
                ["height"] = new JObject { ["type"] = "integer", ["default"] = 768, ["minimum"] = 64, ["maximum"] = 2048 },
                ["overlay"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "lit_default", "wireframe", "submesh_color" }, ["default"] = "lit_default" },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            int instanceId = (int?)args["instanceId"] ?? 0;
            var go = MeshResolver.ResolveGameObject(instanceId);
            if (go == null) return ToolResult.Error("NOT_FOUND", $"no GameObject with instanceId {instanceId}");

            int width = Mathf.Clamp((int?)args["width"] ?? 1024, 64, 2048);
            int height = Mathf.Clamp((int?)args["height"] ?? 768, 64, 2048);
            var overlay = ParseOverlay((string)args["overlay"]);
            var requests = ParseViews(args["views"]);

            // Hard cap to keep payload bounded for the LLM.
            if (requests.Count > 6) requests = requests.GetRange(0, 6);
            if (requests.Count == 0) requests.Add(new OffscreenRenderer.ViewRequest { Preset = OffscreenRenderer.ViewPreset.Iso });

            List<OffscreenRenderer.Result> renders;
            try
            {
                renders = OffscreenRenderer.Render(go, requests, width, height, overlay, null);
            }
            catch (Exception e)
            {
                return ToolResult.Error("RENDER_FAILED", e.Message);
            }
            if (renders.Count == 0)
                return ToolResult.Error("NO_GEOMETRY", $"GameObject '{go.name}' has no enabled renderer to frame");

            // Build the MCP content array: one text summary, then one image
            // block per view. The metadata block carries the camera matrices
            // so the caller can post-process if needed.
            var content = new JArray();
            var metaArr = new JArray();
            content.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = $"{renders.Count} view(s) of '{go.name}' ({width}×{height}, overlay={overlay.ToString().ToLowerInvariant()})",
            });
            for (int i = 0; i < renders.Count; i++)
            {
                var r = renders[i];
                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["data"] = Convert.ToBase64String(r.Png),
                    ["mimeType"] = "image/png",
                });
                metaArr.Add(new JObject
                {
                    ["index"] = i,
                    ["preset"] = r.PresetName,
                    ["cameraPos"] = new JArray { r.CameraPos.x, r.CameraPos.y, r.CameraPos.z },
                    ["lookAt"] = new JArray { r.LookAt.x, r.LookAt.y, r.LookAt.z },
                    ["cameraToWorld"] = MatrixJson(r.CameraToWorld),
                    ["projection"] = MatrixJson(r.Projection),
                    ["bytes"] = r.Png.Length,
                });
            }
            // Append the per-view metadata as a final text block so the LLM
            // can read camera poses alongside the images.
            content.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = "view_metadata=" + new JObject { ["views"] = metaArr }.ToString(Newtonsoft.Json.Formatting.None),
            });

            return new ToolResult
            {
                Text = $"rendered {renders.Count} view(s) of '{go.name}'",
                ContentBlocks = content,
            };
        }

        private static OffscreenRenderer.Overlay ParseOverlay(string s)
        {
            return (s ?? "lit_default").ToLowerInvariant() switch
            {
                "wireframe" => OffscreenRenderer.Overlay.Wireframe,
                "submesh_color" => OffscreenRenderer.Overlay.SubmeshColor,
                _ => OffscreenRenderer.Overlay.LitDefault,
            };
        }

        private static List<OffscreenRenderer.ViewRequest> ParseViews(JToken token)
        {
            var list = new List<OffscreenRenderer.ViewRequest>();
            if (token == null || token.Type == JTokenType.Null) return list;
            if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (item.Type == JTokenType.String)
                    {
                        if (TryParsePreset((string)item, out var preset))
                            list.Add(new OffscreenRenderer.ViewRequest { Preset = preset });
                    }
                    else if (item is JObject o)
                    {
                        var req = new OffscreenRenderer.ViewRequest();
                        if (o["cameraPos"] is JArray cp && cp.Count >= 3)
                            req.CameraPos = new Vector3((float)cp[0], (float)cp[1], (float)cp[2]);
                        if (o["lookAt"] is JArray la && la.Count >= 3)
                            req.LookAt = new Vector3((float)la[0], (float)la[1], (float)la[2]);
                        list.Add(req);
                    }
                }
            }
            return list;
        }

        private static bool TryParsePreset(string s, out OffscreenRenderer.ViewPreset preset)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "iso": preset = OffscreenRenderer.ViewPreset.Iso; return true;
                case "front": preset = OffscreenRenderer.ViewPreset.Front; return true;
                case "back": preset = OffscreenRenderer.ViewPreset.Back; return true;
                case "top": preset = OffscreenRenderer.ViewPreset.Top; return true;
                case "left": preset = OffscreenRenderer.ViewPreset.Left; return true;
                case "right": preset = OffscreenRenderer.ViewPreset.Right; return true;
                default: preset = OffscreenRenderer.ViewPreset.Iso; return false;
            }
        }

        private static JArray MatrixJson(Matrix4x4 m)
        {
            // Row-major flatten — the LLM can reconstruct or feed back as-is.
            return new JArray {
                m.m00, m.m01, m.m02, m.m03,
                m.m10, m.m11, m.m12, m.m13,
                m.m20, m.m21, m.m22, m.m23,
                m.m30, m.m31, m.m32, m.m33,
            };
        }
    }
}
