using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetAnimatorParameterTool : IMcpTool
    {
        public string Name => "set_animator_parameter";
        public string Description => "Play Mode only. Sets a live Animator parameter after validating the controller's real parameter type; trigger value=false resets it.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["oneOf"] = new JArray
            {
                Variant(null, null, false),
                Variant(null, ScalarValueSchema(), true),
                Variant("auto", null, false),
                Variant("auto", ScalarValueSchema(), true),
                Variant("float", new JObject { ["type"] = "number" }, true),
                Variant("int", new JObject { ["type"] = "integer" }, true),
                Variant("bool", new JObject { ["type"] = "boolean" }, true),
                Variant("trigger", new JObject { ["type"] = "boolean" }, false),
            },
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.RuntimeWriteDescriptor(Name, true);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.SetAnimatorParameter(args));

        private static JObject Variant(string type, JObject valueSchema, bool requireValue)
        {
            var properties = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            };
            var required = new JArray { "instanceId", "name" };
            if (type != null)
            {
                properties["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray { type } };
                required.Add("type");
            }
            if (valueSchema != null)
            {
                properties["value"] = valueSchema;
                if (requireValue) required.Add("value");
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        private static JObject ScalarValueSchema() => new JObject
        {
            ["type"] = new JArray { "number", "boolean" },
        };
    }
}
