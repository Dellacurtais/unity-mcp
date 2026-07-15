using System;
using Maicon.SceneAnalyzer.Editor.Optional;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.TestTools
{
    [InitializeOnLoad]
    internal static class TestToolsBootstrap
    {
        static TestToolsBootstrap() { EditorApplication.delayCall += Initialize; }

        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("test-framework", "com.unity.test-framework",
                    new ListTestsTool(),
                    new RunTestsTool(),
                    new GetTestJobTool(),
                    new CancelTestJobTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
