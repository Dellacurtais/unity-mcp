using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class ListNavMeshSurfacesTool : IMcpTool
    {
        public string Name => "list_navmesh_surfaces";
        public string Description => "Lists every NavMeshSurface on loaded scene objects, including inactive objects, with collection/build settings and persisted NavMeshData asset state.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["surfaces"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "count", "surfaces" },
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshSurfaceAuthor.ListSurfaces(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
