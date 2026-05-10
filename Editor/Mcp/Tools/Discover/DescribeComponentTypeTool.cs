using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class DescribeComponentTypeTool : IMcpTool
    {
        public string Name => "describe_component_type";
        public string Description =>
            "Returns the SCHEMA of a Component type without needing an instance: serialized fields (name, type, isArray, attributes like Range/Tooltip/Header), required-by-RequireComponent list, namespace, assembly. Use this after list_component_types to learn what set_property paths are valid on that type. Pass simple name (e.g. 'Rigidbody') or fully-qualified name.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["componentType"] = new JObject { ["type"] = "string" },
                ["includeMethods"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "componentType" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var typeName = (string)args["componentType"];
            var withMethods = (bool?)args["includeMethods"] ?? false;
            var d = ComponentTypeInspector.Describe(typeName, withMethods);
            if (d == null) return ToolResult.Error("NOT_FOUND", $"component type '{typeName}' not found");

            var fields = new JArray();
            if (d.Fields != null)
            {
                foreach (var f in d.Fields)
                {
                    var fo = new JObject
                    {
                        ["name"] = f.Name,
                        ["type"] = f.Type,
                        ["isArray"] = f.IsArray,
                        ["isRequired"] = f.IsRequired,
                    };
                    if (!string.IsNullOrEmpty(f.ArrayElementType)) fo["elementType"] = f.ArrayElementType;
                    if (f.Attributes != null)
                    {
                        var attrs = new JObject();
                        foreach (var kv in f.Attributes) attrs[kv.Key] = JToken.FromObject(kv.Value);
                        fo["attributes"] = attrs;
                    }
                    fields.Add(fo);
                }
            }

            var requires = new JArray();
            if (d.RequiredComponents != null)
                foreach (var r in d.RequiredComponents) requires.Add(r);

            var methods = new JArray();
            if (d.Methods != null)
                foreach (var m in d.Methods) methods.Add(m);

            var payload = new JObject
            {
                ["name"] = d.Name,
                ["fullName"] = d.FullName,
                ["namespace"] = d.Namespace,
                ["assembly"] = d.Assembly,
                ["isMonoBehaviour"] = d.IsMonoBehaviour,
                ["isBuiltin"] = d.IsBuiltin,
                ["requires"] = requires,
                ["fields"] = fields,
                ["methods"] = methods,
            };
            return ToolResult.Ok($"{d.Name}: {fields.Count} serialized field(s)", payload);
        }
    }
}
