using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetSoPropertyTool : IMcpTool
    {
        public string Name => "set_so_property";
        public string Description =>
            "Atomically sets and saves a SerializedProperty on an editable ScriptableObject asset. Returns structured before/after values and asset identity. propertyPath uses Unity serialized form; non-nullable properties reject null.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["propertyPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["value"] = new JObject(),
            },
            ["required"] = new JArray { "assetGuidOrPath", "propertyPath", "value" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["asset"] = new JObject { ["type"] = "object" },
                ["propertyPath"] = new JObject { ["type"] = "string" },
                ["before"] = new JObject(),
                ["after"] = new JObject(),
                ["savedAssets"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "asset", "propertyPath", "before", "after", "savedAssets" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.SetProperty(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
