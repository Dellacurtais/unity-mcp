using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Maicon.SceneAnalyzer.Editor.Authoring;
using UnityEngine;

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
        public JObject OutputSchema => BridgeToolSchemas.Install;

        public ToolResult Execute(JObject args)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("EDIT_MODE_REQUIRED", "install the bridge in Edit Mode so it exists before runtime Awake/Start handlers register");
            bool ddol = (bool?)args["markDontDestroyOnLoad"] ?? true;
            var existingBridges = UnityObjectIds.FindAll<McpCommandBridge>(FindObjectsInactive.Include);
            if (existingBridges.Length > 1)
                return ToolResult.Error("BRIDGE_DUPLICATES", $"found {existingBridges.Length} McpCommandBridge components; keep exactly one");
            if (existingBridges.Length == 1)
            {
                var existing = existingBridges[0];
                return ToolResult.Ok(
                    $"bridge already installed on '{existing.name}' (instanceId {existing.gameObject.GetMcpInstanceId()})",
                    new JObject
                    {
                        ["installed"] = true,
                        ["alreadyExisted"] = true,
                        ["sceneObjectInstanceId"] = existing.gameObject.GetMcpInstanceId(),
                        ["dontDestroyOnLoad"] = existing.dontDestroyOnLoad,
                    });
            }

            GameObject go = null;
            try
            {
                using (var mutation = new MutationScope("Install McpCommandBridge"))
                {
                    go = new GameObject("__McpCommandBridge");
                    mutation.RegisterCreated(go);
                    var bridge = mutation.AddComponent<McpCommandBridge>(go);
                    bridge.dontDestroyOnLoad = ddol;
                    mutation.Touch(bridge);
                    mutation.Complete();
                }
                return ToolResult.Ok(
                    $"bridge installed on '{go.name}' (instanceId {go.GetMcpInstanceId()})",
                    new JObject
                    {
                        ["installed"] = true,
                        ["alreadyExisted"] = false,
                        ["sceneObjectInstanceId"] = go.GetMcpInstanceId(),
                        ["dontDestroyOnLoad"] = ddol,
                    });
            }
            catch (System.Exception exception)
            {
                if (go != null) Object.DestroyImmediate(go);
                return ToolResult.Error("BRIDGE_INSTALL_FAILED", exception.Message);
            }
        }
    }
}
