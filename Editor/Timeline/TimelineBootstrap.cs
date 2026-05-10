using System;
using Maicon.SceneAnalyzer.Editor.Authoring;
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
                if (Bootstrap.ToolRegistry == null) return;

                ChangesetExecutor.RegisterHandler("create_timeline_asset", TimelineAuthor.CreateTimeline);
                ChangesetExecutor.RegisterHandler("add_timeline_track", TimelineAuthor.AddTrack);
                ChangesetExecutor.RegisterHandler("add_timeline_clip", TimelineAuthor.AddClip);
                ChangesetExecutor.RegisterHandler("assign_timeline_to_director", TimelineAuthor.AssignToDirector);
                ChangesetExecutor.RegisterHandler("play_timeline", TimelineAuthor.Play);

                Bootstrap.ToolRegistry.Register(new CreateTimelineAssetTool());
                Bootstrap.ToolRegistry.Register(new AddTimelineTrackTool());
                Bootstrap.ToolRegistry.Register(new AddTimelineClipTool());
                Bootstrap.ToolRegistry.Register(new AssignTimelineToDirectorTool());
                Bootstrap.ToolRegistry.Register(new PlayTimelineTool());

                Bootstrap.RecordEntry("[timeline] registered 5 timeline tools (com.unity.timeline present)");
            }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
