using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetNavMeshAgentTool : IMcpTool
    {
        public string Name => "set_navmesh_agent";
        public string Description => "Validates and patches NavMeshAgent settings with Undo and prefab-override recording. destination, isStopped, and resetPath are runtime commands and require an active agent already placed on a NavMesh; ordinary settings remain editable in Edit Mode.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["destination"] = SchemaHelpers.StrictVec3Schema,
                ["isStopped"] = new JObject { ["type"] = "boolean" },
                ["speed"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["acceleration"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["angularSpeed"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["stoppingDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["radius"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["height"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["baseOffset"] = new JObject { ["type"] = "number" },
                ["areaMask"] = NavMeshSchemas.AreaMask,
                ["resetPath"] = new JObject { ["type"] = "boolean" },
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
                ["agentInstanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["isOnNavMesh"] = new JObject { ["type"] = "boolean" },
                ["speed"] = new JObject { ["type"] = "number" },
                ["acceleration"] = new JObject { ["type"] = "number" },
                ["angularSpeed"] = new JObject { ["type"] = "number" },
                ["stoppingDistance"] = new JObject { ["type"] = "number" },
                ["radius"] = new JObject { ["type"] = "number" },
                ["height"] = new JObject { ["type"] = "number" },
                ["baseOffset"] = new JObject { ["type"] = "number" },
                ["areaMask"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "instanceId", "agentInstanceId", "name", "isOnNavMesh", "speed", "acceleration", "angularSpeed", "stoppingDistance", "radius", "height", "baseOffset", "areaMask" },
        };
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "navmesh",
            Capabilities = McpCapability.SceneWrite | McpCapability.RuntimeCommand,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.Any,
            IsIdempotent = true,
            OutputSchema = OutputSchema,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.SetAgent(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
