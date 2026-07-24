using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddCompoundCollidersTool : IMcpTool
    {
        public string Name => "add_compound_colliders";
        public string Description =>
            "Adds one collider per Renderer in the target hierarchy using each renderer's local bounds, preserving that renderer GameObject's orientation. " +
            "kind: box (default), sphere, capsule, or mesh; sphere/capsule fits approximate the local AABB and capsules follow its longest axis. " +
            "Mesh kind requires every renderer GameObject to have a MeshFilter.sharedMesh and always creates convex colliders. " +
            "replaceExisting=false by default; when true, existing Colliders are removed except CharacterController.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule", "mesh" }, ["default"] = "box" },
                ["isTrigger"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["replaceExisting"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule", "mesh" } },
                ["addedCount"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                ["replacedCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["replaceExisting"] = new JObject { ["type"] = "boolean" },
                ["isTrigger"] = new JObject { ["type"] = "boolean" },
                ["colliders"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = SchemaHelpers.TargetRefSchema,
                    ["minItems"] = 1,
                },
            },
            ["required"] = new JArray { "target", "kind", "addedCount", "replacedCount", "replaceExisting", "isTrigger", "colliders" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "physics",
            Capabilities = McpCapability.SceneWrite | McpCapability.Destructive,
            Risk = McpRiskLevel.High,
            ConfirmationPolicy = McpConfirmationPolicy.Required,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = false,
            OutputSchema = OutputSchema,
        };

        public ToolResult Execute(JObject args)
        {
            var result = PhysicsAuthor.AddCompoundColliders(args);
            if (!result.Ok) return ToolResult.Error("OP_FAILED", result.Error);
            return ToolResult.Ok(result.Summary, result.Data);
        }
    }
}
