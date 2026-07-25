using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class NavMeshCalculatePathTool : IMcpTool
    {
        public string Name => "navmesh_calculate_path";
        public string Description => "Calculates a NavMesh path between two world points and returns whether Unity found one, its status, ordered corners, and total corner-to-corner distance. agentTypeID applies the corresponding NavMeshQueryFilter.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["start"] = SchemaHelpers.StrictVec3Schema,
                ["end"] = SchemaHelpers.StrictVec3Schema,
                ["areaMask"] = NavMeshSchemas.AreaMask,
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "start", "end" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["found"] = new JObject { ["type"] = "boolean" },
                ["status"] = new JObject { ["type"] = "string" },
                ["corners"] = new JObject { ["type"] = "array", ["items"] = NavMeshSchemas.Vec3Output },
                ["totalDistance"] = new JObject { ["type"] = "number" },
                ["areaMask"] = new JObject { ["type"] = "integer" },
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "found", "status", "corners", "totalDistance", "areaMask" },
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.CalculatePath(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
