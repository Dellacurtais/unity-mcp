using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class QuerySceneTool : IMcpTool
    {
        public string Name => "query_scene";
        public string Description =>
            "Combined filter over the scene's GameObjects. Apply any subset of: tag (exact), layer (int or name), componentTypes (array — must have ALL), nameContains, pathContains, withinRadius {center, radius}. sortBy: 'name' (default) / 'distance' / 'depth'. Returns matched GameObjects with id, name, tag, layer, position (and distance when filtering by radius).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["tag"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["layer"] = new JObject
                {
                    ["oneOf"] = new JArray
                    {
                        new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 31 },
                        new JObject { ["type"] = "string", ["minLength"] = 1 },
                    },
                },
                ["componentTypes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["minItems"] = 1 },
                ["nameContains"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["pathContains"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["withinRadius"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["center"] = SchemaHelpers.StrictVec3Schema,
                        ["radius"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                    },
                    ["required"] = new JArray { "center", "radius" },
                    ["additionalProperties"] = false,
                },
                ["sortBy"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "name", "distance", "depth" } },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 100 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => SceneToolSchemas.QueryScene;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.QueryScene(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
