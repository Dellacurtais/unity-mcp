using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools
{
    public static class GameObjectAuthoringSchemas
    {
        public static JObject TargetResult => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["data"] = new JObject(),
            },
            ["required"] = new JArray { "instanceId", "target" },
            ["additionalProperties"] = false,
        };

        public static JObject DeletedResult => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["deleted"] = new JObject { ["type"] = "boolean" },
                ["descendantCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            },
            ["required"] = new JArray { "instanceId", "name", "deleted", "descendantCount" },
            ["additionalProperties"] = false,
        };

        public static JObject RectTransformResult => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["gameObject"] = SchemaHelpers.TargetRefSchema,
                ["componentType"] = new JObject { ["type"] = "string" },
                ["rect"] = new JObject { ["type"] = "object" },
            },
            ["required"] = new JArray { "target", "gameObject", "componentType", "rect" },
            ["additionalProperties"] = false,
        };

        public static JObject StaticFlagsResult => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["flags"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "target", "flags" },
            ["additionalProperties"] = false,
        };

        public static JObject ChangesetResult => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["preview"] = new JObject { ["type"] = "boolean" },
                ["failed"] = new JObject { ["type"] = "boolean" },
                ["error"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["failedAtIndex"] = new JObject { ["type"] = "integer" },
                ["opsApplied"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["receipt"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["outcome"] = new JObject { ["type"] = "string" },
                        ["rollbackScope"] = new JObject { ["type"] = "string" },
                        ["rollbackComplete"] = new JObject { ["type"] = new JArray { "boolean", "null" } },
                        ["rollbackErrors"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                    },
                    ["required"] = new JArray { "outcome", "rollbackScope", "rollbackComplete", "rollbackErrors" },
                    ["additionalProperties"] = false,
                },
                ["results"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "object" },
                },
            },
            ["required"] = new JArray { "preview", "failed", "error", "failedAtIndex", "opsApplied", "results" },
            ["additionalProperties"] = false,
        };
    }
}
