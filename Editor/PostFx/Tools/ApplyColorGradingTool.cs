using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ApplyColorGradingTool : IMcpTool
    {
        public string Name => "apply_color_grading";
        public string Description =>
            "Ensures a pipeline-compatible ColorAdjustments override exists and atomically applies one or more common parameters. Validates all values before creating or mutating the profile.";

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
                ["colorFilter"] = SchemaHelpers.ColorSchema,
            },
            ["required"] = new JArray { "volumeInstanceId" },
            ["minProperties"] = 2,
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.ApplyColorGrading(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data);
        }
    }
}
