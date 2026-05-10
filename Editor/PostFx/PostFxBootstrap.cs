using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
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
                if (Bootstrap.ToolRegistry == null) return;

                // Op kinds (used by apply_changeset).
                ChangesetExecutor.RegisterHandler("create_volume", VolumeFactory.Create);
                ChangesetExecutor.RegisterHandler("add_post_effect", PostEffectEditor.AddEffect);
                ChangesetExecutor.RegisterHandler("remove_post_effect", PostEffectEditor.RemoveEffect);
                ChangesetExecutor.RegisterHandler("set_post_effect_property", PostEffectEditor.SetEffectProperty);
                ChangesetExecutor.RegisterHandler("apply_color_grading", PostEffectEditor.ApplyColorGrading);

                // MCP tools.
                Bootstrap.ToolRegistry.Register(new CreateVolumeTool());
                Bootstrap.ToolRegistry.Register(new AddPostEffectTool());
                Bootstrap.ToolRegistry.Register(new RemovePostEffectTool());
                Bootstrap.ToolRegistry.Register(new SetPostEffectPropertyTool());
                Bootstrap.ToolRegistry.Register(new ApplyColorGradingTool());
                Bootstrap.ToolRegistry.Register(new ListPostEffectsTool());
                Bootstrap.ToolRegistry.Register(new DescribePostEffectTool());

                Bootstrap.RecordEntry("[postfx] registered 7 volume/post-fx tools (SRP Core present)");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
