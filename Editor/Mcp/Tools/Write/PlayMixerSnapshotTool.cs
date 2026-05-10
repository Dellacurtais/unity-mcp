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
                ["transitionSeconds"] = new JObject { ["type"] = "number", ["default"] = 0.5 },
            },
            ["required"] = new JArray { "snapshot" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.PlayMixerSnapshot(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
