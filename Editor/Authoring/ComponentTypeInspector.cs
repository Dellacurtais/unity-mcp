using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            var componentBase = typeof(Component);
            int count = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                bool isBuiltin = IsBuiltinAssembly(asmName);
                if (category == "user" && isBuiltin) continue;
                if (category == "builtin" && !isBuiltin) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException tle) { types = tle.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!componentBase.IsAssignableFrom(t)) continue;
                    if (t.IsAbstract) continue;
                    if (t.IsGenericType) continue;
                    if (!string.IsNullOrEmpty(nameContains)
                        && t.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0
                        && (t.FullName ?? "").IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    yield return Brief(t, isBuiltin);
                    count++;
                    if (count >= limit) yield break;
                }
            }
        }

        public static TypeDescriptor Describe(string typeName, bool includeMethods = false)
        {
            var t = ResolveType(typeName);
            if (t == null) return null;
            var asmName = t.Assembly.GetName().Name ?? "";
            var d = Brief(t, IsBuiltinAssembly(asmName));
            d.Fields = GetSerializedFields(t);
            if (includeMethods) d.Methods = GetPublicMethods(t);
            return d;
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

        public static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
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

        private static bool IsBuiltinAssembly(string asmName)
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
