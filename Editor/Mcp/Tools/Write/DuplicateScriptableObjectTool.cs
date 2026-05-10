using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class DuplicateScriptableObjectTool : IMcpTool
    {
        public string Name => "duplicate_scriptable_object";
        public string Description =>
            "Clones an existing ScriptableObject asset and saves it at targetPath. All field values are copied. The new asset is independent — modifying it does not affect the source.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sourceGuidOrPath"] = new JObject { ["type"] = "string" },
                ["targetPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "sourceGuidOrPath", "targetPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.Duplicate(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID })
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
