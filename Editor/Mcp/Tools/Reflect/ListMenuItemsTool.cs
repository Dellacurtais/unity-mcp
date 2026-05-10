using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect
{
    /// Enumerates user-defined [MenuItem] attributes across loaded assemblies.
    /// Unity's native menus (most of GameObject/, File/, Edit/) live in C++ and
    /// are NOT discoverable here — execute_menu_item still invokes them when
    /// the path is known. Use this for surfacing custom Tools/* and asset-store
    /// (Probuilder, Odin, etc.) menu items the agent could call.
    public sealed class ListMenuItemsTool : IMcpTool
    {
        // Skipped to keep the sweep cheap — these never define [MenuItem].
        private static readonly string[] SkipPrefixes =
        {
            "mscorlib", "System", "netstandard", "Newtonsoft.Json",
            "nunit.framework", "Mono.Cecil", "Microsoft.",
        };

        public string Name => "list_menu_items";

        public string Description =>
            "Enumerates [MenuItem] attributes from loaded assemblies. Returns user-defined and " +
            "asset-store menu items (e.g. Tools/MyBuild, Window/Probuilder). Unity's native menus " +
            "(GameObject/, File/, Edit/, etc.) live in C++ and are NOT listed — execute_menu_item " +
            "still runs them when the path is known. Filter by pathPrefix substring (case-insensitive) " +
            "and/or assemblyName. includeValidate=true also returns validate functions (MenuItem(.., true)).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["pathPrefix"] = new JObject { ["type"] = "string" },
                ["assemblyName"] = new JObject { ["type"] = "string" },
                ["includeValidate"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 500, ["minimum"] = 1 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var pathPrefix = (string)args["pathPrefix"];
            var asmFilter = (string)args["assemblyName"];
            var includeValidate = (bool?)args["includeValidate"] ?? false;
            int limit = Math.Max(1, (int?)args["limit"] ?? 500);

            var entries = new JArray();
            int truncatedFrom = 0;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (ShouldSkipAssembly(asmName)) continue;
                if (!string.IsNullOrEmpty(asmFilter) &&
                    !string.Equals(asmName, asmFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var t in ReflectionResolver.SafeGetTypes(asm))
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                               | BindingFlags.Static | BindingFlags.Instance
                                               | BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }

                    foreach (var m in methods)
                    {
                        MenuItem[] attrs;
                        try
                        {
                            attrs = (MenuItem[])m.GetCustomAttributes(typeof(MenuItem), false);
                        }
                        catch { continue; }
                        if (attrs == null || attrs.Length == 0) continue;

                        foreach (var attr in attrs)
                        {
                            if (!includeValidate && attr.validate) continue;
                            if (!string.IsNullOrEmpty(pathPrefix) &&
                                attr.menuItem.IndexOf(pathPrefix, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            if (entries.Count >= limit)
                            {
                                truncatedFrom++;
                                continue;
                            }

                            entries.Add(new JObject
                            {
                                ["path"] = attr.menuItem,
                                ["isValidate"] = attr.validate,
                                ["priority"] = attr.priority,
                                ["definingType"] = t.FullName,
                                ["assembly"] = asmName,
                                ["method"] = m.Name,
                            });
                        }
                    }
                }
            }

            var payload = new JObject
            {
                ["count"] = entries.Count,
                ["items"] = entries,
            };
            if (truncatedFrom > 0) payload["truncated"] = truncatedFrom;

            var summary = truncatedFrom > 0
                ? $"{entries.Count} menu item(s) (truncated; +{truncatedFrom} more)"
                : $"{entries.Count} menu item(s)";
            return ToolResult.Ok(summary, payload);
        }

        private static bool ShouldSkipAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            for (int i = 0; i < SkipPrefixes.Length; i++)
            {
                var p = SkipPrefixes[i];
                if (name == p || name.StartsWith(p + ".", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
