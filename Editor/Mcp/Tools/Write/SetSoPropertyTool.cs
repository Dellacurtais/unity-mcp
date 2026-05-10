using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetSoPropertyTool : IMcpTool
    {
        public string Name => "set_so_property";
        public string Description =>
            "Sets a SerializedProperty on a ScriptableObject ASSET (not a Component on a GameObject — for that use set_property). Same value-type coverage as set_property: int/bool/float/string/Color/Vector*/LayerMask/Bounds/Rect/enum/ObjectReference. propertyPath uses Unity serialized form (the field's serialized name; nested with dots).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetGuidOrPath"] = new JObject { ["type"] = "string" },
                ["propertyPath"] = new JObject { ["type"] = "string" },
                ["value"] = new JObject(),
            },
            ["required"] = new JArray { "assetGuidOrPath", "propertyPath", "value" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.SetProperty(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
