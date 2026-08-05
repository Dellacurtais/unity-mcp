using System;
using System.Collections.Generic;
using System.Diagnostics;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Fast, agent-facing object capture. The target is cloned to a temporary
    /// hidden layer by default, so the result contains only the requested
    /// hierarchy without changing the source scene or prefab.
    public sealed class ScreenshotObjectTool : ObjectScreenshotToolBase
    {
        public override string Name => "screenshot_object";
        public override string Description =>
            "Quickly captures one GameObject or prefab asset without moving the Scene View or creating project cameras. Automatically frames the complete hierarchy and returns inline PNG images in one MCP call. Supports iso/front/back/top/left/right views, custom camera poses, resolution, and lit/wireframe/submesh overlays. Isolated capture is enabled by default and does not modify the source object.";

        protected override bool DefaultIsolated => true;
    }

    /// Renders a specific GameObject from N camera angles with optional
    /// material overlays. Returns PNG image content blocks plus per-view
    /// camera matrices so the LLM (or a follow-up tool) can correlate
    /// pixels back to 3D points.
    ///
    /// Unlike `screenshot_scene_view`, this tool drives a private off-screen
    /// camera framed around the target's bounds — independent of the user's
    /// SceneView setup. Useful for "show me this house from front/back/iso".
    public sealed class RenderObjectViewsTool : ObjectScreenshotToolBase
    {
        public override string Name => "render_object_views";
        public override string Description =>
            "Renders a GameObject from one or more camera angles (presets: front/back/top/iso/left/right) and returns each as a PNG. Overlay modes: lit_default (normal shading), wireframe (light unlit), submesh_color (per-submesh tint to reveal structure). Each view also returns camera-to-world and projection matrices for 2D→3D correlation.";

        // Keep world-space rendering as the compatibility default because
        // mesh_pick_from_screen consumes these matrices against the source.
        protected override bool DefaultIsolated => false;
        protected override string ToolCategory => "render";
    }

    public abstract class ObjectScreenshotToolBase : IMcpTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        protected abstract bool DefaultIsolated { get; }
        protected virtual string ToolCategory => "screenshot";

        public JObject InputSchema => ObjectScreenshotSchemas.Build(DefaultIsolated);

        public JObject OutputSchema => MeshToolSchemas.RenderViewsOutput;

        public bool IsWrite => false;

        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = ToolCategory,
            Capabilities = McpCapability.Read,
            Risk = McpRiskLevel.Low,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.Any,
            IsIdempotent = true,
            ExecutionRequirements = McpExecutionRequirement.MainThread,
            // Rendering needs a graphics device even though it does not need
            // a visible Scene View or Game View.
            HeadlessSafe = false,
        };

        public ToolResult Execute(JObject args)
        {
            var go = MeshResolver.ResolveGameObject(args, out var targetError);
            if (go == null) return ToolResult.Error("NOT_FOUND", targetError);

            int width = Mathf.Clamp((int?)args["width"] ?? 1024, 64, 2048);
            int height = Mathf.Clamp((int?)args["height"] ?? 768, 64, 2048);
            bool isolated = (bool?)args["isolated"] ?? DefaultIsolated;
            var overlay = ParseOverlay((string)args["overlay"]);
            var requests = ParseViews(args["views"]);

            // Hard cap to keep payload bounded for the LLM while still
            // batching the common six-view inspection into one round trip.
            if (requests.Count > 6) requests = requests.GetRange(0, 6);
            if (requests.Count == 0) requests.Add(new OffscreenRenderer.ViewRequest { Preset = OffscreenRenderer.ViewPreset.Iso });

            List<OffscreenRenderer.Result> renders;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                renders = OffscreenRenderer.Render(go, requests, width, height, overlay, null, isolated);
            }
            catch (Exception e)
            {
                return ToolResult.Error("RENDER_FAILED", e.Message);
            }
            finally
            {
                stopwatch.Stop();
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
                ["text"] = $"{renders.Count} object screenshot(s) of '{go.name}' ({width}×{height}, isolated={isolated.ToString().ToLowerInvariant()}, overlay={overlay.ToString().ToLowerInvariant()})",
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
                Text = $"captured {renders.Count} view(s) of '{go.name}'",
                Json = new JObject
                {
                    ["target"] = MeshResolver.TargetData(go),
                    ["width"] = width,
                    ["height"] = height,
                    ["overlay"] = overlay.ToString().ToLowerInvariant(),
                    ["isolated"] = isolated,
                    ["elapsedMs"] = stopwatch.Elapsed.TotalMilliseconds,
                    ["views"] = metaArr,
                },
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

    internal static class ObjectScreenshotSchemas
    {
        public static JObject Build(bool defaultIsolated) => new JObject
        {
            ["type"] = "object",
            ["properties"] = Properties(defaultIsolated),
            // Some MCP clients project the oneOf branches as the callable
            // signature. Repeat all options in each locator branch so views,
            // resolution, overlay, and isolation remain callable.
            ["oneOf"] = new JArray
            {
                LocatorBranch("target", defaultIsolated),
                LocatorBranch("instanceId", defaultIsolated),
            },
            ["additionalProperties"] = false,
        };

        private static JObject LocatorBranch(string requiredProperty, bool defaultIsolated) => new JObject
        {
            ["type"] = "object",
            ["properties"] = Properties(defaultIsolated),
            ["required"] = new JArray { requiredProperty },
            ["additionalProperties"] = false,
        };

        private static JObject Properties(bool defaultIsolated) => new JObject
        {
            ["target"] = MeshToolSchemas.Target,
            ["instanceId"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = "Session-local GameObject instance id fallback.",
            },
            ["views"] = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject
                {
                    ["oneOf"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray { "iso", "front", "back", "top", "left", "right" },
                        },
                        new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["cameraPos"] = Vector3Schema(),
                                ["lookAt"] = Vector3Schema(),
                            },
                            ["required"] = new JArray { "cameraPos" },
                            ["additionalProperties"] = false,
                        },
                    },
                },
                ["default"] = new JArray { "iso" },
                ["maxItems"] = 6,
                ["description"] = "Preset strings or custom poses. Defaults to one isometric shot; up to six views are rendered in one MCP call.",
            },
            ["width"] = new JObject { ["type"] = "integer", ["default"] = 1024, ["minimum"] = 64, ["maximum"] = 2048 },
            ["height"] = new JObject { ["type"] = "integer", ["default"] = 768, ["minimum"] = 64, ["maximum"] = 2048 },
            ["overlay"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "lit_default", "wireframe", "submesh_color" }, ["default"] = "lit_default" },
            ["isolated"] = new JObject
            {
                ["type"] = "boolean",
                ["default"] = defaultIsolated,
                ["description"] = "Render a temporary hidden clone on an isolated layer. The source scene object or prefab is never modified.",
            },
        };

        private static JObject Vector3Schema() => new JObject
        {
            ["type"] = "array",
            ["items"] = new JObject { ["type"] = "number" },
            ["minItems"] = 3,
            ["maxItems"] = 3,
        };
    }
}
