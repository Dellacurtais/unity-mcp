using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class AlternateAuditRenderPipelineAsset : RenderPipelineAsset
    {
        protected override RenderPipeline CreatePipeline()
        {
            return new Pipeline();
        }

        private sealed class Pipeline : RenderPipeline
        {
            protected override void Render(ScriptableRenderContext context, List<Camera> cameras)
            {
            }
        }
    }
}
