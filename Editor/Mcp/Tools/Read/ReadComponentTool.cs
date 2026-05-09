using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadComponentTool : IMcpTool
    {
        public string Name => "read_component";
        public string Description =>
            "Returns the serialized properties of one Component (or all components when componentType is omitted). Includes scalar values, color, vectors, enums, object references and bounded array elements. For deeply-nested struct fields, surface comes back tagged '[struct: ...]' — call set_property with a propertyPath to write into them.";

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

        public ToolResult Execute(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            var max = (int?)args["maxArrayItems"] ?? ReadHelpers.DefaultMaxArrayItems;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return ToolResult.Error("NOT_FOUND", $"GameObject not found: instanceId={id}");

            var arr = new JArray();
            int matched = 0;
            foreach (var c in go.GetComponents<Component>())
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
                ["gameObjectName"] = go.name,
                ["components"] = arr,
            };
            return ToolResult.Ok($"{matched} component(s) on '{go.name}'", payload);
        }
    }
}
