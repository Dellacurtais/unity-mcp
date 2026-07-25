using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class AddNavMeshModifierTool : IMcpTool
    {
        public string Name => "add_navmesh_modifier";
        public string Description => "Adds or patches NavMeshModifier, or NavMeshModifierVolume when volume=true. Supports area/ignore hierarchy rules, generated-link overrides, volume bounds, apply-to-children, and AI Navigation 2.0 affected-agent filtering.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["volume"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["area"] = NavMeshSchemas.AreaIndex,
                ["overrideArea"] = new JObject { ["type"] = "boolean" },
                ["ignoreFromBuild"] = new JObject { ["type"] = "boolean" },
                ["applyToChildren"] = new JObject { ["type"] = "boolean" },
                ["overrideGenerateLinks"] = new JObject { ["type"] = "boolean" },
                ["generateLinks"] = new JObject { ["type"] = "boolean" },
                ["affectedAgentTypeIDs"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer", ["minimum"] = -1 }, ["minItems"] = 1 },
                ["center"] = SchemaHelpers.StrictVec3Schema,
                ["size"] = SchemaHelpers.StrictVec3Schema,
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string" },
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["hostInstanceId"] = new JObject { ["type"] = "integer" },
                ["area"] = new JObject { ["type"] = "integer" },
                ["areaName"] = new JObject { ["type"] = "string" },
                ["affectedAgentTypeIDs"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } },
            },
            ["required"] = new JArray { "kind", "instanceId", "hostInstanceId", "area", "areaName", "affectedAgentTypeIDs" },
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshSurfaceAuthor.AddModifier(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
