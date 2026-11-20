using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadComponentTool : IMcpTool
    {
        public string Name => "read_component";
        public string Description =>
            "Returns serialized properties for a GameObject or Component instanceId. A GameObject ID returns all components unless componentType filters them; a Component ID returns that exact component. IDs emitted by read_scene are accepted directly.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["componentType"] = new JObject { ["type"] = "string", ["description"] = "Optional — omit to dump all components." },
                ["maxArrayItems"] = new JObject { ["type"] = "integer", ["default"] = 16, ["minimum"] = 1 },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;
        public JObject OutputSchema => SceneToolSchemas.ReadComponent;

        public ToolResult Execute(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            var max = (int?)args["maxArrayItems"] ?? ReadHelpers.DefaultMaxArrayItems;

            var target = UnityObjectIds.Resolve(id);
            var requestedComponent = target as Component;
            var go = target as GameObject ?? requestedComponent?.gameObject;
            if (go == null) return ToolResult.Error("NOT_FOUND", $"GameObject or Component not found: instanceId={id}");

            var arr = new JArray();
            int matched = 0;
            var components = requestedComponent != null
                ? new[] { requestedComponent }
                : go.GetComponents<Component>();
            foreach (var c in components)
            {
                if (c == null) continue;
                if (!string.IsNullOrEmpty(typeName))
                {
                    var t = c.GetType();
                    if (!string.Equals(t.FullName, typeName, System.StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(t.Name, typeName, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                arr.Add(ReadHelpers.SerializeComponent(c, max));
                matched++;
            }

            if (matched == 0 && !string.IsNullOrEmpty(typeName))
                return ToolResult.Error("NOT_FOUND", $"GameObject has no component of type '{typeName}'");

            var payload = new JObject
            {
                ["instanceId"] = id,
                ["requestedInstanceId"] = id,
                ["gameObjectInstanceId"] = go.GetMcpInstanceId(),
                ["componentInstanceId"] = requestedComponent != null ? requestedComponent.GetMcpInstanceId() : (JToken)null,
                ["gameObjectName"] = go.name,
                ["components"] = arr,
            };
            return ToolResult.Ok($"{matched} component(s) on '{go.name}'", payload);
        }
    }
}
