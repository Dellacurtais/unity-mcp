using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class InspectAudioSetupTool : IMcpTool
    {
        public string Name => "inspect_audio_setup";
        public string Description =>
            "Audit of the scene's audio: lists AudioListeners (warns when 0 or >1), counts AudioSources by clip presence / 2D vs 3D / loop+playOnAwake combinations / mixer group, and lists AudioReverbZones. Reports warnings (sources without clip, listener count) at the bottom.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.InspectAudioSetup(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
