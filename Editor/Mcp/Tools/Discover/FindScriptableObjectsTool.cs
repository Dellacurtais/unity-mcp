using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class FindScriptableObjectsTool : IMcpTool
    {
        public string Name => "find_scriptable_objects";
        public string Description =>
            "Lists every .asset of a given ScriptableObject subclass (and its descendants) under the project. Bridges list_scriptable_object_types (which shows TYPES) to the actual asset instances. " +
            "className accepts simple ('EnemyData'), fully-qualified ('Game.EnemyData'), or assembly-qualified names; ambiguous simple names are rejected. Optional pathPrefix is a canonical Assets folder. Results are deterministic and paginated with offset/limit. " +
            "Querying a base class also surfaces derived assets via IsAssignableFrom validation.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["className"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["pathPrefix"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets(?:/(?!.*(?:^|/)\\.{1,2}(?:/|$))(?!.*//)(?!.*\\\\).+)?$", ["default"] = "Assets", ["description"] = "Canonical project folder path." },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 200 },
            },
            ["required"] = new JArray { "className" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["type"] = new JObject { ["type"] = "string" },
                ["searchPath"] = new JObject { ["type"] = "string" },
                ["total"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["returned"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["truncated"] = new JObject { ["type"] = "boolean" },
                ["assets"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "type", "searchPath", "total", "returned", "offset", "truncated", "assets" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.FindInstances(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
