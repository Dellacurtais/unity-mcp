using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ListComponentTypesTool : IMcpTool
    {
        public string Name => "list_component_types";
        public string Description =>
            "Enumerates every concrete UnityEngine.Component subtype the editor has loaded — built-in (Rigidbody, BoxCollider, Light, …) AND user MonoBehaviours from your project assemblies. Filter by name substring, by category ('builtin' / 'user' / 'all'), and cap with limit. Use this BEFORE add_component to know what's installable. For per-type schema (fields, attributes, RequireComponent), call describe_component_type next.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["nameContains"] = new JObject { ["type"] = "string" },
                ["category"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "all", "builtin", "user" }, ["default"] = "all" },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 200, ["minimum"] = 1, ["maximum"] = 2000 },
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
                ["types"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "count", "matched", "truncated", "remaining", "types" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            args = args ?? new JObject();
            var name = (string)args["nameContains"];
            var category = ((string)args["category"] ?? "all").ToLowerInvariant();
            var limit = (int?)args["limit"] ?? 200;
            if (category != "all" && category != "builtin" && category != "user")
                return ToolResult.Error("BAD_ARGS", "category must be all, builtin or user");
            if (limit < 1 || limit > 2000)
                return ToolResult.Error("BAD_ARGS", "limit must be in 1..2000");

            var matched = ComponentTypeInspector.CountTypes(name, category);
            var arr = new JArray();
            int count = 0;
            foreach (var d in ComponentTypeInspector.ListTypes(name, category, limit))
            {
                var o = new JObject
                {
                    ["name"] = d.Name,
                    ["fullName"] = d.FullName,
                    ["namespace"] = d.Namespace,
                    ["assembly"] = d.Assembly,
                    ["isMonoBehaviour"] = d.IsMonoBehaviour,
                    ["isBuiltin"] = d.IsBuiltin,
                };
                if (d.RequiredComponents != null)
                {
                    var req = new JArray();
                    foreach (var r in d.RequiredComponents) req.Add(r);
                    o["requires"] = req;
                }
                arr.Add(o);
                count++;
            }

            var remaining = System.Math.Max(0, matched - count);
            return ToolResult.Ok($"{count} component type(s) (category={category})",
                new JObject
                {
                    ["count"] = count,
                    ["matched"] = matched,
                    ["truncated"] = remaining > 0,
                    ["remaining"] = remaining,
                    ["types"] = arr,
                });
        }
    }
}
