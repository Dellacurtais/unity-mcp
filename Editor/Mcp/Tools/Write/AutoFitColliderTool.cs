using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AutoFitColliderTool : IMcpTool
    {
        public string Name => "auto_fit_collider";
        public string Description =>
            "Adds ONE collider to the GameObject sized to encapsulate its renderers' bounds. " +
            "kind: box (default) / sphere / capsule / mesh. " +
            "useChildren=true (default) encapsulates child renderers' bounds too. " +
            "replaceExisting=true (default) removes existing colliders (except CharacterController) before adding. " +
            "isTrigger flags the new collider as a trigger volume. " +
            "For mesh kind: pass convex=true if the GameObject also has a Rigidbody.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule", "mesh" }, ["default"] = "box" },
                ["useChildren"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["replaceExisting"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["isTrigger"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["convex"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.AutoFitCollider(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary);
        }
    }
}
