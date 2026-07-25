using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class AddNavMeshSurfaceTool : IMcpTool
    {
        public string Name => "add_navmesh_surface";
        public string Description => "Adds or patches an AI Navigation NavMeshSurface. Supports every 2.0 collection mode, volume bounds, default area, link generation, geometry mode, layer filtering, voxel/tile overrides, minimum region area, and height mesh. Use bake_navmesh_surface separately for the destructive asset-backed bake.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["agentTypeID"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["collectObjects"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "All", "Volume", "Children", "MarkedWithModifier" }, ["default"] = "All" },
                ["useGeometry"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "RenderMeshes", "PhysicsColliders" }, ["default"] = "RenderMeshes" },
                ["layerMask"] = SchemaHelpers.LayerMaskSchema,
                ["defaultArea"] = NavMeshSchemas.AreaIndex,
                ["size"] = SchemaHelpers.StrictVec3Schema,
                ["center"] = SchemaHelpers.StrictVec3Schema,
                ["generateLinks"] = new JObject { ["type"] = "boolean" },
                ["overrideVoxelSize"] = new JObject { ["type"] = "boolean" },
                ["voxelSize"] = new JObject { ["type"] = "number", ["minimum"] = 0.000001 },
                ["overrideTileSize"] = new JObject { ["type"] = "boolean" },
                ["tileSize"] = new JObject { ["type"] = "integer", ["minimum"] = 16, ["maximum"] = 1024 },
                ["minRegionArea"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["buildHeightMesh"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["surfaceInstanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
                ["collectObjects"] = new JObject { ["type"] = "string" },
                ["useGeometry"] = new JObject { ["type"] = "string" },
                ["layerMask"] = new JObject { ["type"] = "integer" },
                ["defaultArea"] = new JObject { ["type"] = "integer" },
                ["baked"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "instanceId", "surfaceInstanceId", "name", "agentTypeID", "collectObjects", "useGeometry", "layerMask", "defaultArea", "baked" },
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshSurfaceAuthor.AddSurface(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
