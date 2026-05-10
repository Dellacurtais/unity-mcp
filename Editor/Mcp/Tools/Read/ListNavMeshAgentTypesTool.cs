using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ListNavMeshAgentTypesTool : IMcpTool
    {
        public string Name => "list_navmesh_agent_types";
        public string Description =>
            "Lists every NavMesh agent type registered in the project (Project Settings > Navigation > Agents) with its agentTypeID, name, radius, height, slope, and climb. agentTypeID=0 is conventionally Humanoid but custom projects may register more. Run this BEFORE add_navmesh_surface or add_navmesh_link so you can pick the correct agentTypeID instead of guessing.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.ListAgentTypes(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
