using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetPropertyTool : IMcpTool
    {
        public string Name => "set_property";
        public string Description =>
            "Sets a single SerializedProperty on a Component instance. propertyPath uses Unity's serialized form (often the field name; nested with dots, e.g. 'm_LocalPosition.x'). Value type must match the property — see read_component to inspect names/types first.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["componentType"] = new JObject { ["type"] = "string" },
                ["propertyPath"] = new JObject { ["type"] = "string" },
                ["value"] = new JObject(),
            },
            ["required"] = new JArray { "instanceId", "componentType", "propertyPath", "value" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => GameObjectAuthoringSchemas.TargetResult;

        public ToolResult Execute(JObject args)
        {
            var r = ComponentEditor.SetProperty(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, SceneAuthoringSupport.TargetResult(r.InstanceID, r.Data));
        }
    }
}
