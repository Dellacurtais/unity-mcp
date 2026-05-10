using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ListAudioMixersTool : IMcpTool
    {
        public string Name => "list_audio_mixers";
        public string Description =>
            "Lists `.mixer` (AudioMixer) assets in the project. Filter by name substring or folder.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["nameContains"] = new JObject { ["type"] = "string" },
                ["folder"] = new JObject { ["type"] = "string" },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 100 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.ListMixers(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
