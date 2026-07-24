using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateAudioSourceTool : IMcpTool
    {
        public string Name => "create_audio_source";
        public string Description =>
            "Creates a GameObject with an AudioSource. Pass an AudioClip GUID or Assets path via clipGuidOrPath (legacy clipGuid/clip are accepted). " +
            "Validates all references before creating anything; spatial=true sets spatialBlend to 1 (3D).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string" },
                ["clipGuidOrPath"] = new JObject { ["type"] = "string" },
                ["clipGuid"] = new JObject { ["type"] = "string" },
                ["clip"] = new JObject { ["type"] = "string" },
                ["loop"] = new JObject { ["type"] = "boolean" },
                ["volume"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["pitch"] = new JObject { ["type"] = "number", ["minimum"] = -3, ["maximum"] = 3 },
                ["spatial"] = new JObject { ["type"] = "boolean" },
                ["playOnAwake"] = new JObject { ["type"] = "boolean" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.StrictVec3Schema,
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = AudioSourceFactory.Create(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data ?? new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
