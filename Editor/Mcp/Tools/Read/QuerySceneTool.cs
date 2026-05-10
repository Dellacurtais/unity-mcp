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
                ["tag"] = new JObject { ["type"] = "string" },
                ["layer"] = new JObject(),
                ["componentTypes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                ["nameContains"] = new JObject { ["type"] = "string" },
                ["pathContains"] = new JObject { ["type"] = "string" },
                ["withinRadius"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["center"] = new JObject(),
                        ["radius"] = new JObject { ["type"] = "number" },
                    },
                },
                ["sortBy"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "name", "distance", "depth" } },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 100 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.QueryScene(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
