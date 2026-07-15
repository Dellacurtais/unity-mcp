using Maicon.SceneAnalyzer.Editor.Play.Tools;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play
{
    /// Side-loaded init for play-mode tools. Lives in its own asmdef so the
    /// folder can be deleted to disable all play-mode tooling without touching
    /// the main plugin. Defers registration to delayCall so the main
    /// Bootstrap's static ctor has finished populating ToolRegistry first
    /// (same pattern as PostFxBootstrap).
    [InitializeOnLoad]
    internal static class PlayBootstrap
    {
        static PlayBootstrap()
        {
            EditorApplication.delayCall += Init;
        }

        private static void Init()
        {
            try
            {
                if (Bootstrap.ToolRegistry == null || Bootstrap.ToolRegistry.TryGet("get_play_state", out _)) return;

                Bootstrap.ToolRegistry.RegisterRange(new Maicon.SceneAnalyzer.Editor.Mcp.IMcpTool[]
                {
                    new GetPlayStateTool(),
                    new EnterPlayModeTool(),
                    new ExitPlayModeTool(),
                    new SetPlayPausedTool(),
                    new StepFrameTool(),
                    new SetTimeScaleTool(),
                    new ScreenshotGameViewTool(),
                    new ScreenshotFromPovTool(),
                    new GetActiveCamerasTool(),
                    new ReadRuntimeLogDeltaTool(),
                    new MarkLogAnchorTool(),
                });

                Bootstrap.RecordEntry("[play] registered 11 play-mode + capture tools");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
