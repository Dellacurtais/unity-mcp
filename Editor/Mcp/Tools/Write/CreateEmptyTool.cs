using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateEmptyTool : IMcpTool
    {
        public string Name => "create_empty";
        public string Description =>
            "Creates an empty GameObject (no components beyond Transform). Useful for grouping or for adding components afterwards via add_component.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "GameObject" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["rotation"] = SchemaHelpers.RotSchema,
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var name = (string)args["name"] ?? "GameObject";
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Empty");
            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");
            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            go.transform.localRotation = JsonHelpers.ParseRotation(args["rotation"]);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok($"created empty '{name}'", new JObject { ["instanceId"] = go.GetInstanceID() });
        }
    }
}
