using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ModifyArrayPropertyTool : IMcpTool
    {
        public string Name => "modify_array_property";
        public string Description =>
            "Atomically mutates an array/list SerializedProperty on either a Component (stable target or instanceId + componentType) OR an editable ScriptableObject asset (assetGuidOrPath). " +
            "Resolves Unity's internal `Array.size` / `Array.data[N]` mechanics so the agent doesn't have to know that detail. " +
            "Actions: " +
            "`append` (push value at end), " +
            "`insert` (at index, shifts the rest), " +
            "`remove` (delete at index — handles ObjectReference's two-step null-then-delete quirk), " +
            "`set` (overwrite element at index), " +
            "`resize` (set size N; expansion requires value and applies it to every new element), " +
            "`clear` (empty the array). " +
            "append/insert/set require value; inserted structs require all serialized fields because Unity documents inserted values as undefined. ScriptableObject mutations are saved immediately and all writes return before/after values.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Path/GUID of a ScriptableObject asset (target = SO)." },
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["instanceId"] = new JObject { ["type"] = "integer", ["description"] = "Session-local GameObject instanceId fallback." },
                ["componentType"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Required when targeting a GameObject." },
                ["componentOrdinal"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["propertyPath"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Path of the array/list field, e.g. 'items' or 'sections.entries'." },
                ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "append", "insert", "remove", "set", "resize", "clear" }, ["default"] = "append" },
                ["value"] = new JObject { ["description"] = "Element value (append/insert/set)." },
                ["index"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Required for insert/remove/set." },
                ["size"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Required for resize; value is also required when expanding." },
            },
            ["required"] = new JArray { "propertyPath" },
            ["oneOf"] = new JArray
            {
                new JObject { ["required"] = new JArray { "assetGuidOrPath" } },
                new JObject { ["required"] = new JArray { "target", "componentType" } },
                new JObject { ["required"] = new JArray { "instanceId", "componentType" } },
            },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["assetPath"] = new JObject { ["oneOf"] = new JArray { new JObject { ["type"] = "string" }, new JObject { ["type"] = "null" } } },
                ["propertyPath"] = new JObject { ["type"] = "string" },
                ["action"] = new JObject { ["type"] = "string" },
                ["before"] = new JObject(),
                ["after"] = new JObject(),
                ["arraySize"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["savedAssets"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "target", "assetPath", "propertyPath", "action", "before", "after", "arraySize", "savedAssets" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.ModifyArrayProperty(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
