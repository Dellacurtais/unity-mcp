using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ListNavMeshAreasTool : IMcpTool
    {
        public string Name => "list_navmesh_areas";
        public string Description =>
            "Lists every named NavMesh area (Project Settings > Navigation > Areas) with its index and traversal cost. The indices map directly to bit positions in NavMesh.areaMask, so use them with set_navmesh_agent / navmesh_calculate_path / navmesh_sample_position. Run this BEFORE passing area names to other NavMesh tools so you know what's defined in the project.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.ListAreas(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
