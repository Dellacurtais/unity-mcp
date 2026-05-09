using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ApplyColorGradingTool : IMcpTool
    {
        public string Name => "apply_color_grading";
        public string Description =>
            "Convenience: ensures a ColorAdjustments override exists on the Volume and sets the most common knobs (postExposure stops, contrast %, saturation %, hueShift degrees, colorFilter as color). Pass only the fields you want changed. Equivalent to add_post_effect+set_post_effect_property loop, but cheaper to call.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["volumeInstanceId"] = new JObject { ["type"] = "integer" },
                ["exposure"] = new JObject { ["type"] = "number", ["description"] = "Stops, e.g. 0.5 brightens." },
                ["contrast"] = new JObject { ["type"] = "number", ["description"] = "-100..100." },
                ["saturation"] = new JObject { ["type"] = "number", ["description"] = "-100..100." },
                ["hueShift"] = new JObject { ["type"] = "number", ["description"] = "-180..180 degrees." },
                ["colorFilter"] = new JObject { ["description"] = "Color tint multiplied over the image." },
            },
            ["required"] = new JArray { "volumeInstanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.ApplyColorGrading(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary);
        }
    }
}
