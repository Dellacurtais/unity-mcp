using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetMixerParameterTool : IMcpTool
    {
        public string Name => "set_mixer_parameter";
        public string Description =>
            "Sets an exposed mixer parameter at runtime via AudioMixer.SetFloat. Useful for ducking, dynamic EQ, fade in/out. " +
            "LIMITATION: parameter must already be exposed — right-click the parameter in the AudioMixer window → 'Expose [name] to script'. Use read_audio_mixer to see what's currently exposed.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["mixerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["mixerInstanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["value"] = new JObject { ["type"] = "number" },
            },
            ["required"] = new JArray { "name", "value" },
            ["oneOf"] = new JArray
            {
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject { ["mixerGuidOrPath"] = new JObject { ["type"] = "string" } },
                    ["required"] = new JArray { "mixerGuidOrPath" },
                },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject { ["mixerInstanceId"] = new JObject { ["type"] = "integer" } },
                    ["required"] = new JArray { "mixerInstanceId" },
                },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "audio",
            Capabilities = McpCapability.RuntimeCommand,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.PlayModeOnly,
            IsIdempotent = true,
        };
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.SetMixerParameter(args);
            if (r.Ok) return ToolResult.Ok(r.Summary, r.Data);
            return r.Error != null && r.Error.StartsWith("PLAY_MODE_REQUIRED:")
                ? ToolResult.Error("PLAY_MODE_REQUIRED", r.Error.Substring("PLAY_MODE_REQUIRED:".Length).Trim())
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
