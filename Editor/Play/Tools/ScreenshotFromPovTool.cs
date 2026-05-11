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
                ["orthographicSize"] = new JObject { ["type"] = "number", ["default"] = 5 },
                ["nearClip"] = new JObject { ["type"] = "number", ["default"] = 0.1 },
                ["farClip"] = new JObject { ["type"] = "number", ["default"] = 1000 },
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

            var posArr = args["position"] as JArray;
            if (posArr == null || posArr.Count != 3)
                return ToolResult.Error("BAD_REQUEST", "'position' must be a [x,y,z] array.");
            spec.Position = ReadVec3(posArr);

            if (args["lookAt"] is JArray lookAtArr && lookAtArr.Count == 3)
            {
                spec.HasLookAt = true;
                spec.LookAt = ReadVec3(lookAtArr);
            }
            else if (args["rotationEuler"] is JArray rotArr && rotArr.Count == 3)
            {
                var eul = ReadVec3(rotArr);
                spec.Rotation = Quaternion.Euler(eul.x, eul.y, eul.z);
            }

            spec.FieldOfView = Mathf.Clamp(ReadFloat(args, "fov", 60f), 1f, 179f);
            spec.IsOrthographic = (bool?)args["isOrthographic"] ?? false;
            spec.OrthographicSize = ReadFloat(args, "orthographicSize", 5f);
            spec.NearClip = ReadFloat(args, "nearClip", 0.1f);
            spec.FarClip = ReadFloat(args, "farClip", 1000f);
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
                                     $"{result.Bytes.Length:N0} bytes ({result.MimeType})";
                    return ToolResult.Image(b64, result.MimeType, summary);
                }
            }
            catch (Exception e)
            {
                return ToolResult.Error("CAPTURE_FAILED", $"POV render threw: {e.Message}");
            }
        }

        private static Vector3 ReadVec3(JArray arr)
        {
            return new Vector3(
                (float)(double)arr[0],
                (float)(double)arr[1],
                (float)(double)arr[2]);
        }

        private static float ReadFloat(JObject args, string key, float fallback)
        {
            var token = args[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return (float)(double)token; } catch { return fallback; }
        }
    }
}
