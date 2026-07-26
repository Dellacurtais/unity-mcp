using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect
{
    /// Reflection-based static method invocation. Resolves typeName +
    /// methodName, coerces JSON args to the parameter types, calls Invoke,
    /// serialises the return value. Powerful — can call any public/non-public
    /// static method in any loaded assembly. Gated by Settings.AllowWrites.
    public sealed class InvokeStaticMethodTool : IMcpTool
    {
        public string Name => "invoke_static_method";

        public string Description =>
            "Invokes a public STATIC method by name inside the reflection sandbox. Use describe_static_method " +
            "first to learn parameter types. typeName accepts FullName, simple Name, or " +
            "assembly-qualified; pass assemblyName when ambiguous. Pass argTypes (parameter " +
            "FullNames or Names) to disambiguate overloads with the same arity. UnityEngine.Object " +
            "params accept {instanceId:int} or a bare integer. Vector2/3/4, Quaternion, Color " +
            "accept arrays or {x,y,z,w}/{r,g,b,a} objects. Enums accept name strings or ints. " +
            "Returns the method's return value serialised to JSON (UnityEngine.Object → " +
            "{instanceId,name,type,assetPath?}). Errors propagate as INVOKE_THREW with stack trace. " +
            "Generic, ref/out, params, accessors, delegates, enumerators and async methods are UNSUPPORTED. " +
            "public methods only are eligible; use dedicated MCP tools for project/editor writes. Requires AllowWrites=true.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["typeName"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
                ["methodName"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 },
                ["assemblyName"] = new JObject { ["type"] = "string", ["maxLength"] = 512 },
                ["argTypes"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["description"] = "Parameter type FullNames or simple Names; one entry per parameter.",
                },
                ["args"] = new JObject
                {
                    ["type"] = "array",
                    // anyOf with explicit primitive/object branches — empty-schema
                    // items ({}) gets stripped by some provider validators (OpenAI
                    // strict-mode, Gemini), so enumerate valid JSON value kinds.
                    // Vector3/Quaternion/Color args should use object form
                    // ({x,y,z}, {r,g,b,a}); JsonHelpers.Parse* already accepts those.
                    ["items"] = new JObject
                    {
                        ["anyOf"] = new JArray
                        {
                            new JObject { ["type"] = "string" },
                            new JObject { ["type"] = "number" },
                            new JObject { ["type"] = "integer" },
                            new JObject { ["type"] = "boolean" },
                            new JObject { ["type"] = "object" },
                            new JObject { ["type"] = "array" },
                            new JObject { ["type"] = "null" },
                        },
                    },
                    ["description"] = "JSON values, one per parameter. UnityEngine.Object params accept {instanceId:int}; Vector3/Quaternion/Color use object form like {x,y,z} or {r,g,b,a}.",
                },
            },
            ["required"] = new JArray { "typeName", "methodName" },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["typeFullName"] = new JObject { ["type"] = "string" },
                ["method"] = new JObject { ["type"] = "string" },
                ["returnTypeFullName"] = new JObject { ["type"] = "string" },
                ["returnValue"] = new JObject
                {
                    ["anyOf"] = new JArray
                    {
                        new JObject { ["type"] = "string" }, new JObject { ["type"] = "number" },
                        new JObject { ["type"] = "integer" }, new JObject { ["type"] = "boolean" },
                        new JObject { ["type"] = "object" }, new JObject { ["type"] = "array" },
                        new JObject { ["type"] = "null" },
                    },
                },
            },
            ["required"] = new JArray { "typeFullName", "method", "returnTypeFullName", "returnValue" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            args = args ?? new JObject();
            var typeName = ((string)args["typeName"] ?? string.Empty).Trim();
            var methodName = ((string)args["methodName"] ?? string.Empty).Trim();
            var asmHint = ((string)args["assemblyName"] ?? string.Empty).Trim();
            if (args["argTypes"] != null && !(args["argTypes"] is JArray))
                return ToolResult.Error("BAD_ARGS", "argTypes must be an array of strings");
            if (args["args"] != null && !(args["args"] is JArray))
                return ToolResult.Error("BAD_ARGS", "args must be an array");
            var argTypes = args["argTypes"] as JArray;
            var jsonArgs = args["args"] as JArray ?? new JArray();

            if (typeName.Length == 0 || methodName.Length == 0
                || typeName.Length > 1024 || methodName.Length > 512 || asmHint.Length > 512)
                return ToolResult.Error("BAD_ARGS", "typeName and methodName are required and names must fit supported limits");
            if (argTypes != null && argTypes.Any(token => token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token)))
                return ToolResult.Error("BAD_ARGS", "argTypes must contain only non-empty strings");

            var typeRes = ReflectionResolver.ResolveType(typeName, asmHint);
            if (typeRes.Ambiguous)
            {
                return ToolResult.Error("AMBIGUOUS_TYPE",
                    $"multiple types named '{typeName}' loaded — pass assemblyName",
                    new JObject { ["candidates"] = new JArray(typeRes.Candidates) });
            }
            if (typeRes.Type == null)
                return ToolResult.Error("TYPE_NOT_FOUND", $"type '{typeName}' not found");
            var t = typeRes.Type;

            var methodRes = ReflectionResolver.ResolveStaticMethod(t, methodName, argTypes, jsonArgs);
            if (methodRes.Method == null)
            {
                return ToolResult.Error("METHOD_NOT_FOUND",
                    methodRes.Error ?? "method not found",
                    new JObject
                    {
                        ["typeFullName"] = t.FullName,
                        ["signatures"] = new JArray(methodRes.Signatures ?? new System.Collections.Generic.List<string>()),
                    });
            }
            var method = methodRes.Method;

            if (method.ContainsGenericParameters)
                return ToolResult.Error("UNSUPPORTED",
                    "generic methods are not supported (open generic parameters)");
            if (method.GetParameters().Any(p => p.ParameterType.IsByRef))
                return ToolResult.Error("UNSUPPORTED",
                    "ref/out parameters are not supported");
            if (!ReflectionPolicy.ValidateMethod(method, jsonArgs, out var policyError))
                return ToolResult.Error("REFLECTION_BLOCKED", policyError,
                    new JObject
                    {
                        ["typeFullName"] = t.FullName,
                        ["method"] = method.Name,
                    });

            object[] coerced;
            try { coerced = ReflectionResolver.CoerceArgs(method, jsonArgs); }
            catch (Exception e)
            {
                return ToolResult.Error("ARG_COERCION_FAILED", e.Message);
            }

            Bootstrap.RecordEntry(
                $"[mcp] invoke_static_method {t.FullName}.{method.Name}({jsonArgs.Count} args)");

            object ret;
            try
            {
                ret = method.Invoke(null, coerced);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return ToolResult.Error("INVOKE_THREW",
                    $"{inner.GetType().Name}: {inner.Message}",
                    new JObject
                    {
                        ["exceptionType"] = inner.GetType().FullName,
                        ["stackTrace"] = inner.StackTrace ?? "",
                    });
            }
            catch (Exception e)
            {
                return ToolResult.Error("INVOKE_THREW",
                    $"{e.GetType().Name}: {e.Message}",
                    new JObject
                    {
                        ["exceptionType"] = e.GetType().FullName,
                        ["stackTrace"] = e.StackTrace ?? "",
                    });
            }

            var payload = new JObject
            {
                ["typeFullName"] = t.FullName,
                ["method"] = method.Name,
                ["returnTypeFullName"] = method.ReturnType.FullName ?? method.ReturnType.Name,
                ["returnValue"] = ReflectionResolver.SerializeReturn(ret, method.ReturnType),
            };
            return ToolResult.Ok(
                $"invoked {t.Name}.{method.Name}",
                payload);
        }
    }
}
