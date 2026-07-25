using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateScriptableObjectTool : IMcpTool
    {
        public string Name => "create_scriptable_object";
        public string Description =>
            "Creates a new ScriptableObject .asset without overwriting an existing path. className accepts simple, fully-qualified, or assembly-qualified names; ambiguous simple names are rejected. path must be a canonical .asset path under Assets/ (folders auto-created, except StreamingAssets). " +
            "initialFields is an optional object mapping SerializedProperty paths to values. Every supplied field is validated before the asset is written; any error leaves no asset or folder residue. " +
            "If the class was just created in code, call refresh_assets first so the type is available.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["className"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["path"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["pattern"] = "^Assets/(?!.*(?:^|/)\\.{1,2}(?:/|$))(?!.*//)(?!.*\\\\).+\\.asset$", ["description"] = "Canonical .asset path, e.g. Assets/Data/Goblin.asset" },
                ["initialFields"] = new JObject { ["type"] = "object" },
            },
            ["required"] = new JArray { "className", "path" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["asset"] = new JObject { ["type"] = "object" },
                ["savedAssets"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "asset", "savedAssets" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.Create(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, r.Data)
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
