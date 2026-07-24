using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetPhysicsMaterialTool : IMcpTool
    {
        public string Name => "set_physics_material";
        public string Description =>
            "Assigns a PhysicsMaterial asset by GUID or Assets/Packages path to a Collider's sharedMaterial. " +
            "When the GameObject has multiple Colliders, colliderInstanceId is required to select one explicitly. " +
            "The edit is Undo-safe and records Prefab instance overrides.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["colliderInstanceId"] = new JObject { ["type"] = "integer" },
                ["materialGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            },
            ["required"] = new JArray { "instanceId", "materialGuidOrPath" },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["collider"] = SchemaHelpers.TargetRefSchema,
                ["material"] = SchemaHelpers.TargetRefSchema,
                ["changed"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "target", "collider", "material", "changed" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "physics",
            Capabilities = McpCapability.SceneWrite,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = true,
            OutputSchema = OutputSchema,
        };

        public ToolResult Execute(JObject args)
        {
            var result = PhysicsAuthor.SetPhysicsMaterial(args);
            if (!result.Ok) return ToolResult.Error("OP_FAILED", result.Error);
            return ToolResult.Ok(result.Summary, result.Data);
        }
    }
}
