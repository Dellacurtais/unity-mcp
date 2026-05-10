using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimatorParameterTool : IMcpTool
    {
        public string Name => "add_animator_parameter";
        public string Description =>
            "Registers a parameter on an AnimatorController. type: Float / Int / Bool / Trigger. defaultValue is optional (number for Float/Int, bool for Bool, ignored for Trigger).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["controllerInstanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Float", "Int", "Bool", "Trigger" }, ["default"] = "Float" },
                ["defaultValue"] = new JObject(),
            },
            ["required"] = new JArray { "name" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.AddParameter(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
