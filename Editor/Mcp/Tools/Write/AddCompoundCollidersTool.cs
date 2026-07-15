using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddCompoundCollidersTool : IMcpTool
    {
        public string Name => "add_compound_colliders";
        public string Description =>
            "Adds ONE primitive collider per child renderer — gives you a compound collider that approximates a complex mesh way better than a single box around the whole bounds, while staying cheap (no convex hull / VHACD). " +
            "kind: box (default) / sphere / capsule / mesh (convex). replaceExisting=false (default) keeps existing colliders. Useful for buildings, vehicles, multi-part models.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule", "mesh" }, ["default"] = "box" },
                ["isTrigger"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["replaceExisting"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.AddCompoundColliders(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
