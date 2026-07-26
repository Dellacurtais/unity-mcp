using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect
{
    /// Enumerates user-defined [MenuItem] attributes across loaded assemblies.
    /// Unity's native menus are not discoverable through managed attributes.
    public sealed class ListMenuItemsTool : IMcpTool
    {
        private const int MaxLimit = 2000;

        public string Name => "list_menu_items";

        public string Description =>
            "Enumerates managed [MenuItem] attributes using Unity TypeCache. Returns user-defined and " +
            "package menu items in deterministic path order. Unity native menus are not listed, but " +
            "execute_menu_item can invoke a known exact native path. pathPrefix is a case-insensitive " +
            "path prefix; assemblyName is an exact case-insensitive assembly filter. Validation functions " +
            "are excluded unless includeValidate=true.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["pathPrefix"] = new JObject { ["type"] = "string", ["maxLength"] = 1024 },
                ["assemblyName"] = new JObject { ["type"] = "string", ["maxLength"] = 512 },
                ["includeValidate"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 500, ["minimum"] = 1, ["maximum"] = MaxLimit },
            },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["count"] = new JObject { ["type"] = "integer" },
                ["matched"] = new JObject { ["type"] = "integer" },
                ["truncated"] = new JObject { ["type"] = "boolean" },
                ["remaining"] = new JObject { ["type"] = "integer" },
                ["items"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["path"] = new JObject { ["type"] = "string" },
                            ["isValidate"] = new JObject { ["type"] = "boolean" },
                            ["priority"] = new JObject { ["type"] = "integer" },
                            ["definingType"] = new JObject { ["type"] = new JArray { "string", "null" } },
                            ["assembly"] = new JObject { ["type"] = "string" },
                            ["method"] = new JObject { ["type"] = "string" },
                        },
                        ["required"] = new JArray { "path", "isValidate", "priority", "definingType", "assembly", "method" },
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new JArray { "count", "matched", "truncated", "remaining", "items" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            args = args ?? new JObject();
            var pathPrefix = ((string)args["pathPrefix"] ?? string.Empty).Trim();
            var assemblyFilter = ((string)args["assemblyName"] ?? string.Empty).Trim();
            var includeValidate = (bool?)args["includeValidate"] ?? false;
            var limit = (int?)args["limit"] ?? 500;
            if (limit < 1 || limit > MaxLimit)
                return ToolResult.Error("BAD_ARGS", $"limit must be in 1..{MaxLimit}");
            if (pathPrefix.Length > 1024 || assemblyFilter.Length > 512)
                return ToolResult.Error("BAD_ARGS", "pathPrefix or assemblyName exceeds the supported length");

            var entries = new List<JObject>();
            foreach (var method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            {
                var declaringType = method.DeclaringType;
                var assemblyName = declaringType?.Assembly.GetName().Name ?? method.Module.Assembly.GetName().Name ?? string.Empty;
                if (!string.IsNullOrEmpty(assemblyFilter)
                    && !string.Equals(assemblyName, assemblyFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                MenuItem[] attributes;
                try { attributes = method.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>().ToArray(); }
                catch { continue; }
                foreach (var attribute in attributes)
                {
                    if (attribute == null || string.IsNullOrEmpty(attribute.menuItem)) continue;
                    if (!includeValidate && attribute.validate) continue;
                    if (!string.IsNullOrEmpty(pathPrefix)
                        && !attribute.menuItem.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    entries.Add(new JObject
                    {
                        ["path"] = attribute.menuItem,
                        ["isValidate"] = attribute.validate,
                        ["priority"] = attribute.priority,
                        ["definingType"] = declaringType?.FullName,
                        ["assembly"] = assemblyName,
                        ["method"] = method.Name,
                    });
                }
            }

            var ordered = entries
                .OrderBy(entry => (string)entry["path"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => (bool)entry["isValidate"])
                .ThenBy(entry => (int)entry["priority"])
                .ThenBy(entry => (string)entry["assembly"], StringComparer.Ordinal)
                .ThenBy(entry => (string)entry["definingType"], StringComparer.Ordinal)
                .ThenBy(entry => (string)entry["method"], StringComparer.Ordinal)
                .ToList();
            var page = new JArray(ordered.Take(limit));
            var remaining = Math.Max(0, ordered.Count - page.Count);
            var payload = new JObject
            {
                ["count"] = page.Count,
                ["matched"] = ordered.Count,
                ["truncated"] = remaining > 0,
                ["remaining"] = remaining,
                ["items"] = page,
            };
            return ToolResult.Ok(
                remaining > 0 ? $"{page.Count} menu item(s); {remaining} omitted" : $"{page.Count} menu item(s)",
                payload);
        }
    }
}
