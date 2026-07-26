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
                ["componentType"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
                ["assemblyName"] = new JObject { ["type"] = "string", ["maxLength"] = 512 },
                ["includeMethods"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "componentType" },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string" },
                ["fullName"] = new JObject { ["type"] = "string" },
                ["namespace"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["assembly"] = new JObject { ["type"] = "string" },
                ["isMonoBehaviour"] = new JObject { ["type"] = "boolean" },
                ["isBuiltin"] = new JObject { ["type"] = "boolean" },
                ["requires"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                ["fields"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["methods"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            },
            ["required"] = new JArray { "name", "fullName", "namespace", "assembly", "isMonoBehaviour", "isBuiltin", "requires", "fields", "methods" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            args = args ?? new JObject();
            var typeName = ((string)args["componentType"] ?? string.Empty).Trim();
            var assemblyName = ((string)args["assemblyName"] ?? string.Empty).Trim();
            var withMethods = (bool?)args["includeMethods"] ?? false;
            if (typeName.Length == 0 || typeName.Length > 1024 || assemblyName.Length > 512)
                return ToolResult.Error("BAD_ARGS", "componentType is required and type/assembly names must fit supported limits");
            var d = ComponentTypeInspector.Describe(typeName, assemblyName, withMethods, out var resolutionError);
            if (d == null)
            {
                var code = resolutionError != null && resolutionError.StartsWith("AMBIGUOUS_COMPONENT_TYPE")
                    ? "AMBIGUOUS_COMPONENT_TYPE"
                    : "NOT_FOUND";
                return ToolResult.Error(code, resolutionError ?? $"component type '{typeName}' not found");
            }

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
