using Maicon.SceneAnalyzer.Editor.Bridge.Tools;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Bridge
{
    /// Defer registration to delayCall so Bootstrap.ToolRegistry is populated
    /// before we add the bridge tools (same pattern as PostFxBootstrap and
    /// PlayBootstrap).
    [InitializeOnLoad]
    internal static class BridgeBootstrap
    {
        static BridgeBootstrap()
        {
            EditorApplication.delayCall += Init;
        }

        private static void Init()
        {
            try
            {
                if (Bootstrap.ToolRegistry == null || Bootstrap.ToolRegistry.TryGet("bridge_status", out _)) return;

                Bootstrap.ToolRegistry.RegisterRange(new Maicon.SceneAnalyzer.Editor.Mcp.IMcpTool[]
                {
                    new BridgeStatusTool(),
                    new BridgeInstallTool(),
                    new ListBridgeHandlersTool(),
                    new SendRuntimeCommandTool(),
                    new SendRuntimeCommandsTool(),
                });

                Bootstrap.RecordEntry("[bridge] registered 5 runtime-command tools");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
