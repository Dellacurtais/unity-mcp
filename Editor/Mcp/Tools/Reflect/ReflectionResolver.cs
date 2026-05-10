using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect
{
    /// Shared reflection helpers for the Reflect/* tools. Resolves type and
    /// method names against all loaded assemblies, coerces JSON arguments to
    /// .NET parameter types (with Unity-specific shortcuts: Vector3, Quaternion,
    /// Color, UnityEngine.Object via instanceId, enums by name) and serialises
    /// return values back to JSON the agent can read.
    public static class ReflectionResolver
    {
        public sealed class TypeResolution
        {
            public Type Type;
            public bool Ambiguous;
            public List<string> Candidates;
        }

        public sealed class MethodResolution
        {
            public MethodInfo Method;
            public string Error;
            public List<string> Signatures;
        }

        public static TypeResolution ResolveType(string name, string asmHint = null)
        {
            var result = new TypeResolution();
            if (string.IsNullOrEmpty(name)) return result;

            var direct = Type.GetType(name, throwOnError: false);
            if (direct != null) { result.Type = direct; return result; }

            IEnumerable<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (!string.IsNullOrEmpty(asmHint))
            {
                assemblies = assemblies.Where(a => string.Equals(
                    a.GetName().Name, asmHint, StringComparison.OrdinalIgnoreCase));
            }

            var fullNameMatches = new List<Type>();
            var nameMatches = new List<Type>();
            foreach (var asm in assemblies)
            {
                foreach (var t in SafeGetTypes(asm))
                {
                    if (t.FullName == name) fullNameMatches.Add(t);
                    else if (t.Name == name) nameMatches.Add(t);
                }
            }

            var pool = fullNameMatches.Count > 0 ? fullNameMatches : nameMatches;
            if (pool.Count == 1) { result.Type = pool[0]; return result; }
            if (pool.Count > 1)
            {
                result.Ambiguous = true;
                result.Candidates = pool
                    .Select(t => $"{t.FullName}, {t.Assembly.GetName().Name}")
                    .ToList();
            }
            return result;
        }

        public static MethodResolution ResolveStaticMethod(
            Type t, string methodName, JArray argTypesHint, JArray jsonArgs)
        {
            var result = new MethodResolution();
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public
                                        | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var all = t.GetMethods(flags).Where(m => m.Name == methodName).ToArray();
            if (all.Length == 0)
            {
                result.Error = $"no static method named '{methodName}' on '{t.FullName}'";
                result.Signatures = new List<string>();
                return result;
            }

            if (argTypesHint != null && argTypesHint.Count > 0)
            {
                var hints = argTypesHint.Select(j => (string)j).ToArray();
                var byHint = all.Where(m => MatchesHints(m, hints)).ToArray();
                if (byHint.Length == 1) { result.Method = byHint[0]; return result; }
                if (byHint.Length > 1)
                {
                    result.Error = "multiple methods match argTypes hint";
                    result.Signatures = byHint.Select(SignatureOf).ToList();
                    return result;
                }
                result.Error = "no method matches argTypes hint";
                result.Signatures = all.Select(SignatureOf).ToList();
                return result;
            }

            int argCount = jsonArgs?.Count ?? 0;
            var byArity = all.Where(m =>
            {
                var ps = m.GetParameters();
                int required = ps.Count(p => !p.IsOptional);
                return argCount >= required && argCount <= ps.Length;
            }).ToArray();

            if (byArity.Length == 1) { result.Method = byArity[0]; return result; }
            if (byArity.Length > 1)
            {
                result.Error = "ambiguous overload — pass argTypes to disambiguate";
                result.Signatures = byArity.Select(SignatureOf).ToList();
                return result;
            }

            result.Error = $"no overload of '{methodName}' accepts {argCount} argument(s)";
            result.Signatures = all.Select(SignatureOf).ToList();
            return result;
        }

        public static object[] CoerceArgs(MethodInfo method, JArray jsonArgs)
        {
            var ps = method.GetParameters();
            var coerced = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                if (jsonArgs == null || i >= jsonArgs.Count)
                {
                    coerced[i] = p.IsOptional ? Type.Missing : null;
                    continue;
                }
                coerced[i] = CoerceValue(jsonArgs[i], p.ParameterType, p.Name ?? $"arg{i}");
            }
            return coerced;
        }

        public static JToken SerializeReturn(object value, Type declaredType)
        {
            if (value == null) return JValue.CreateNull();

            if (value is UnityEngine.Object uobj)
            {
                var path = AssetDatabase.GetAssetPath(uobj);
                var o = new JObject
                {
                    ["instanceId"] = uobj.GetInstanceID(),
                    ["name"] = uobj.name,
                    ["type"] = uobj.GetType().FullName,
                };
                if (!string.IsNullOrEmpty(path)) o["assetPath"] = path;
                return o;
            }

            switch (value)
            {
                case Vector2 v2: return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                case Vector3 v3: return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                case Vector4 v4: return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                case Quaternion q: return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
                case Color c: return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
            }

            var t = value.GetType();
            if (t.IsEnum)
                return new JObject { ["enum"] = t.FullName, ["value"] = value.ToString() };
            if (t.IsPrimitive || value is string || value is decimal)
                return JToken.FromObject(value);

            try { return JToken.FromObject(value); }
            catch
            {
                return new JObject
                {
                    ["toString"] = value.ToString(),
                    ["type"] = t.FullName,
                };
            }
        }

        public static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { yield break; }
            foreach (var t in types)
                if (t != null) yield return t;
        }

        public static string SignatureOf(MethodInfo m)
        {
            var ps = m.GetParameters();
            var sb = new StringBuilder();
            sb.Append(TypeShort(m.ReturnType)).Append(' ').Append(m.Name).Append('(');
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TypeShort(ps[i].ParameterType)).Append(' ').Append(ps[i].Name);
                if (ps[i].IsOptional) sb.Append('?');
            }
            sb.Append(')');
            return sb.ToString();
        }

        private static bool MatchesHints(MethodInfo m, string[] hints)
        {
            var ps = m.GetParameters();
            if (ps.Length != hints.Length) return false;
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt.FullName != hints[i] && pt.Name != hints[i]) return false;
            }
            return true;
        }

        private static string TypeShort(Type t)
        {
            if (t == null) return "?";
            if (t == typeof(void)) return "void";
            return t.Name;
        }

        private static object CoerceValue(JToken token, Type targetType, string paramName)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    throw new InvalidOperationException(
                        $"param '{paramName}': null is not assignable to value-type '{targetType.FullName}'");
                return null;
            }

            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (typeof(UnityEngine.Object).IsAssignableFrom(underlying))
            {
                int instanceId;
                if (token.Type == JTokenType.Integer) instanceId = (int)token;
                else if (token is JObject obj && obj["instanceId"] != null) instanceId = (int)obj["instanceId"];
                else
                    throw new InvalidOperationException(
                        $"param '{paramName}': expected instanceId int or {{instanceId:int}} for UnityEngine.Object subtype '{underlying.FullName}'");
                var unityObj = EditorUtility.InstanceIDToObject(instanceId);
                if (unityObj == null)
                    throw new InvalidOperationException(
                        $"param '{paramName}': instanceId {instanceId} did not resolve to a UnityEngine.Object (destroyed or never existed)");
                if (!underlying.IsAssignableFrom(unityObj.GetType()))
                    throw new InvalidOperationException(
                        $"param '{paramName}': instanceId {instanceId} is {unityObj.GetType().FullName}, not assignable to {underlying.FullName}");
                return unityObj;
            }

            if (underlying == typeof(Vector3)) return JsonHelpers.ParseVector3(token);
            if (underlying == typeof(Vector2))
            {
                var v = JsonHelpers.ParseVector3(token);
                return new Vector2(v.x, v.y);
            }
            if (underlying == typeof(Vector4))
            {
                if (token is JArray arr && arr.Count >= 4)
                    return new Vector4((float)arr[0], (float)arr[1], (float)arr[2], (float)arr[3]);
                if (token is JObject ob)
                    return new Vector4(
                        (float?)ob["x"] ?? 0f,
                        (float?)ob["y"] ?? 0f,
                        (float?)ob["z"] ?? 0f,
                        (float?)ob["w"] ?? 0f);
                throw new InvalidOperationException(
                    $"param '{paramName}': expected Vector4 as [x,y,z,w] or {{x,y,z,w}}");
            }
            if (underlying == typeof(Quaternion)) return JsonHelpers.ParseRotation(token);
            if (underlying == typeof(Color)) return JsonHelpers.ParseColor(token);

            if (underlying.IsEnum)
            {
                if (token.Type == JTokenType.String)
                    return Enum.Parse(underlying, (string)token, ignoreCase: true);
                if (token.Type == JTokenType.Integer)
                    return Enum.ToObject(underlying, (int)token);
                throw new InvalidOperationException(
                    $"param '{paramName}': expected enum '{underlying.Name}' as string name or int");
            }

            try { return token.ToObject(targetType); }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"param '{paramName}' ({targetType.Name}): {e.Message}");
            }
        }
    }
}
