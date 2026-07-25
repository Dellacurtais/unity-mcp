using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ListNavMeshAgentTypesTool : IMcpTool
    {
        public string Name => "list_navmesh_agent_types";
        public string Description => "Lists every registered NavMesh build setting with agentTypeID, name, radius, height, slope, and climb. Resolve agentTypeID here instead of assuming zero in custom projects.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["agentTypes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "count", "agentTypes" },
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.ListAgentTypes(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
