using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ReadAudioMixerTool : IMcpTool
    {
        public string Name => "read_audio_mixer";
        public string Description =>
            "Returns the structure of an AudioMixer: groups (Master/Music/SFX/etc.), snapshots, and exposed parameters with their current values. Pass mixerGuidOrPath or mixerInstanceId.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["mixerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["mixerInstanceId"] = new JObject { ["type"] = "integer" },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.ReadMixer(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
