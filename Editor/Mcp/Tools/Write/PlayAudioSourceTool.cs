using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class PlayAudioSourceTool : IMcpTool
    {
        public string Name => "play_audio_source";
        public string Description =>
            "Drives an AudioSource: action: play / stop / pause / unpause (or resume) / playOneShot. " +
            "playOneShot also takes `clip` (guid/path) and optional `volumeScale` (default 1). " +
            "Note: AudioSource only emits in Play Mode — calling Play() in edit mode is silent (the call still runs, no audio is rendered).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "play", "stop", "pause", "unpause", "resume", "playOneShot" }, ["default"] = "play" },
                ["clip"] = new JObject { ["type"] = "string" },
                ["volumeScale"] = new JObject { ["type"] = "number", ["default"] = 1 },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.PlayAudio(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
