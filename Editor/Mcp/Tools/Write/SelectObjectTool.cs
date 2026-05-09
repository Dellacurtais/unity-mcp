using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SelectObjectTool : IMcpTool
    {
        public string Name => "select_object";
        public string Description =>
            "Selects a GameObject in the Hierarchy by instanceId. Optionally frames the SceneView on it. Idempotent and trivially undoable.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["frameInScene"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        // Selection isn't a destructive write but we mark it so it respects the
        // "Allow writes" toggle — clearer for the user.
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return ToolResult.Error("NOT_FOUND", $"GameObject with instanceId={id} not found");

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);

            if ((bool?)args["frameInScene"] == true && SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();

            return ToolResult.Ok($"selected '{go.name}' (id={id})");
        }
    }
}
