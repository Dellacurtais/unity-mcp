using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetTagTool : IMcpTool
    {
        public string Name => "set_tag";
        public string Description =>
            "Sets the GameObject's tag (must already be registered in Project Settings → Tags & Layers). Returns an error if the tag isn't defined.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["tag"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "instanceId", "tag" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = GameObjectEditor.SetTag(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary);
        }
    }
}
