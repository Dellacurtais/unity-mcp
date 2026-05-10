using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetAudioSourcePropsTool : IMcpTool
    {
        public string Name => "set_audio_source_props";
        public string Description =>
            "Typed setter for every common AudioSource field — pass only the keys you want changed. " +
            "Covers: volume, pitch, spatialBlend (0=2D, 1=3D), minDistance, maxDistance, dopplerLevel, spread, panStereo, " +
            "reverbZoneMix, priority (0=highest), mute, loop, playOnAwake, bypassEffects/bypassListenerEffects/bypassReverbZones, " +
            "rolloffMode (Logarithmic/Linear/Custom), clip (guid/path).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["volume"] = new JObject { ["type"] = "number" },
                ["pitch"] = new JObject { ["type"] = "number" },
                ["spatialBlend"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["minDistance"] = new JObject { ["type"] = "number" },
                ["maxDistance"] = new JObject { ["type"] = "number" },
                ["dopplerLevel"] = new JObject { ["type"] = "number" },
                ["spread"] = new JObject { ["type"] = "number" },
                ["panStereo"] = new JObject { ["type"] = "number", ["minimum"] = -1, ["maximum"] = 1 },
                ["reverbZoneMix"] = new JObject { ["type"] = "number" },
                ["priority"] = new JObject { ["type"] = "integer" },
                ["mute"] = new JObject { ["type"] = "boolean" },
                ["loop"] = new JObject { ["type"] = "boolean" },
                ["playOnAwake"] = new JObject { ["type"] = "boolean" },
                ["bypassEffects"] = new JObject { ["type"] = "boolean" },
                ["bypassListenerEffects"] = new JObject { ["type"] = "boolean" },
                ["bypassReverbZones"] = new JObject { ["type"] = "boolean" },
                ["rolloffMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Logarithmic", "Linear", "Custom" } },
                ["clip"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.SetSourceProps(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary);
        }
    }
}
