using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class BakeNavMeshTool : IMcpTool
    {
        public string Name => "bake_navmesh";
        public string Description => "Synchronously rebuilds the legacy global NavMesh with UnityEditor.AI.NavMeshBuilder. This replaces existing global bake data; prefer bake_navmesh_surface when AI Navigation is installed.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["completed"] = new JObject { ["type"] = "boolean" },
                ["mode"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "completed", "mode" },
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
            var result = NavMeshAuthor.BakeLegacy(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
