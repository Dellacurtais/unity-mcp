using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class NavMeshRaycastTool : IMcpTool
    {
        public string Name => "navmesh_raycast";
        public string Description =>
            "`NavMesh.Raycast` — line-of-sight on the navmesh between two points. Note the inverted convention vs Physics.Raycast: returns BLOCKED when the line crosses a non-walkable area (with the block point), CLEAR when the entire line stays on walkable navmesh.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["start"] = new JObject(),
                ["end"] = new JObject(),
                ["areaMask"] = new JObject(),
            },
            ["required"] = new JArray { "start", "end" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.Raycast(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
