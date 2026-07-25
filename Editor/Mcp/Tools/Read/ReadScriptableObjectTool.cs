using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadScriptableObjectTool : IMcpTool
    {
        public string Name => "read_scriptable_object";
        public string Description =>
            "Inspects a ScriptableObject asset through Unity serialization and returns structured top-level fields with property paths, Unity types, array sizes, and recursively encoded values. Skips m_Script. Use offset/limit for fields and maxDepth/maxArrayItems for nested values.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["guidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 80 },
                ["maxDepth"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 16, ["default"] = 4 },
                ["maxArrayItems"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 32 },
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["asset"] = new JObject { ["type"] = "object" },
                ["total"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["returned"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["truncated"] = new JObject { ["type"] = "boolean" },
                ["fields"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "asset", "total", "returned", "offset", "truncated", "fields" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.Read(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
