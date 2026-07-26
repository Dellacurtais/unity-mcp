using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Reflection-based discovery for every UnityEngine.Component subtype the
    /// editor knows about. Used by `list_component_types` and
    /// `describe_component_type` so the agent can learn what's installable on
    /// a GameObject and what fields each one exposes — without needing an
    /// instance in the scene.
    public static class ComponentTypeInspector
    {
        public sealed class TypeDescriptor
        {
            public string Name;
            public string FullName;
            public string Namespace;
            public string Assembly;
            public bool IsMonoBehaviour;
            public bool IsBuiltin;
            public bool IsAbstract;
            public List<string> RequiredComponents;
            public List<FieldDescriptor> Fields;
            public List<string> Methods; // public instance methods, optional
        }

        public sealed class FieldDescriptor
        {
            public string Name;
            public string Type;
            public bool IsArray;
            public string ArrayElementType;
            public bool IsRequired;
            public Dictionary<string, object> Attributes;
        }

        // ---- Listing ----

        public static IEnumerable<TypeDescriptor> ListTypes(string nameContains, string category, int limit)
        {
            var normalizedCategory = string.IsNullOrEmpty(category) ? "all" : category.ToLowerInvariant();
            return TypeCache.GetTypesDerivedFrom<Component>()
                .Where(t => t != null && !t.IsAbstract && !t.IsGenericType && !t.ContainsGenericParameters)
                .Where(t =>
                {
                    var isBuiltin = IsBuiltinAssembly(t.Assembly.GetName().Name ?? string.Empty);
                    return normalizedCategory == "all"
                        || (normalizedCategory == "builtin" && isBuiltin)
                        || (normalizedCategory == "user" && !isBuiltin);
                })
                .Where(t => string.IsNullOrEmpty(nameContains)
                    || t.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0
                    || (t.FullName ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.FullName ?? t.Name, StringComparer.Ordinal)
                .ThenBy(t => t.Assembly.GetName().Name, StringComparer.Ordinal)
                .Take(Math.Max(0, limit))
                .Select(t => Brief(t, IsBuiltinAssembly(t.Assembly.GetName().Name ?? string.Empty)))
                .ToArray();
        }

        public static int CountTypes(string nameContains, string category)
        {
            return ListTypes(nameContains, category, int.MaxValue).Count();
        }

        public static TypeDescriptor Describe(string typeName, bool includeMethods = false)
        {
            return Describe(typeName, null, includeMethods, out _);
        }

        public static TypeDescriptor Describe(string typeName, string assemblyName, bool includeMethods, out string error)
        {
            var t = ResolveComponentType(typeName, assemblyName, out error);
            if (t == null) return null;
            var asmName = t.Assembly.GetName().Name ?? string.Empty;
            var d = Brief(t, IsBuiltinAssembly(asmName));
            d.Fields = GetSerializedFields(t)
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .ToList();
            if (includeMethods) d.Methods = GetPublicMethods(t).OrderBy(method => method, StringComparer.Ordinal).ToList();
            return d;
        }

        private static Type ResolveComponentType(string typeName, string assemblyName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(typeName)) { error = "BAD_ARGS: componentType is required"; return null; }
            var matches = TypeCache.GetTypesDerivedFrom<Component>()
                .Where(t => t != null && (string.Equals(t.FullName, typeName, StringComparison.Ordinal)
                    || string.Equals(t.Name, typeName, StringComparison.Ordinal)))
                .Where(t => string.IsNullOrEmpty(assemblyName)
                    || string.Equals(t.Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.FullName ?? t.Name, StringComparer.Ordinal)
                .ThenBy(t => t.Assembly.GetName().Name, StringComparer.Ordinal)
                .ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1)
            {
                error = "AMBIGUOUS_COMPONENT_TYPE: multiple component types match; pass assemblyName or fullName. Candidates: "
                    + string.Join(", ", matches.Select(t => $"{t.FullName}, {t.Assembly.GetName().Name}"));
                return null;
            }
            error = $"COMPONENT_TYPE_NOT_FOUND: component type '{typeName}' was not found";
            return null;
        }

        // ---- Helpers ----

        private static TypeDescriptor Brief(Type t, bool isBuiltin)
        {
            var d = new TypeDescriptor
            {
                Name = t.Name,
                FullName = t.FullName,
                Namespace = t.Namespace,
                Assembly = t.Assembly.GetName().Name,
                IsBuiltin = isBuiltin,
                IsMonoBehaviour = typeof(MonoBehaviour).IsAssignableFrom(t),
                IsAbstract = t.IsAbstract,
            };

            var requires = t.GetCustomAttributes(typeof(RequireComponent), true);
            if (requires.Length > 0)
            {
                d.RequiredComponents = new List<string>();
                foreach (RequireComponent rc in requires)
                {
                    if (rc.m_Type0 != null) d.RequiredComponents.Add(rc.m_Type0.Name);
                    if (rc.m_Type1 != null) d.RequiredComponents.Add(rc.m_Type1.Name);
                    if (rc.m_Type2 != null) d.RequiredComponents.Add(rc.m_Type2.Name);
                }
            }
            return d;
        }

        public static List<FieldDescriptor> GetSerializedFields(Type t)
        {
            var result = new List<FieldDescriptor>();
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in fields)
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                var nonSer = f.GetCustomAttributes(typeof(NonSerializedAttribute), true);
                if (nonSer.Length > 0) continue;

                bool serializable = f.IsPublic
                    || f.GetCustomAttributes(typeof(SerializeField), true).Length > 0;
                if (!serializable) continue;

                var ft = f.FieldType;
                var fd = new FieldDescriptor
                {
                    Name = f.Name,
                    Type = NiceTypeName(ft),
                };
                if (ft.IsArray)
                {
                    fd.IsArray = true;
                    fd.ArrayElementType = NiceTypeName(ft.GetElementType());
                }
                else if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>))
                {
                    fd.IsArray = true;
                    fd.ArrayElementType = NiceTypeName(ft.GetGenericArguments()[0]);
                }

                var attrs = new Dictionary<string, object>();
                var range = f.GetCustomAttribute<RangeAttribute>();
                if (range != null) attrs["range"] = $"{range.min}..{range.max}";
                var tooltip = f.GetCustomAttribute<TooltipAttribute>();
                if (tooltip != null) attrs["tooltip"] = tooltip.tooltip;
                var header = f.GetCustomAttribute<HeaderAttribute>();
                if (header != null) attrs["header"] = header.header;
                var min = f.GetCustomAttribute<MinAttribute>();
                if (min != null) attrs["min"] = min.min;
                var multiline = f.GetCustomAttribute<MultilineAttribute>();
                if (multiline != null) attrs["multiline"] = multiline.lines;
                var textArea = f.GetCustomAttribute<TextAreaAttribute>();
                if (textArea != null) attrs["textArea"] = $"{textArea.minLines}..{textArea.maxLines}";
                var hideIn = f.GetCustomAttributes(typeof(HideInInspector), true);
                if (hideIn.Length > 0) attrs["hideInInspector"] = true;
                var requiredAttrs = f.GetCustomAttributes(typeof(RequiredFieldAttribute), true);
                if (requiredAttrs.Length > 0) fd.IsRequired = true;

                if (attrs.Count > 0) fd.Attributes = attrs;
                result.Add(fd);
            }
            return result;
        }

        public static List<string> GetPublicMethods(Type t)
        {
            var seen = new HashSet<string>();
            var list = new List<string>();
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                var sig = m.Name + "(" + string.Join(",", m.GetParameters().Select(p => NiceTypeName(p.ParameterType))) + ")";
                if (seen.Add(sig)) list.Add(sig);
            }
            return list;
        }

        // Reflection over AppDomain.GetAssemblies() + asm.GetTypes() costs ~1M ops per
        // miss on a typical Unity project. Cache hits (including null misses) skip it
        // entirely. Domain reload destroys statics → cache repopulates naturally.
        // Safe without locks: all callers reach this via MainThreadDispatcher.
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        public static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_typeCache.TryGetValue(name, out var cached)) return cached;
            var resolved = ResolveTypeUncached(name);
            _typeCache[name] = resolved;
            return resolved;
        }

        private static Type ResolveTypeUncached(string name)
        {
            // try full names + UnityEngine fast path
            var direct = Type.GetType(name) ?? Type.GetType($"UnityEngine.{name}, UnityEngine");
            if (direct != null) return direct;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null) return t;
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var c in types)
                {
                    if (c == null) continue;
                    if (string.Equals(c.Name, name, StringComparison.Ordinal)
                        && typeof(Component).IsAssignableFrom(c)) return c;
                }
            }
            return null;
        }

        public static bool IsBuiltinAssembly(string asmName)
        {
            return asmName.StartsWith("UnityEngine", StringComparison.Ordinal)
                || asmName.StartsWith("Unity.", StringComparison.Ordinal)
                || asmName.StartsWith("UnityEditor", StringComparison.Ordinal)
                || asmName == "mscorlib"
                || asmName == "System"
                || asmName.StartsWith("System.", StringComparison.Ordinal);
        }

        private static string NiceTypeName(Type t)
        {
            if (t == null) return "?";
            if (t.IsArray) return NiceTypeName(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition().Name;
                int tick = def.IndexOf('`');
                if (tick > 0) def = def.Substring(0, tick);
                return def + "<" + string.Join(",", t.GetGenericArguments().Select(NiceTypeName)) + ">";
            }
            return t.Name;
        }
    }
}
