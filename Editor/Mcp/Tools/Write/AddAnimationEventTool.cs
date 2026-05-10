using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAnimationEventTool : IMcpTool
    {
        public string Name => "add_animation_event";
        public string Description =>
            "Appends an AnimationEvent to an AnimationClip. The event calls `functionName` on every MonoBehaviour of the GameObject playing the clip at `time` (seconds). Optional stringParameter / floatParameter / intParameter are passed to the receiver. Uses DontRequireReceiver — silent if no script defines the function.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clipGuidOrPath"] = new JObject { ["type"] = "string" },
                ["time"] = new JObject { ["type"] = "number" },
                ["functionName"] = new JObject { ["type"] = "string" },
                ["stringParameter"] = new JObject { ["type"] = "string" },
                ["floatParameter"] = new JObject { ["type"] = "number" },
                ["intParameter"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "clipGuidOrPath", "time", "functionName" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AnimationAuthor.AddAnimationEvent(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
