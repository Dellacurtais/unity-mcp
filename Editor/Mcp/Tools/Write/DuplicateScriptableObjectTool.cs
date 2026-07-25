using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class DuplicateScriptableObjectTool : IMcpTool
    {
        public string Name => "duplicate_scriptable_object";
        public string Description =>
            "Copies a ScriptableObject asset, including sub-assets, to a new canonical .asset targetPath. Existing targets are never overwritten; failed copies remove partial assets and auto-created empty folders.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sourceGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["targetPath"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["pattern"] = "^Assets/(?!.*(?:^|/)\\.{1,2}(?:/|$))(?!.*//)(?!.*\\\\).+\\.asset$" },
            },
            ["required"] = new JArray { "sourceGuidOrPath", "targetPath" },
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
            var r = ScriptableObjectAuthor.Duplicate(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, r.Data)
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
