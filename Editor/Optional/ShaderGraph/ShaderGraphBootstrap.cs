using System;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.ShaderGraph
{
    [InitializeOnLoad]
    internal static class ShaderGraphBootstrap
    {
        static ShaderGraphBootstrap() { EditorApplication.delayCall += Initialize; }

        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("shadergraph", "com.unity.shadergraph",
                    new GetShaderGraphContextTool(),
                    new ListShaderGraphAssetsTool(),
                    new ReadShaderGraphSourceTool(),
                    new ValidateShaderGraphAssetsTool(),
                    new WriteShaderGraphSourceTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
