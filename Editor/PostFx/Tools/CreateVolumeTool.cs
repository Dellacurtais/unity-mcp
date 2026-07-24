using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateVolumeTool : IMcpTool
    {
        public string Name => "create_volume";
        public string Description =>
            "Creates a global or local SRP Volume with a persistent shared VolumeProfile. Local volumes receive a trigger BoxCollider so they affect the scene. Returns stable profile path/GUID and session instance IDs.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["default"] = "Global Volume" },
                ["isGlobal"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["priority"] = new JObject { ["type"] = "number", ["default"] = 0.0 },
                ["weight"] = new JObject { ["type"] = "number", ["minimum"] = 0.0, ["maximum"] = 1.0, ["default"] = 1.0 },
                ["blendDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0.0, ["default"] = 0.0 },
                ["profilePath"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets/.+\\.asset$", ["description"] = "Optional .asset path under Assets/. Default: Assets/Settings/<name>_Profile.asset. Existing VolumeProfiles are reused." },
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
            return ToolResult.Ok(r.Summary, r.Data);
        }
    }
}
