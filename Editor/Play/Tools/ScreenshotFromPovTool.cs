using System;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class ScreenshotFromPovTool : IMcpTool
    {
        public string Name => "screenshot_from_pov";
        public string Description =>
            "Spawns a hidden temporary camera at the given position/rotation, renders it, then destroys it — no GameObject left in the scene. " +
            "Use to inspect the scene from any angle without moving the player. Combine `position` with `lookAt` to point at a target, or use `rotationEuler` for explicit orientation. " +
            "In play mode the temp camera inherits cullingMask and (best-effort, URP only) post-fx settings from Camera.main, so Volume blending applies. In edit mode, post-fx may be absent in URP/HDRP.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["position"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 3,
                    ["maxItems"] = 3,
                },
                ["rotationEuler"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 3,
                    ["maxItems"] = 3,
                },
                ["lookAt"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 3,
                    ["maxItems"] = 3,
                },
                ["fov"] = new JObject { ["type"] = "number", ["default"] = 60, ["minimum"] = 1, ["maximum"] = 179 },
                ["isOrthographic"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["orthographicSize"] = new JObject { ["type"] = "number", ["default"] = 5, ["minimum"] = 0.0001 },
                ["nearClip"] = new JObject { ["type"] = "number", ["default"] = 0.1, ["exclusiveMinimum"] = 0 },
                ["farClip"] = new JObject { ["type"] = "number", ["default"] = 1000, ["exclusiveMinimum"] = 0 },
                ["width"] = new JObject { ["type"] = "integer", ["default"] = 1280, ["minimum"] = 64, ["maximum"] = 4096 },
                ["height"] = new JObject { ["type"] = "integer", ["default"] = 720, ["minimum"] = 64, ["maximum"] = 4096 },
                ["includePostFx"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["encoding"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "png", "jpg" }, ["default"] = "png" },
                ["jpgQuality"] = new JObject { ["type"] = "integer", ["default"] = 80, ["minimum"] = 1, ["maximum"] = 100 },
            },
            ["required"] = new JArray { "position" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            // Synchronous path: temp camera + Camera.Render(), no end-of-frame
            // wait. Dispatcher already marshals this onto the main thread.
            var spec = new PovCameraFactory.PovSpec();

            if (!TryReadVec3(args["position"], out spec.Position))
                return ToolResult.Error("BAD_REQUEST", "'position' must contain three finite numbers.");

            var hasLookAt = args["lookAt"] != null && args["lookAt"].Type != JTokenType.Null;
            var hasRotation = args["rotationEuler"] != null && args["rotationEuler"].Type != JTokenType.Null;
            if (hasLookAt && hasRotation)
                return ToolResult.Error("AMBIGUOUS_ORIENTATION", "pass either 'lookAt' or 'rotationEuler', not both.");
            if (hasLookAt)
            {
                if (!TryReadVec3(args["lookAt"], out spec.LookAt))
                    return ToolResult.Error("BAD_REQUEST", "'lookAt' must contain three finite numbers.");
                spec.HasLookAt = true;
            }
            else if (hasRotation)
            {
                if (!TryReadVec3(args["rotationEuler"], out var euler))
                    return ToolResult.Error("BAD_REQUEST", "'rotationEuler' must contain three finite numbers.");
                spec.Rotation = Quaternion.Euler(euler.x, euler.y, euler.z);
            }

            if (!TryReadFiniteFloat(args, "fov", 60f, out spec.FieldOfView)
                || spec.FieldOfView < 1f || spec.FieldOfView > 179f)
                return ToolResult.Error("BAD_REQUEST", "'fov' must be finite and in the range 1..179.");
            spec.IsOrthographic = (bool?)args["isOrthographic"] ?? false;
            if (!TryReadFiniteFloat(args, "orthographicSize", 5f, out spec.OrthographicSize)
                || spec.OrthographicSize <= 0f)
                return ToolResult.Error("BAD_REQUEST", "'orthographicSize' must be finite and greater than zero.");
            if (!TryReadFiniteFloat(args, "nearClip", 0.1f, out spec.NearClip)
                || !TryReadFiniteFloat(args, "farClip", 1000f, out spec.FarClip)
                || spec.NearClip <= 0f || spec.FarClip <= spec.NearClip)
                return ToolResult.Error("INVALID_CLIP_PLANES", "clip planes must be finite and satisfy 0 < nearClip < farClip.");
            spec.IncludePostFx = (bool?)args["includePostFx"] ?? true;

            int width = Mathf.Clamp((int?)args["width"] ?? 1280, 64, 4096);
            int height = Mathf.Clamp((int?)args["height"] ?? 720, 64, 4096);
            string encoding = ((string)args["encoding"] ?? "png").ToLowerInvariant();
            int jpgQuality = Mathf.Clamp((int?)args["jpgQuality"] ?? 80, 1, 100);

            try
            {
                using (var scope = PovCameraFactory.CreateScope(spec, Camera.main))
                {
                    var sourceDesc = $"POV camera at ({spec.Position.x:F2}, {spec.Position.y:F2}, {spec.Position.z:F2})";
                    var result = GameViewCapture.CaptureSync(scope.Camera, width, height,
                        encoding, jpgQuality, sourceDesc);

                    if (result == null || result.Bytes == null || result.Bytes.Length == 0)
                        return ToolResult.Error("CAPTURE_FAILED", "POV render returned 0 bytes.");

                    string b64 = Convert.ToBase64String(result.Bytes);
                    string summary = $"{result.SourceDescription} — {result.Width}×{result.Height}, " +
                                     $"{result.Bytes.Length:N0} bytes ({result.MimeType}), renderPath={result.RenderPath}";
                    return ToolResult.Image(b64, result.MimeType, summary);
                }
            }
            catch (Exception e)
            {
                return ToolResult.Error("CAPTURE_FAILED", $"POV render threw: {e.Message}");
            }
        }

        private static bool TryReadVec3(JToken token, out Vector3 value)
        {
            value = default;
            if (!(token is JArray array) || array.Count != 3) return false;
            try
            {
                value = new Vector3((float)(double)array[0], (float)(double)array[1], (float)(double)array[2]);
                return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
            }
            catch { return false; }
        }

        private static bool TryReadFiniteFloat(JObject args, string key, float fallback, out float value)
        {
            value = fallback;
            var token = args[key];
            if (token == null || token.Type == JTokenType.Null) return true;
            try
            {
                value = (float)(double)token;
                return IsFinite(value);
            }
            catch { return false; }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
