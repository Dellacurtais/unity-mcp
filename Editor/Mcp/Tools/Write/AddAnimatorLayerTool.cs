using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimatorLayerTool : IMcpTool
    {
        public string Name => "add_animator_layer";
        public string Description =>
            "Adds a layer to an AnimatorController. weight (0..1) is the default layer weight. blendingMode: Override or Additive. avatarMaskGuidOrPath optionally restricts the layer to specific bones.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["controllerInstanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["weight"] = new JObject { ["type"] = "number", ["default"] = 1 },
                ["blendingMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Override", "Additive" } },
                ["avatarMaskGuidOrPath"] = new JObject { ["type"] = "string" },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.AddLayer(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
