using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class DeleteObjectTool : IMcpTool
    {
        public string Name => "delete_object";
        public string Description =>
            "Destroys a GameObject (and its children). Refuses by default if the GameObject has more than 8 children unless force=true. Registers a single Undo step.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["force"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => SceneAuthoringSupport.DeletedResultSchema;

        public ToolResult Execute(JObject args)
        {
            var result = GameObjectEditor.Delete(args);
            if (!result.Ok) return ToolResult.Error("OP_FAILED", result.Error);
            var data = result.Data as JObject ?? new JObject();
            data["instanceId"] = result.InstanceID;
            return ToolResult.Ok(result.Summary, data);
        }
    }
}
