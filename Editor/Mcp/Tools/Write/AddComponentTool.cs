using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddComponentTool : IMcpTool
    {
        public string Name => "add_component";
        public string Description =>
            "Adds a component to a GameObject. componentType is the class name (e.g. 'Rigidbody', 'BoxCollider', or a fully-qualified MonoBehaviour name).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["componentType"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "instanceId", "componentType" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => GameObjectAuthoringSchemas.TargetResult;

        public ToolResult Execute(JObject args)
        {
            var r = ComponentEditor.AddComponent(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, SceneAuthoringSupport.TargetResult(r.InstanceID, r.Data));
        }
    }
}
