using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimatorTransitionTool : IMcpTool
    {
        public string Name => "add_animator_transition";
        public string Description => "Adds a validated state or AnyState transition with typed conditions, normalized/fixed duration controls and atomic Undo.";
        public JObject InputSchema => AnimationToolContracts.ControllerInputSchema(new JObject
        {
            ["from"] = new JObject { ["type"] = "string" },
            ["to"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            ["layer"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
            ["hasExitTime"] = new JObject { ["type"] = "boolean", ["default"] = false },
            ["exitTime"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 0.85 },
            ["duration"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 0.25 },
            ["fixedDuration"] = new JObject { ["type"] = "boolean", ["default"] = false },
            ["offset"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 0 },
            ["conditions"] = new JObject
            {
                ["type"] = "array", ["maxItems"] = 64,
                ["items"] = ConditionSchema(),
            },
        }, "to");
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.AddTransition(args));

        private static JObject ConditionSchema()
        {
            var properties = new JObject
            {
                ["parameter"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Greater", "Less", "Equals", "NotEqual", "If", "IfNot" } },
                ["threshold"] = new JObject { ["type"] = "number" },
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["oneOf"] = new JArray
                {
                    ConditionVariant(new[] { "If", "IfNot" }, null),
                    ConditionVariant(new[] { "Greater", "Less" }, new JObject { ["type"] = "number" }),
                    ConditionVariant(new[] { "Equals", "NotEqual" }, new JObject { ["type"] = "integer" }),
                },
                ["additionalProperties"] = false,
            };
        }

        private static JObject ConditionVariant(string[] modes, JObject thresholdSchema)
        {
            var properties = new JObject
            {
                ["parameter"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray(modes) },
            };
            var required = new JArray { "parameter", "mode" };
            if (thresholdSchema != null)
            {
                properties["threshold"] = thresholdSchema;
                required.Add("threshold");
            }
            return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        }
    }
}
