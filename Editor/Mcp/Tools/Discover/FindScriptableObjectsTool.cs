using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class FindScriptableObjectsTool : IMcpTool
    {
        public string Name => "find_scriptable_objects";
        public string Description =>
            "Lists every .asset of a given ScriptableObject subclass (and its descendants) under the project. Bridges list_scriptable_object_types (which shows TYPES) to the actual asset instances. " +
            "className accepts simple ('EnemyData') or fully-qualified ('Game.EnemyData'). Optional pathPrefix filters results to a folder ('Assets/Data/Enemies'). limit caps the listing (default 200). " +
            "Querying a base class also surfaces derived assets via IsAssignableFrom validation.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["className"] = new JObject { ["type"] = "string" },
                ["pathPrefix"] = new JObject { ["type"] = "string", ["description"] = "Optional folder filter, e.g. 'Assets/Data/Enemies'." },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 200 },
            },
            ["required"] = new JArray { "className" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.FindInstances(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
