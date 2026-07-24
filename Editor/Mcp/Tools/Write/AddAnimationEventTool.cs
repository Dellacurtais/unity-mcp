using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimationEventTool : IMcpTool
    {
        public string Name => "add_animation_event";
        public string Description => "Persistently adds a validated AnimationEvent to a standalone .anim asset under Assets/ using AnimationUtility.SetAnimationEvents and Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clipGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["time"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["functionName"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["stringParameter"] = new JObject { ["type"] = "string" },
                ["floatParameter"] = new JObject { ["type"] = "number" },
                ["intParameter"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "clipGuidOrPath", "time", "functionName" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args) => AnimationToolContracts.Result(AnimationAuthor.AddAnimationEvent(args));
    }
}
