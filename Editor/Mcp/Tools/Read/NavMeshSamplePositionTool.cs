using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class NavMeshSamplePositionTool : IMcpTool
    {
        public string Name => "navmesh_sample_position";
        public string Description => "Projects a world point onto the nearest matching NavMesh. areaMask accepts a raw bitmask, one defined area name, or an array of defined names/indices; agentTypeID selects a specific agent build. Keep maxDistance small because wide searches are more expensive.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["position"] = SchemaHelpers.StrictVec3Schema,
                ["maxDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 1.0 },
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
                ["maxDistance"] = new JObject { ["type"] = "number" },
                ["areaMask"] = new JObject { ["type"] = "integer" },
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
                ["hit"] = new JObject { ["type"] = "object" },
            },
            ["required"] = new JArray { "found", "requestedPosition", "maxDistance", "areaMask" },
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.SamplePosition(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
