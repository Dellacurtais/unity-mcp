using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddBlendTreeMotionTool : IMcpTool
    {
        public string Name => "add_blend_tree_motion";
        public string Description =>
            "Adds a motion (AnimationClip) to a state's BlendTree. For 1D trees pass `threshold` (the parameter value at which this motion plays). For 2D trees pass `position: [x, y]`. State must already be a blend tree (via create_blend_tree).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["controllerInstanceId"] = new JObject { ["type"] = "integer" },
                ["state"] = new JObject { ["type"] = "string" },
                ["layer"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["clipGuidOrPath"] = new JObject { ["type"] = "string" },
                ["threshold"] = new JObject { ["type"] = "number" },
                ["position"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2 },
            },
            ["required"] = new JArray { "state", "clipGuidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.AddBlendTreeMotion(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
