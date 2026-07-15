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
            "Creates a GameObject with an AudioSource component. Pass an AudioClip via clipGuid (preferred) or path. spatial=true sets spatialBlend to 1.0 (3D); false stays 2D.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string" },
                ["clipGuid"] = new JObject { ["type"] = "string" },
                ["loop"] = new JObject { ["type"] = "boolean" },
                ["volume"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["pitch"] = new JObject { ["type"] = "number" },
                ["spatial"] = new JObject { ["type"] = "boolean" },
                ["playOnAwake"] = new JObject { ["type"] = "boolean" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = AudioSourceFactory.Create(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
