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
                if (Bootstrap.ToolRegistry == null) return;

                Bootstrap.ToolRegistry.Register(new BridgeStatusTool());
                Bootstrap.ToolRegistry.Register(new BridgeInstallTool());
                Bootstrap.ToolRegistry.Register(new ListBridgeHandlersTool());
                Bootstrap.ToolRegistry.Register(new SendRuntimeCommandTool());
                Bootstrap.ToolRegistry.Register(new SendRuntimeCommandsTool());

                Bootstrap.RecordEntry("[bridge] registered 5 runtime-command tools");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
