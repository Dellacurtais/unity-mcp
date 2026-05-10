using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadScriptableObjectTool : IMcpTool
    {
        public string Name => "read_scriptable_object";
        public string Description =>
            "Inspects a ScriptableObject asset: dumps every visible SerializedProperty with its name, type, array size (when applicable), and a short value description. Skips `m_Script`. Truncated at 80 fields (rare). Use this when read_component would apply (Component on GO) doesn't — for SO data assets like configs, item databases, event channels.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["guidOrPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.Read(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
