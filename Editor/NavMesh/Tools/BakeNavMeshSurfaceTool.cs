using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class BakeNavMeshSurfaceTool : IMcpTool
    {
        public string Name => "bake_navmesh_surface";
        public string Description => "Synchronously rebuilds one NavMeshSurface, or every loaded surface when no ID is supplied, and persists each resulting NavMeshData as an asset like the AI Navigation Inspector workflow. Existing bake assets can be replaced.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["surfaceInstanceId"] = new JObject { ["type"] = "integer" } },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["bakedCount"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                ["surfaces"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" }, ["minItems"] = 1 },
            },
            ["required"] = new JArray { "bakedCount", "surfaces" },
        };
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "navmesh",
            Capabilities = McpCapability.SceneWrite | McpCapability.AssetWrite | McpCapability.Destructive,
            Risk = McpRiskLevel.High,
            ConfirmationPolicy = McpConfirmationPolicy.Required,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = false,
            OutputSchema = OutputSchema,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshSurfaceAuthor.BakeSurface(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
