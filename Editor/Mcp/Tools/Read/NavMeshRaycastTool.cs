using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class NavMeshRaycastTool : IMcpTool
    {
        public string Name => "navmesh_raycast";
        public string Description => "Tests a line across the NavMesh. Unity's NavMesh.Raycast convention is inverted from Physics.Raycast: blocked=true means the line hit a NavMesh boundary. The start is sampled within sampleDistance first, so an off-mesh start returns found=false instead of being misreported as clear.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["start"] = SchemaHelpers.StrictVec3Schema,
                ["end"] = SchemaHelpers.StrictVec3Schema,
                ["areaMask"] = NavMeshSchemas.AreaMask,
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
                ["sampleDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 0.1 },
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
                ["blocked"] = new JObject { ["type"] = "boolean" },
                ["clear"] = new JObject { ["type"] = "boolean" },
                ["start"] = NavMeshSchemas.Vec3Output,
                ["end"] = NavMeshSchemas.Vec3Output,
                ["areaMask"] = new JObject { ["type"] = "integer" },
                ["sampleDistance"] = new JObject { ["type"] = "number" },
                ["agentTypeID"] = new JObject { ["type"] = "integer" },
                ["mappedStart"] = NavMeshSchemas.Vec3Output,
                ["hit"] = new JObject { ["type"] = "object" },
            },
            ["required"] = new JArray { "found", "blocked", "clear", "start", "end", "areaMask", "sampleDistance" },
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.Raycast(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
