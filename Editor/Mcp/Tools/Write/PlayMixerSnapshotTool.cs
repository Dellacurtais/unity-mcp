using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class PlayMixerSnapshotTool : IMcpTool
    {
        public string Name => "play_mixer_snapshot";
        public string Description =>
            "Transitions an AudioMixer to a named snapshot over `transitionSeconds`. Snapshots store full mixer state (group volumes, FX wet/dry, etc.) — use to swap moods (combat → peaceful, normal → muffled-by-water).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["mixerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["mixerInstanceId"] = new JObject { ["type"] = "integer" },
                ["snapshot"] = new JObject { ["type"] = "string" },
                ["transitionSeconds"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 0.5 },
            },
            ["required"] = new JArray { "snapshot" },
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
            IsIdempotent = false,
        };
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.PlayMixerSnapshot(args);
            if (r.Ok) return ToolResult.Ok(r.Summary, r.Data);
            return r.Error != null && r.Error.StartsWith("PLAY_MODE_REQUIRED:")
                ? ToolResult.Error("PLAY_MODE_REQUIRED", r.Error.Substring("PLAY_MODE_REQUIRED:".Length).Trim())
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
