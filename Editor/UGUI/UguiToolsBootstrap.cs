using System;
using Maicon.SceneAnalyzer.Editor.Optional;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.UGUI
{
    [InitializeOnLoad]
    internal static class UguiToolsBootstrap
    {
        static UguiToolsBootstrap() { EditorApplication.delayCall += Initialize; }

        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("ugui", "com.unity.ugui",
                    new ListUguiElementsTool(),
                    new CreateUguiCanvasTool(),
                    new CreateUguiElementTool(),
                    new SetRectTransformTool(),
                    new ConfigureUguiLayoutTool(),
                    new InspectUguiEventsTool(),
                    new BindButtonClickTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
