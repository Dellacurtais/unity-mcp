using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class QueryCollisionLayersTool : IMcpTool
    {
        public string Name => "query_collision_layers";
        public string Description =>
            "Reads the effective runtime 3D layer-collision matrix via Physics.GetIgnoreLayerCollision. Without args returns all 32 layer indices, including unnamed layers. With `layer` (0..31 or a configured name) returns that row. Runtime overrides can differ from the saved Project Settings matrix.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["layer"] = new JObject
                {
                    ["oneOf"] = new JArray
                    {
                        new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 31 },
                        new JObject { ["type"] = "string", ["minLength"] = 1 },
                    },
                },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["matrixState"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "effectiveRuntime" } },
                ["selectedLayer"] = new JObject
                {
                    ["oneOf"] = new JArray
                    {
                        new JObject { ["type"] = "object" },
                        new JObject { ["type"] = "null" },
                    },
                },
                ["layers"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "matrixState", "selectedLayer", "layers" },
        };
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.QueryCollisionLayers(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
