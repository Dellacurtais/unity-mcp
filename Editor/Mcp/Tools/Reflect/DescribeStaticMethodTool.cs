using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect
{
    /// Read-only introspection of static method overloads. The agent should
    /// call this before invoke_static_method to learn the right argTypes for
    /// disambiguation and the parameter types so it can build args correctly.
    public sealed class DescribeStaticMethodTool : IMcpTool
    {
        public string Name => "describe_static_method";

        public string Description =>
            "Returns parameter and return-type info for every static overload of a method. " +
            "Pass typeName as FullName ('UnityEditor.EditorApplication'), simple Name " +
            "('EditorApplication'), or assembly-qualified ('UnityEditor.EditorApplication, " +
            "UnityEditor'); pass assemblyName when multiple assemblies define the same simple " +
            "name. Marks generic and ref/out methods as unsupported (invoke_static_method " +
            "rejects those).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["typeName"] = new JObject { ["type"] = "string" },
                ["methodName"] = new JObject { ["type"] = "string" },
                ["assemblyName"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "typeName", "methodName" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var typeName = (string)args["typeName"];
            var methodName = (string)args["methodName"];
            var asmHint = (string)args["assemblyName"];

            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                return ToolResult.Error("BAD_ARGS", "typeName and methodName are required");

            var resolution = ReflectionResolver.ResolveType(typeName, asmHint);
            if (resolution.Ambiguous)
            {
                return ToolResult.Error("AMBIGUOUS_TYPE",
                    $"multiple types named '{typeName}' loaded — pass assemblyName",
                    new JObject { ["candidates"] = new JArray(resolution.Candidates) });
            }
            if (resolution.Type == null)
                return ToolResult.Error("TYPE_NOT_FOUND", $"type '{typeName}' not found");

            var t = resolution.Type;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public
                                        | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var overloads = t.GetMethods(flags).Where(m => m.Name == methodName).ToArray();

            if (overloads.Length == 0)
            {
                return ToolResult.Error("METHOD_NOT_FOUND",
                    $"no static method named '{methodName}' on '{t.FullName}'",
                    new JObject { ["typeFullName"] = t.FullName });
            }

            var overloadsArr = new JArray();
            foreach (var m in overloads)
            {
                var paramsArr = new JArray();
                foreach (var p in m.GetParameters())
                {
                    var po = new JObject
                    {
                        ["name"] = p.Name,
                        ["typeFullName"] = p.ParameterType.FullName ?? p.ParameterType.Name,
                        ["isOptional"] = p.IsOptional,
                        ["isParams"] = p.IsDefined(typeof(System.ParamArrayAttribute), false),
                        ["isRefOrOut"] = p.ParameterType.IsByRef,
                    };
                    if (p.IsOptional && p.HasDefaultValue && p.DefaultValue != null)
                        po["defaultValue"] = JToken.FromObject(p.DefaultValue);
                    paramsArr.Add(po);
                }

                bool unsupported = m.ContainsGenericParameters
                                   || m.GetParameters().Any(p => p.ParameterType.IsByRef);
                string unsupportedReason = null;
                if (m.ContainsGenericParameters) unsupportedReason = "generic";
                else if (m.GetParameters().Any(p => p.ParameterType.IsByRef)) unsupportedReason = "ref/out";

                var o = new JObject
                {
                    ["signature"] = ReflectionResolver.SignatureOf(m),
                    ["returnTypeFullName"] = m.ReturnType.FullName ?? m.ReturnType.Name,
                    ["isPublic"] = m.IsPublic,
                    ["parameters"] = paramsArr,
                    ["unsupported"] = unsupported,
                };
                if (unsupportedReason != null) o["unsupportedReason"] = unsupportedReason;
                overloadsArr.Add(o);
            }

            var payload = new JObject
            {
                ["typeFullName"] = t.FullName,
                ["assembly"] = t.Assembly.GetName().Name,
                ["methodName"] = methodName,
                ["overloads"] = overloadsArr,
            };
            return ToolResult.Ok(
                $"{t.Name}.{methodName}: {overloads.Length} overload(s)",
                payload);
        }
    }
}
