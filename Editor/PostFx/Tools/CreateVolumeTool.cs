using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateVolumeTool : IMcpTool
    {
        public string Name => "create_volume";
        public string Description =>
            "Creates a Volume GameObject for SRP post-processing (URP/HDRP). Also creates and assigns a VolumeProfile asset on disk so the override survives reload. Use add_post_effect / set_post_effect_property next to populate it. Returns the volume's instanceId.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "Global Volume" },
                ["isGlobal"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["priority"] = new JObject { ["type"] = "number", ["default"] = 0.0 },
                ["weight"] = new JObject { ["type"] = "number", ["default"] = 1.0 },
                ["blendDistance"] = new JObject { ["type"] = "number", ["default"] = 0.0 },
                ["profilePath"] = new JObject { ["type"] = "string", ["description"] = "Optional asset path under Assets/. Default: Assets/Settings/<name>_Profile.asset" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = VolumeFactory.Create(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
