using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools
{
    /// Shared JSON Schema fragments used across tool input schemas. Centralised
    /// so that pose/color shapes stay consistent across the MCP surface.
    /// Public because the secondary PostFx asmdef references it from its own
    /// tools (CreateVolumeTool etc.).
    public static class SchemaHelpers
    {
        public static JObject Vec3Schema => new JObject
        {
            ["description"] = "Vector3 as [x,y,z] or {x,y,z}.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                new JObject { ["type"] = "object" },
            },
        };

        public static JObject StrictVec3Schema => new JObject
        {
            ["description"] = "Vector3 as [x,y,z] or {x,y,z}; object form requires all three coordinates.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["x"] = new JObject { ["type"] = "number" },
                        ["y"] = new JObject { ["type"] = "number" },
                        ["z"] = new JObject { ["type"] = "number" },
                    },
                    ["required"] = new JArray { "x", "y", "z" },
                    ["additionalProperties"] = false,
                },
            },
        };

        public static JObject TargetRefSchema => new JObject
        {
            ["type"] = "object",
            ["description"] = "Stable Unity target reference. Prefer globalObjectId; instanceId remains a session-local fallback.",
            ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string" },
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["globalObjectId"] = new JObject { ["type"] = "string" },
                ["assetGuid"] = new JObject { ["type"] = "string" },
                ["localFileId"] = new JObject { ["type"] = "integer" },
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["sceneGuid"] = new JObject { ["type"] = "string" },
                ["scenePath"] = new JObject { ["type"] = "string" },
                ["sceneHandle"] = new JObject { ["type"] = "integer" },
                ["hierarchyPath"] = new JObject { ["type"] = "string" },
                ["componentType"] = new JObject { ["type"] = "string" },
                ["componentOrdinal"] = new JObject { ["type"] = "integer" },
                ["isPrefabStage"] = new JObject { ["type"] = "boolean" },
            },
            ["additionalProperties"] = false,
        };

        public static JObject RotSchema => new JObject
        {
            ["description"] = "Euler degrees [x,y,z] or quaternion [x,y,z,w] / {x,y,z,w}.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" } },
                new JObject { ["type"] = "object" },
            },
        };

        /// Color accepts: hex string ("#rrggbb"/"#rrggbbaa"), RGB[A] array
        /// ([r,g,b] or [r,g,b,a] in 0..1), or {r,g,b,a} object.
        /// Uses oneOf so OpenAI / strict-mode validators accept it (they reject
        /// `type: ["string", "array", "object"]` without a corresponding `items`
        /// for the array branch — which is what tripped 0.9.0 on Codex/GPT).
        public static JObject ColorSchema => new JObject
        {
            ["description"] = "Color: hex string '#rrggbb[aa]', RGB[A] array [r,g,b]/[r,g,b,a] in 0..1, or object {r,g,b,a}.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "string" },
                new JObject {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["minItems"] = 3,
                    ["maxItems"] = 4,
                },
                new JObject { ["type"] = "object" },
            },
        };
    }
}
