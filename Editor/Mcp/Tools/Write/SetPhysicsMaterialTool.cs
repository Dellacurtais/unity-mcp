using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetPhysicsMaterialTool : IMcpTool
    {
        public string Name => "set_physics_material";
        public string Description =>
            "Assigns a PhysicsMaterial asset (friction/bounciness) to the Collider on a GameObject. " +
            "Use list_assets type=PhysicsMaterial to find available materials.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["materialGuidOrPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "instanceId", "materialGuidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.SetPhysicsMaterial(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
