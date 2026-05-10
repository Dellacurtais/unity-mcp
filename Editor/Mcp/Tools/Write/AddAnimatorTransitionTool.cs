using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimatorTransitionTool : IMcpTool
    {
        public string Name => "add_animator_transition";
        public string Description =>
            "Adds a transition between states. `from` is the source state name, OR omit/use 'AnyState' / '*' for an any-state transition. `to` is required. " +
            "`conditions` is an array of `{ parameter, mode, threshold }` where mode is Greater/Less/Equals/NotEqual/If/IfNot. " +
            "Defaults: hasExitTime=false, duration=0.25, exitTime=0.85, offset=0.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["controllerInstanceId"] = new JObject { ["type"] = "integer" },
                ["from"] = new JObject { ["type"] = "string" },
                ["to"] = new JObject { ["type"] = "string" },
                ["layer"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["hasExitTime"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["exitTime"] = new JObject { ["type"] = "number" },
                ["duration"] = new JObject { ["type"] = "number" },
                ["offset"] = new JObject { ["type"] = "number" },
                ["conditions"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["parameter"] = new JObject { ["type"] = "string" },
                            ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Greater", "Less", "Equals", "NotEqual", "If", "IfNot" } },
                            ["threshold"] = new JObject { ["type"] = "number" },
                        },
                    },
                },
            },
            ["required"] = new JArray { "to" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.AddTransition(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
