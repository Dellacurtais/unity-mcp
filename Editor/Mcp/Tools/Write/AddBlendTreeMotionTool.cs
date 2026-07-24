using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddBlendTreeMotionTool : IMcpTool
    {
        public string Name => "add_blend_tree_motion";
        public string Description => "Adds an AnimationClip child using exactly one tree-compatible coordinate: threshold (1D), position (2D), or directBlendParameter (Direct).";
        public JObject InputSchema => Schema();
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.AddBlendTreeMotion(args));

        private static JObject Schema()
        {
            var schema = AnimationToolContracts.ControllerInputSchema(new JObject
            {
                ["state"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["layer"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["clipGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["threshold"] = new JObject { ["type"] = "number" },
                ["position"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2 },
                ["directBlendParameter"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            }, "state", "clipGuidOrPath");
            schema["allOf"] = new JArray
            {
                new JObject
                {
                    ["oneOf"] = new JArray
                    {
                        Shape("threshold", new JObject { ["type"] = "number" }),
                        Shape("position", new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2 }),
                        Shape("directBlendParameter", new JObject { ["type"] = "string", ["minLength"] = 1 }),
                    },
                },
            };
            return schema;
        }

        private static JObject Shape(string name, JObject propertySchema) => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { [name] = propertySchema },
            ["required"] = new JArray { name },
        };
    }
}
