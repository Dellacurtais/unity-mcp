using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimatorLayerTool : IMcpTool
    {
        public string Name => "add_animator_layer";
        public string Description => "Adds a unique AnimatorController layer with bounded weight, blending mode, optional AvatarMask and atomic Undo.";
        public JObject InputSchema => AnimationToolContracts.ControllerInputSchema(new JObject
        {
            ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            ["weight"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 1 },
            ["blendingMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Override", "Additive" }, ["default"] = "Override" },
            ["avatarMaskGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
        });
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.AddLayer(args));
    }
}
