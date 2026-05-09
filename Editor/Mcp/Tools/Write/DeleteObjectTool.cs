using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class DeleteObjectTool : IMcpTool
    {
        public string Name => "delete_object";
        public string Description =>
            "Destroys a GameObject (and its children). Refuses by default if the GameObject has more than 8 children unless force=true. Registers a single Undo step.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["force"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return ToolResult.Error("NOT_FOUND", $"GameObject not found: instanceId={id}");

            int childCount = go.transform.childCount;
            bool force = (bool?)args["force"] ?? false;
            if (childCount > 8 && !force)
                return ToolResult.Error("REFUSED",
                    $"refusing to delete '{go.name}' with {childCount} children — pass force=true to override");

            var name = go.name;
            Undo.DestroyObjectImmediate(go);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok($"deleted '{name}' (id={id})");
        }
    }
}
