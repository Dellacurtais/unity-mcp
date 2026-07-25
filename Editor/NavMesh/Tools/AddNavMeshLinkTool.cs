using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class AddNavMeshLinkTool : IMcpTool
    {
        public string Name => "add_navmesh_link";
        public string Description => "Adds or patches AI Navigation's NavMeshLink. Endpoints can be local startPoint/endPoint values or tracked startInstanceId/endInstanceId transforms. Supports width, agent type, area, direction, activation, automatic endpoint tracking, and traversal cost.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["startPoint"] = SchemaHelpers.StrictVec3Schema,
                ["endPoint"] = SchemaHelpers.StrictVec3Schema,
                ["startInstanceId"] = new JObject { ["type"] = "integer" },
                ["endInstanceId"] = new JObject { ["type"] = "integer" },
                ["width"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["agentTypeID"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["area"] = NavMeshSchemas.AreaIndex,
                ["bidirectional"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["activated"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["autoUpdate"] = new JObject { ["type"] = "boolean" },
                ["costModifier"] = new JObject { ["type"] = "number", ["default"] = -1 },
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
                ["hostInstanceId"] = new JObject { ["type"] = "integer" },
                ["startPoint"] = NavMeshSchemas.Vec3Output,
                ["endPoint"] = NavMeshSchemas.Vec3Output,
                ["width"] = new JObject { ["type"] = "number" },
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
                ["area"] = new JObject { ["type"] = "integer" },
                ["areaName"] = new JObject { ["type"] = "string" },
                ["bidirectional"] = new JObject { ["type"] = "boolean" },
                ["activated"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "instanceId", "hostInstanceId", "startPoint", "endPoint", "width", "agentTypeID", "area", "areaName", "bidirectional", "activated" },
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshSurfaceAuthor.AddLink(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
