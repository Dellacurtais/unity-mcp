using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Reverse mapping from an image-space (u,v) pick on a previously
    /// rendered view back to a 3D point + face + region(s) on the target
    /// mesh. Closes the loop: render_object_views → LLM picks pixels →
    /// mesh_pick_from_screen tells what's there.
    ///
    /// Inputs:
    ///   - `instanceId` : same GameObject that was rendered
    ///   - `cameraToWorld` + `projection` : 16-float row-major matrices
    ///     copied verbatim from view_metadata of the render call
    ///   - `uv` : [u, v] in [0,1] (top-left origin, matches the PNG)
    ///
    /// Output: world point, world normal, hit triangle index, submesh
    /// index, distance, and any regionIds in the analytics cache whose
    /// faceIndices contain that triangle (so the LLM can say "you clicked
    /// inside the platform_candidate plane").
    public sealed class MeshPickFromScreenTool : IMcpTool
    {
        public string Name => "mesh_pick_from_screen";
        public string Description =>
            "Given a (u,v) point in [0,1] on an image previously produced by render_object_views (along with that view's cameraToWorld and projection matrices), raycasts back into the scene and reports the world point, world normal, mesh triangle index, submesh index, and any cached regionIds (planes/edges/smooth/submeshes) that contain the hit triangle. Closes the visual feedback loop: 'I saw something at (u,v) — what is it?'";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = MeshToolSchemas.Target,
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["uv"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 2,
                    ["maxItems"] = 2,
                    ["description"] = "[u, v] in [0,1], top-left origin to match the rendered PNG.",
                },
                ["cameraToWorld"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 16,
                    ["maxItems"] = 16,
                    ["description"] = "Row-major 4×4 matrix from view_metadata.",
                },
                ["projection"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 16,
                    ["maxItems"] = 16,
                    ["description"] = "Row-major 4×4 projection from view_metadata.",
                },
                ["maxDistance"] = new JObject { ["type"] = "number", ["default"] = 0.0, ["description"] = "Metres. 0 or omitted = unbounded." },
            },
            ["required"] = new JArray { "uv", "cameraToWorld", "projection" },
            ["oneOf"] = MeshToolSchemas.TargetOrInstance,
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => MeshToolSchemas.PickOutput;

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var go = MeshResolver.ResolveGameObject(args, out var targetError);
            if (go == null) return ToolResult.Error("NOT_FOUND", targetError);
            int instanceId = go.GetMcpInstanceId();

            var mesh = MeshResolver.ResolveMesh(go);
            if (mesh == null) return ToolResult.Error("NOT_FOUND", $"GameObject {instanceId} has no mesh");
            if (!MeshResolver.IsReadable(mesh, out var notReadable))
                return ToolResult.Error("MESH_NOT_READABLE", notReadable);
            if (!MeshResolver.HasTriangleTopology(mesh, out var topologyError))
                return ToolResult.Error("UNSUPPORTED_TOPOLOGY", topologyError);

            if (!TryParseUv(args["uv"], out float u, out float v))
                return ToolResult.Error("BAD_ARGS", "uv must be [u,v] in [0,1]");
            if (!TryParseMatrix(args["cameraToWorld"], out var c2w))
                return ToolResult.Error("BAD_ARGS", "cameraToWorld must be 16 numbers (row-major)");
            if (!TryParseMatrix(args["projection"], out var proj))
                return ToolResult.Error("BAD_ARGS", "projection must be 16 numbers (row-major)");

            var ray = MeshPicker.RayFromUv(c2w, proj, u, v);
            float maxDist = (float?)args["maxDistance"] ?? 0f;
            if (!MeshPicker.Pick(go, ray, maxDist, out var pick))
                return ToolResult.Error("NO_HIT", $"ray missed mesh '{mesh.name}'");

            var payload = new JObject
            {
                ["target"] = MeshResolver.TargetData(go),
                ["instanceId"] = instanceId,
                ["worldPoint"] = new JArray { pick.WorldPoint.x, pick.WorldPoint.y, pick.WorldPoint.z },
                ["worldNormal"] = new JArray { pick.WorldNormal.x, pick.WorldNormal.y, pick.WorldNormal.z },
                ["triangleIndex"] = pick.TriangleIndex,
                ["submeshIndex"] = pick.SubmeshIndex,
                ["distanceFromCamera"] = pick.DistanceFromCamera,
                ["regionIds"] = new JArray(pick.RegionIds),
                ["rayOrigin"] = new JArray { ray.origin.x, ray.origin.y, ray.origin.z },
                ["rayDirection"] = new JArray { ray.direction.x, ray.direction.y, ray.direction.z },
            };
            string regionList = pick.RegionIds.Length > 0 ? $", {pick.RegionIds.Length} region(s)" : "";
            return ToolResult.Ok(
                $"hit '{go.name}' at ({pick.WorldPoint.x:F2},{pick.WorldPoint.y:F2},{pick.WorldPoint.z:F2}) tri={pick.TriangleIndex} submesh={pick.SubmeshIndex}{regionList}",
                payload);
        }

        private static bool TryParseUv(JToken t, out float u, out float v)
        {
            u = v = 0f;
            if (!(t is JArray a) || a.Count < 2) return false;
            u = (float)a[0]; v = (float)a[1];
            return u >= 0f && u <= 1f && v >= 0f && v <= 1f;
        }

        private static bool TryParseMatrix(JToken t, out Matrix4x4 m)
        {
            m = Matrix4x4.identity;
            if (!(t is JArray a) || a.Count < 16) return false;
            m.m00 = (float)a[0];  m.m01 = (float)a[1];  m.m02 = (float)a[2];  m.m03 = (float)a[3];
            m.m10 = (float)a[4];  m.m11 = (float)a[5];  m.m12 = (float)a[6];  m.m13 = (float)a[7];
            m.m20 = (float)a[8];  m.m21 = (float)a[9];  m.m22 = (float)a[10]; m.m23 = (float)a[11];
            m.m30 = (float)a[12]; m.m31 = (float)a[13]; m.m32 = (float)a[14]; m.m33 = (float)a[15];
            return true;
        }
    }
}
