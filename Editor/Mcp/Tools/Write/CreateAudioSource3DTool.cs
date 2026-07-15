using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateAudioSource3DTool : IMcpTool
    {
        public string Name => "create_audio_source_3d";
        public string Description =>
            "Convenience for a 3D point source (engine box, generator humming, fountain, etc.). " +
            "If `instanceId` is given, attaches/configures an AudioSource on that GO; otherwise creates a new GameObject. " +
            "Defaults: spatialBlend=1 (full 3D), loop=true, playOnAwake=true. " +
            "Crucial 3D fields: minDistance (full volume inside this radius — default 1), maxDistance (silent beyond this — default 50), " +
            "rolloffMode (Logarithmic/Linear/Custom — default Logarithmic), dopplerLevel (default 1), spread (0-360°). " +
            "mixerGroup routes the source (e.g. 'SFX' or 'MainMixer/Ambient').";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["clipGuidOrPath"] = new JObject { ["type"] = "string" },
                ["loop"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["playOnAwake"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["volume"] = new JObject { ["type"] = "number", ["default"] = 1.0 },
                ["pitch"] = new JObject { ["type"] = "number", ["default"] = 1.0 },
                ["minDistance"] = new JObject { ["type"] = "number", ["default"] = 1.0 },
                ["maxDistance"] = new JObject { ["type"] = "number", ["default"] = 50.0 },
                ["rolloffMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Logarithmic", "Linear", "Custom" } },
                ["dopplerLevel"] = new JObject { ["type"] = "number", ["default"] = 1.0 },
                ["spread"] = new JObject { ["type"] = "number" },
                ["priority"] = new JObject { ["type"] = "integer", ["default"] = 128 },
                ["reverbZoneMix"] = new JObject { ["type"] = "number", ["default"] = 1.0 },
                ["mixerGroup"] = new JObject { ["type"] = "string" },
                ["mixerGuidOrPath"] = new JObject { ["type"] = "string" },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.Create3DSource(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
