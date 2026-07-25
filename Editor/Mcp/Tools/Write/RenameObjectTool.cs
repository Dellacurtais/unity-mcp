using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class RenameObjectTool : IMcpTool
    {
        public string Name => "rename_object";
        public string Description =>
            "Renames a GameObject (gameObject.name). Does NOT rename the underlying prefab asset.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "instanceId", "name" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => GameObjectAuthoringSchemas.TargetResult;

        public ToolResult Execute(JObject args)
        {
            var r = GameObjectEditor.Rename(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, SceneAuthoringSupport.TargetResult(r.InstanceID, r.Data));
        }
    }
}
