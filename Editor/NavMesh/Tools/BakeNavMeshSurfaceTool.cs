using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class BakeNavMeshSurfaceTool : IMcpTool
    {
        public string Name => "bake_navmesh_surface";
        public string Description =>
            "Bakes a specific NavMeshSurface (modern per-surface workflow). Pass `surfaceInstanceId` to bake one — omit to bake EVERY NavMeshSurface in the active scene. Sync; saves the navMeshData on the surface.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["surfaceInstanceId"] = new JObject { ["type"] = "integer" },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshSurfaceAuthor.BakeSurface(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
