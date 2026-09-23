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

        public static JObject StrictVec2Schema => new JObject
        {
            ["description"] = "Vector2 as [x,y] or {x,y}; object form requires both coordinates.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2 },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["x"] = new JObject { ["type"] = "number" },
                        ["y"] = new JObject { ["type"] = "number" },
                    },
                    ["required"] = new JArray { "x", "y" },
                    ["additionalProperties"] = false,
                },
            },
        };

        public static JObject NonNegativeVec2Schema => new JObject
        {
            ["description"] = "Non-negative Vector2 as [x,y] or {x,y}.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number", ["minimum"] = 0 }, ["minItems"] = 2, ["maxItems"] = 2 },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["x"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                        ["y"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                    },
                    ["required"] = new JArray { "x", "y" },
                    ["additionalProperties"] = false,
                },
            },
        };

        public static JObject NonNegativeVec3Schema => new JObject
        {
            ["description"] = "Non-negative Vector3 as [x,y,z] or {x,y,z}.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number", ["minimum"] = 0 }, ["minItems"] = 3, ["maxItems"] = 3 },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["x"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                        ["y"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                        ["z"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                    },
                    ["required"] = new JArray { "x", "y", "z" },
                    ["additionalProperties"] = false,
                },
            },
        };

        public static JObject LayerMaskSchema => new JObject
        {
            ["description"] = "Layer mask as an integer bitmask, one layer name, or a non-empty array of layer names.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue },
                new JObject { ["type"] = "string", ["minLength"] = 1 },
                new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                    ["minItems"] = 1,
                },
            },
        };

        public static JObject TriggerInteractionSchema => new JObject
        {
            ["description"] = "Trigger handling as true/false or UseGlobal/Collide/Ignore.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "boolean" },
                new JObject { ["type"] = "string", ["enum"] = new JArray { "UseGlobal", "Collide", "Ignore" } },
            },
        };

        public static JObject TargetRefSchema => new JObject
        {
            ["type"] = "object",
            ["description"] = "Unity target reference. Preserve captureDomainId and instanceId from read results to detect replacement within the current domain; globalObjectId/assetGuid support resolution after reload.",
            ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string" },
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["captureDomainId"] = new JObject { ["type"] = "string" },
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
