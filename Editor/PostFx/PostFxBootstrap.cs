using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using Maicon.SceneAnalyzer.Editor.Optional;
using Maicon.SceneAnalyzer.Editor.PostFx.Tools;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.PostFx
{
    /// Side-loaded init for SRP-Core-dependent tools. Lives in a secondary asmdef
    /// gated by SCENE_ANALYZER_HAS_SRP_CORE so the main plugin builds even on
    /// projects without URP/HDRP. When SRP Core is present, this registers the
    /// 4 volume / post-effect tools and their changeset op kinds with the main
    /// runtime — defers to delayCall to guarantee main Bootstrap finished first.
    [InitializeOnLoad]
    internal static class PostFxBootstrap
    {
        static PostFxBootstrap()
        {
            EditorApplication.delayCall += Init;
        }

        private static void Init()
        {
            try
            {
                if (Bootstrap.ToolRegistry == null || OptionalModuleRegistry.Contains("postfx")) return;

                OptionalToolSupport.RegisterModule("postfx", "com.unity.render-pipelines.core",
                    new CreateVolumeTool(),
                    new AddPostEffectTool(),
                    new RemovePostEffectTool(),
                    new SetPostEffectPropertyTool(),
                    new ApplyColorGradingTool(),
                    new ListPostEffectsTool(),
                    new DescribePostEffectTool());

                ChangesetExecutor.RegisterHandler("create_volume", VolumeFactory.Create);
                ChangesetExecutor.RegisterHandler("add_post_effect", PostEffectEditor.AddEffect);
                ChangesetExecutor.RegisterHandler("remove_post_effect", PostEffectEditor.RemoveEffect);
                ChangesetExecutor.RegisterHandler("set_post_effect_property", PostEffectEditor.SetEffectProperty);
                ChangesetExecutor.RegisterHandler("apply_color_grading", PostEffectEditor.ApplyColorGrading);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
