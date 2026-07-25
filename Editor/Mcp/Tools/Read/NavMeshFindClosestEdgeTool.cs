using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class NavMeshFindClosestEdgeTool : IMcpTool
    {
        public string Name => "navmesh_find_closest_edge";
        public string Description => "Finds the nearest edge on a matching NavMesh and returns its position, normal, distance, and area information. agentTypeID applies the corresponding NavMeshQueryFilter.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["position"] = SchemaHelpers.StrictVec3Schema,
                ["areaMask"] = NavMeshSchemas.AreaMask,
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "position" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["found"] = new JObject { ["type"] = "boolean" },
                ["requestedPosition"] = NavMeshSchemas.Vec3Output,
                ["areaMask"] = new JObject { ["type"] = "integer" },
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
                ["hit"] = new JObject { ["type"] = "object" },
            },
            ["required"] = new JArray { "found", "requestedPosition", "areaMask" },
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.FindClosestEdge(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
