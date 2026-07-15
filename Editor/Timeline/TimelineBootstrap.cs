using System;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Optional;
using Maicon.SceneAnalyzer.Editor.Timeline.Tools;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Timeline
{
    /// Side-loaded init for Timeline-dependent tools. Lives in a secondary
    /// asmdef gated by SCENE_ANALYZER_HAS_TIMELINE so the main plugin builds
    /// even on projects without `com.unity.timeline`. When the package is
    /// present, this registers the 5 timeline tools and their changeset op
    /// kinds with the main runtime — defers to delayCall to guarantee main
    /// Bootstrap finished first.
    [InitializeOnLoad]
    internal static class TimelineBootstrap
    {
        static TimelineBootstrap()
        {
            EditorApplication.delayCall += Init;
        }

        private static void Init()
        {
            try
            {
                if (Bootstrap.ToolRegistry == null || OptionalModuleRegistry.Contains("timeline")) return;

                OptionalToolSupport.RegisterModule("timeline", "com.unity.timeline",
                    new CreateTimelineAssetTool(),
                    new AddTimelineTrackTool(),
                    new AddTimelineClipTool(),
                    new AssignTimelineToDirectorTool(),
                    new PlayTimelineTool());

                ChangesetExecutor.RegisterHandler("create_timeline_asset", TimelineAuthor.CreateTimeline);
                ChangesetExecutor.RegisterHandler("add_timeline_track", TimelineAuthor.AddTrack);
                ChangesetExecutor.RegisterHandler("add_timeline_clip", TimelineAuthor.AddClip);
                ChangesetExecutor.RegisterHandler("assign_timeline_to_director", TimelineAuthor.AssignToDirector);
                ChangesetExecutor.RegisterHandler("play_timeline", TimelineAuthor.Play);
            }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
