using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateBlendTreeTool : IMcpTool
    {
        public string Name => "create_blend_tree";
        public string Description =>
            "Creates a state in the controller backed by a BlendTree. blendType: Simple1D, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, Direct. blendParameter (and blendParameterY for 2D) reference parameters already added to the controller. Follow with add_blend_tree_motion to populate.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["controllerInstanceId"] = new JObject { ["type"] = "integer" },
                ["state"] = new JObject { ["type"] = "string" },
                ["layer"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["blendType"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Simple1D", "SimpleDirectional2D", "FreeformDirectional2D", "FreeformCartesian2D", "Direct" }, ["default"] = "Simple1D" },
                ["blendParameter"] = new JObject { ["type"] = "string" },
                ["blendParameterY"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "state" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.CreateBlendTree(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
