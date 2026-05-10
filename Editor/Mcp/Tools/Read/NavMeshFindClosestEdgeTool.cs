using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class NavMeshFindClosestEdgeTool : IMcpTool
    {
        public string Name => "navmesh_find_closest_edge";
        public string Description =>
            "`NavMesh.FindClosestEdge` — finds the nearest walkable-area edge near a point. Returns position, distance, normal, and area mask. Useful for cover-finding or boundary AI.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["position"] = new JObject(),
                ["areaMask"] = new JObject(),
            },
            ["required"] = new JArray { "position" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.FindClosestEdge(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
