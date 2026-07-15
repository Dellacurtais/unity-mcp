using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Bridge.Tools
{
    public sealed class BridgeInstallTool : IMcpTool
    {
        public string Name => "bridge_install";
        public string Description =>
            "Adds the McpCommandBridge MonoBehaviour to the active scene (a single GameObject named '__McpCommandBridge'). " +
            "Idempotent — returns the existing instance if one is already present. " +
            "Sets DontDestroyOnLoad by default so the bridge survives runtime scene loads. " +
            "Marks the scene dirty and registers the new object with Undo so Ctrl+Z removes it.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["markDontDestroyOnLoad"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            bool ddol = (bool?)args["markDontDestroyOnLoad"] ?? true;

            // Idempotent check first.
            if (McpCommandBridge.Exists)
            {
                var existing = McpCommandBridge.Instance.gameObject;
                return ToolResult.Ok(
                    $"bridge already installed on '{existing.name}' (instanceId {existing.GetInstanceID()})",
                    new JObject
                    {
                        ["installed"] = true,
                        ["alreadyExisted"] = true,
                        ["sceneObjectInstanceId"] = existing.GetInstanceID(),
                    });
            }

            var go = new GameObject("__McpCommandBridge");
            // Visible in the hierarchy by default so devs can see the bridge
            // exists. They can set HideFlags manually if they want it hidden.
            var bridge = go.AddComponent<McpCommandBridge>();
            bridge.dontDestroyOnLoad = ddol;

            Undo.RegisterCreatedObjectUndo(go, "Install McpCommandBridge");

            return ToolResult.Ok(
                $"bridge installed on '{go.name}' (instanceId {go.GetInstanceID()})",
                new JObject
                {
                    ["installed"] = true,
                    ["alreadyExisted"] = false,
                    ["sceneObjectInstanceId"] = go.GetInstanceID(),
                    ["dontDestroyOnLoad"] = ddol,
                });
        }
    }
}
