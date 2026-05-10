using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class AddNavMeshSurfaceTool : IMcpTool
    {
        public string Name => "add_navmesh_surface";
        public string Description =>
            "Adds a NavMeshSurface to a GameObject and configures it. agentTypeID=0 is Humanoid (default). collectObjects: All / Volume / Children. useGeometry: RenderMeshes (default) / PhysicsColliders. layerMask: int OR layer name OR array of names. defaultArea is the area index assigned to walkables. overrideVoxelSize/voxelSize tune voxel resolution; overrideTileSize/tileSize tune tile size. Pass `bake: true` to bake immediately after configuring.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["agentTypeID"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["collectObjects"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "All", "Volume", "Children" }, ["default"] = "Children" },
                ["useGeometry"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "RenderMeshes", "PhysicsColliders" }, ["default"] = "RenderMeshes" },
                ["layerMask"] = new JObject(),
                ["defaultArea"] = new JObject { ["type"] = "integer" },
                ["overrideVoxelSize"] = new JObject { ["type"] = "boolean" },
                ["voxelSize"] = new JObject { ["type"] = "number" },
                ["overrideTileSize"] = new JObject { ["type"] = "boolean" },
                ["tileSize"] = new JObject { ["type"] = "integer" },
                ["bake"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshSurfaceAuthor.AddSurface(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID })
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
