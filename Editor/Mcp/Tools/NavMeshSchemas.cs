using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools
{
    public static class NavMeshSchemas
    {
        public static JObject AreaMask => new JObject
        {
            ["description"] = "NavMesh area mask as a raw Int32 bitmask, one defined area name, or a non-empty array of defined area names/indices.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue },
                new JObject { ["type"] = "string", ["minLength"] = 1 },
                new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["oneOf"] = new JArray
                        {
                            new JObject { ["type"] = "string", ["minLength"] = 1 },
                            new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 31 },
                        },
                    },
                    ["minItems"] = 1,
                },
            },
        };

        public static JObject AreaIndex => new JObject
        {
            ["description"] = "One defined NavMesh area as its name or numeric index.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 31 },
                new JObject { ["type"] = "string", ["minLength"] = 1 },
            },
        };

        public static JObject Vec3Output => new JObject
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
        };
    }
}
