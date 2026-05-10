using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class QueryCollisionLayersTool : IMcpTool
    {
        public string Name => "query_collision_layers";
        public string Description =>
            "Reads Unity's per-layer collision matrix (Project Settings → Physics → Layer Collision Matrix). Without args returns the full matrix. With `layer` (int or name) returns only the layers that DO collide with that one.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["layer"] = new JObject(),
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.QueryCollisionLayers(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
