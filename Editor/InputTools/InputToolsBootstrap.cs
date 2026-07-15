using System;
using Maicon.SceneAnalyzer.Editor.Optional;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.InputTools
{
    [InitializeOnLoad]
    internal static class InputToolsBootstrap
    {
        static InputToolsBootstrap() { EditorApplication.delayCall += Initialize; }

        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("input-system", "com.unity.inputsystem",
                    new GetInputSystemContextTool(),
                    new ReadInputActionAssetTool(),
                    new CreateInputActionAssetTool(),
                    new AddInputActionMapTool(),
                    new AddInputActionTool(),
                    new AddInputBindingTool(),
                    new AddInputControlSchemeTool(),
                    new ConfigurePlayerInputTool(),
                    new ValidateInputSetupTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
