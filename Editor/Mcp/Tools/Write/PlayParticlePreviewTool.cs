using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class PlayParticlePreviewTool : IMcpTool
    {
        public string Name => "play_particle_preview";
        public string Description =>
            "Drives the editor preview of a ParticleSystem without entering Play Mode. action: " +
            "`play` (start), `stop` (stop emission, let live particles die), `pause`, `clear` (kill all live particles), " +
            "`restart` (stop+clear+play), `simulate` (advance to time=N seconds and freeze). " +
            "withChildren=true (default) cascades to child ParticleSystems. Useful to visually verify a freshly-created emitter.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "play", "stop", "pause", "clear", "restart", "simulate" }, ["default"] = "play" },
                ["time"] = new JObject { ["type"] = "number", ["description"] = "Seconds to advance for `simulate`. Ignored otherwise." },
                ["withChildren"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        // Preview drives runtime state — mark as write so the AllowWrites toggle gates it.
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = ParticleEditor.PlayPreview(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
