using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class RemoveComponentTool : IMcpTool
    {
        public string Name => "remove_component";
        public string Description =>
            "Removes a component from a GameObject (custom MonoBehaviour, BoxCollider, Rigidbody, AudioSource, etc.). Refuses Transform. When multiple of the same type are attached, pass `nth` (0-based) to disambiguate. Registers a single Undo step.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["componentType"] = new JObject { ["type"] = "string" },
                ["nth"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Index when multiple of the same type are attached (default: 0 → first)." },
            },
            ["required"] = new JArray { "instanceId", "componentType" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = ComponentEditor.RemoveComponent(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
