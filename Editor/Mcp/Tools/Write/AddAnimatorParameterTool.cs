using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimatorParameterTool : IMcpTool
    {
        public string Name => "add_animator_parameter";
        public string Description => "Adds a unique Float, Int, Bool or Trigger parameter with a type-compatible default and atomic Undo.";
        public JObject InputSchema => Schema();
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.AddParameter(args));

        private static JObject Schema()
        {
            var variants = new JArray();
            foreach (var reference in new[] { "controllerGuidOrPath", "controllerInstanceId" })
            {
                variants.Add(Variant(reference, null, new JObject { ["type"] = "number" }));
                variants.Add(Variant(reference, "Float", new JObject { ["type"] = "number" }));
                variants.Add(Variant(reference, "Int", new JObject { ["type"] = "integer" }));
                variants.Add(Variant(reference, "Bool", new JObject { ["type"] = "boolean" }));
                variants.Add(Variant(reference, "Trigger", null));
            }
            return new JObject { ["type"] = "object", ["oneOf"] = variants };
        }

        private static JObject Variant(string reference, string type, JObject defaultSchema)
        {
            var properties = new JObject
            {
                [reference] = reference == "controllerInstanceId"
                    ? new JObject { ["type"] = "integer" }
                    : new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            };
            var required = new JArray { reference, "name" };
            if (type != null)
            {
                properties["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray { type } };
                required.Add("type");
            }
            if (defaultSchema != null) properties["defaultValue"] = defaultSchema;
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }
    }
}
