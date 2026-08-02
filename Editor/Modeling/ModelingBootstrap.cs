using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Modeling.Mcp;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling
{
    [InitializeOnLoad]
    internal static class ModelingBootstrap
    {
        static ModelingBootstrap()
        {
            EditorApplication.delayCall += Initialize;
        }

        private static void Initialize()
        {
            try
            {
                if (Bootstrap.ToolRegistry == null
                    || Bootstrap.ToolRegistry.TryGet("mesh_analyze_body_parts", out _)) return;
                Bootstrap.ToolRegistry.RegisterRange(new IMcpTool[]
                {
                    new AnalyzeBodyPartsTool(),
                    new PreviewSmartBodySplitTool(),
                    new ApplySmartBodySplitTool(),
                    new ValidateSmartBodySplitTool(),
                });
                Bootstrap.RecordEntry("[asset-modeler] registered 4 Smart Body Split tools");
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }
}
