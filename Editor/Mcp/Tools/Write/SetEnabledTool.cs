using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetEnabledTool : IMcpTool
    {
        public string Name => "set_enabled";
        public string Description =>
            "Toggles the `enabled` flag on a Behaviour / Renderer / Collider component (does NOT remove it). Useful to disable a script or a collider without deleting. Errors when the type has no enabled flag.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["componentType"] = new JObject { ["type"] = "string" },
                ["enabled"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "instanceId", "componentType", "enabled" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = ComponentEditor.SetEnabled(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary);
        }
    }
}
