using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AutoFitColliderTool : IMcpTool
    {
        public string Name => "auto_fit_collider";
        public string Description =>
            "Adds one Box, Sphere, Capsule, or MeshCollider to a GameObject using renderer bounds expressed in the collider's local space. " +
            "Primitive sphere/capsule fits approximate the local AABB; capsules follow its longest axis. " +
            "useChildren=true by default for primitives. Mesh kind requires useChildren=false and a MeshFilter.sharedMesh on the target. " +
            "replaceExisting=true removes existing Colliders except CharacterController. Mesh triggers and non-kinematic Rigidbodies require convex=true.";

        private static JObject SharedProperties => new JObject
        {
            ["instanceId"] = new JObject { ["type"] = "integer" },
            ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule", "mesh" }, ["default"] = "box" },
            ["useChildren"] = new JObject { ["type"] = "boolean" },
            ["replaceExisting"] = new JObject { ["type"] = "boolean", ["default"] = true },
            ["isTrigger"] = new JObject { ["type"] = "boolean", ["default"] = false },
            ["convex"] = new JObject { ["type"] = "boolean", ["default"] = false },
        };

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = SharedProperties,
            ["required"] = new JArray { "instanceId" },
            ["oneOf"] = new JArray
            {
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["instanceId"] = new JObject { ["type"] = "integer" },
                        ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule" }, ["default"] = "box" },
                        ["useChildren"] = new JObject { ["type"] = "boolean", ["default"] = true },
                        ["replaceExisting"] = new JObject { ["type"] = "boolean", ["default"] = true },
                        ["isTrigger"] = new JObject { ["type"] = "boolean", ["default"] = false },
                    },
                    ["required"] = new JArray { "instanceId" },
                    ["additionalProperties"] = false,
                },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["instanceId"] = new JObject { ["type"] = "integer" },
                        ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "mesh" } },
                        ["useChildren"] = new JObject { ["type"] = "boolean", ["enum"] = new JArray { false }, ["default"] = false },
                        ["replaceExisting"] = new JObject { ["type"] = "boolean", ["default"] = true },
                        ["isTrigger"] = new JObject { ["type"] = "boolean", ["default"] = false },
                        ["convex"] = new JObject { ["type"] = "boolean", ["default"] = false },
                    },
                    ["required"] = new JArray { "instanceId", "kind" },
                    ["additionalProperties"] = false,
                },
            },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["collider"] = SchemaHelpers.TargetRefSchema,
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule", "mesh" } },
                ["useChildren"] = new JObject { ["type"] = "boolean" },
                ["replaceExisting"] = new JObject { ["type"] = "boolean" },
                ["replacedCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["isTrigger"] = new JObject { ["type"] = "boolean" },
                ["convex"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "target", "collider", "kind", "useChildren", "replaceExisting", "replacedCount", "isTrigger", "convex" },
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
            var result = PhysicsAuthor.AutoFitCollider(args);
            if (!result.Ok) return ToolResult.Error("OP_FAILED", result.Error);
            return ToolResult.Ok(result.Summary, result.Data);
        }
    }
}
