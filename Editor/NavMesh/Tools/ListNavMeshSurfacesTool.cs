using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class ListNavMeshSurfacesTool : IMcpTool
    {
        public string Name => "list_navmesh_surfaces";
        public string Description =>
            "Finds all NavMeshSurface components in the active scene. Returns instanceId, name, agentTypeID, collectObjects mode, layerMask, useGeometry mode, and bake status. Use to audit which surfaces exist before invoking bake_navmesh_surface.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshSurfaceAuthor.ListSurfaces(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
