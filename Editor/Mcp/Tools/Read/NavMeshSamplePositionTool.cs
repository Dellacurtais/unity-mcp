using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class NavMeshSamplePositionTool : IMcpTool
    {
        public string Name => "navmesh_sample_position";
        public string Description =>
            "`NavMesh.SamplePosition` — projects a world point onto the nearest navmesh point within `maxDistance`. " +
            "Answers \"is this point reachable / on the walkable area?\" and returns the projected position, distance, area, normal. " +
            "areaMask: int (raw bitmask), single area name string ('Walkable'/'Jump'/...), or array of area names. Default: all areas.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["position"] = new JObject(),
                ["maxDistance"] = new JObject { ["type"] = "number", ["default"] = 1.0 },
                ["areaMask"] = new JObject(),
            },
            ["required"] = new JArray { "position" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.SamplePosition(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
