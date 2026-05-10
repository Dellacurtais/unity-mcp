using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ModifyArrayPropertyTool : IMcpTool
    {
        public string Name => "modify_array_property";
        public string Description =>
            "Mutates an array/list SerializedProperty on either a Component (instanceId + componentType) OR a ScriptableObject asset (assetGuidOrPath). " +
            "Resolves Unity's internal `Array.size` / `Array.data[N]` mechanics so the agent doesn't have to know that detail. " +
            "Actions: " +
            "`append` (push value at end), " +
            "`insert` (at index, shifts the rest), " +
            "`remove` (delete at index — handles ObjectReference's two-step null-then-delete quirk), " +
            "`set` (overwrite element at index), " +
            "`resize` (set new size N — fills with default for non-object types), " +
            "`clear` (empty the array). " +
            "value uses the same parser as set_property (int/bool/float/string/Color/Vector*/LayerMask/ObjectReference via instanceId or GUID/path).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetGuidOrPath"] = new JObject { ["type"] = "string", ["description"] = "Path/GUID of a ScriptableObject asset (target = SO)." },
                ["instanceId"] = new JObject { ["type"] = "integer", ["description"] = "GameObject instanceId (target = Component)." },
                ["componentType"] = new JObject { ["type"] = "string", ["description"] = "Required when instanceId is set." },
                ["propertyPath"] = new JObject { ["type"] = "string", ["description"] = "Path of the array/list field, e.g. 'items' or 'sections.entries'." },
                ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "append", "insert", "remove", "set", "resize", "clear" }, ["default"] = "append" },
                ["value"] = new JObject { ["description"] = "Element value (append/insert/set)." },
                ["index"] = new JObject { ["type"] = "integer", ["description"] = "Required for insert/remove/set." },
                ["size"] = new JObject { ["type"] = "integer", ["description"] = "Required for resize." },
            },
            ["required"] = new JArray { "propertyPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.ModifyArrayProperty(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
