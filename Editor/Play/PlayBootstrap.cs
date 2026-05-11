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
                if (Bootstrap.ToolRegistry == null) return;

                // Phase A — play-mode lifecycle
                Bootstrap.ToolRegistry.Register(new GetPlayStateTool());
                Bootstrap.ToolRegistry.Register(new EnterPlayModeTool());
                Bootstrap.ToolRegistry.Register(new ExitPlayModeTool());
                Bootstrap.ToolRegistry.Register(new SetPlayPausedTool());
                Bootstrap.ToolRegistry.Register(new StepFrameTool());
                Bootstrap.ToolRegistry.Register(new SetTimeScaleTool());

                // Phase B — game-view capture & runtime monitoring
                Bootstrap.ToolRegistry.Register(new ScreenshotGameViewTool());
                Bootstrap.ToolRegistry.Register(new ScreenshotFromPovTool());
                Bootstrap.ToolRegistry.Register(new GetActiveCamerasTool());
                Bootstrap.ToolRegistry.Register(new ReadRuntimeLogDeltaTool());
                Bootstrap.ToolRegistry.Register(new MarkLogAnchorTool());

                Bootstrap.RecordEntry("[play] registered 11 play-mode + capture tools");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
