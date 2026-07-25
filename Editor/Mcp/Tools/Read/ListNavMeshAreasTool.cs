using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ListNavMeshAreasTool : IMcpTool
    {
        public string Name => "list_navmesh_areas";
        public string Description => "Lists every defined NavMesh area with its true numeric index and traversal cost. Use these names or indices with NavMesh query and authoring tools.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["areas"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "count", "areas" },
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.ListAreas(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
