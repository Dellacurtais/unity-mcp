using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetAnimatorParameterTool : IMcpTool
    {
        public string Name => "set_animator_parameter";
        public string Description =>
            "Sets a runtime Animator parameter. type: 'float' / 'int' / 'bool' / 'trigger' / 'auto' (default — read type from controller). For trigger, omit value to fire SetTrigger; pass value=false to ResetTrigger. Effects are most visible in Play Mode.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "auto", "float", "int", "bool", "trigger" }, ["default"] = "auto" },
                ["value"] = new JObject(),
            },
            ["required"] = new JArray { "instanceId", "name" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.SetAnimatorParameter(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
