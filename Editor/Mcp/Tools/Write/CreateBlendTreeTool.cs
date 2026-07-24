using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateBlendTreeTool : IMcpTool
    {
        public string Name => "create_blend_tree";
        public string Description => "Creates a unique BlendTree state after validating layer, blend type and required Float controller parameters.";
        public JObject InputSchema => Schema();
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.CreateBlendTree(args));

        private static JObject Schema()
        {
            var variants = new JArray();
            foreach (var reference in new[] { "controllerGuidOrPath", "controllerInstanceId" })
            {
                variants.Add(Variant(reference, null, true, false));
                variants.Add(Variant(reference, "Simple1D", true, false));
                variants.Add(Variant(reference, "SimpleDirectional2D", true, true));
                variants.Add(Variant(reference, "FreeformDirectional2D", true, true));
                variants.Add(Variant(reference, "FreeformCartesian2D", true, true));
                variants.Add(Variant(reference, "Direct", false, false));
            }
            return new JObject { ["type"] = "object", ["oneOf"] = variants };
        }

        private static JObject Variant(string reference, string blendType, bool requiresX, bool requiresY)
        {
            var properties = new JObject
            {
                [reference] = reference == "controllerInstanceId"
                    ? new JObject { ["type"] = "integer" }
                    : new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["state"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["layer"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
            };
            var required = new JArray { reference, "state" };
            if (blendType != null)
            {
                properties["blendType"] = new JObject { ["type"] = "string", ["enum"] = new JArray { blendType } };
                required.Add("blendType");
            }
            if (requiresX)
            {
                properties["blendParameter"] = new JObject { ["type"] = "string", ["minLength"] = 1 };
                required.Add("blendParameter");
            }
            if (requiresY)
            {
                properties["blendParameterY"] = new JObject { ["type"] = "string", ["minLength"] = 1 };
                required.Add("blendParameterY");
            }
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
