using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ParentObjectTool : IMcpTool
    {
        public string Name => "parent_object";
        public string Description =>
            "Reparents a GameObject. Pass newParentId=null (or omit) to move it to the scene root. worldPositionStays=true keeps the object in the same world position; false snaps it to the parent's local origin.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["newParentId"] = new JObject {
                    ["type"] = "integer",
                    ["description"] = "Parent GameObject instanceId. Omit (or pass 0) to move to the scene root.",
                },
                ["worldPositionStays"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = TransformEditor.Parent(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary);
        }
    }
}
