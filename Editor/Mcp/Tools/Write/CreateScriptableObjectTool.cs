using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateScriptableObjectTool : IMcpTool
    {
        public string Name => "create_scriptable_object";
        public string Description =>
            "Creates a ScriptableObject .asset on disk. className is the C# class (simple name 'EnemyData' or full 'Game.EnemyData'). path is under Assets/ (folders auto-created). " +
            "initialFields is an optional JObject mapping SerializedProperty names → values applied at creation (same value parsing as set_property: int/bool/float/string/Color/Vector*/LayerMask/enum/ObjectReference). " +
            "If the class was just created in code, call refresh_assets first so the type is available.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["className"] = new JObject { ["type"] = "string" },
                ["path"] = new JObject { ["type"] = "string", ["description"] = "e.g. Assets/Data/Goblin.asset" },
                ["initialFields"] = new JObject { ["type"] = "object" },
            },
            ["required"] = new JArray { "className", "path" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = ScriptableObjectAuthor.Create(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID })
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
