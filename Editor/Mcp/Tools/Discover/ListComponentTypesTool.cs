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
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 200, ["minimum"] = 1 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var name = (string)args["nameContains"];
            var category = (string)args["category"] ?? "all";
            var limit = (int?)args["limit"] ?? 200;

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

            return ToolResult.Ok($"{count} component type(s) (category={category})",
                new JObject { ["count"] = count, ["types"] = arr });
        }
    }
}
