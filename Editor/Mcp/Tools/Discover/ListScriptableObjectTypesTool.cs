using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ListScriptableObjectTypesTool : IMcpTool
    {
        public string Name => "list_scriptable_object_types";
        public string Description =>
            "Enumerates every concrete ScriptableObject subclass loaded in the editor (across project assemblies + Unity packages). Each entry includes its full type name, assembly, and any [CreateAssetMenu] attribute (menu path + default file name) — that's the right-click → Create entry the user would normally use. Filter by `nameContains`, `category` ('all' / 'builtin' / 'user'), `withMenuOnly: true` to surface only types the project actually exposes via the Create menu. Use BEFORE create_scriptable_object so you pick a real className.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["nameContains"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["category"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "all", "builtin", "user" }, ["default"] = "all" },
                ["withMenuOnly"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 200 },
            },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["total"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["returned"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["truncated"] = new JObject { ["type"] = "boolean" },
                ["types"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "total", "returned", "offset", "truncated", "types" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.ListTypes(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
