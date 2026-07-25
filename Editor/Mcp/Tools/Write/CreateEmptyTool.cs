using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateEmptyTool : IMcpTool
    {
        public string Name => "create_empty";
        public string Description =>
            "Creates an empty GameObject (no components beyond Transform). Useful for grouping or for adding components afterwards via add_component.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "GameObject" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["rotation"] = SchemaHelpers.RotSchema,
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => SceneAuthoringSupport.TargetResultSchema;

        public ToolResult Execute(JObject args)
        {
            var result = GameObjectFactory.CreateEmpty(args);
            if (!result.Ok) return ToolResult.Error("OP_FAILED", result.Error);
            return ToolResult.Ok(result.Summary, SceneAuthoringSupport.TargetResult(result.InstanceID, result.Data));
        }
    }
}
