using System;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.Localization
{
    [InitializeOnLoad]
    internal static class LocalizationBootstrap
    {
        static LocalizationBootstrap() { EditorApplication.delayCall += Initialize; }

        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("localization", "com.unity.localization",
                    new GetLocalizationContextTool(),
                    new ListLocalizationTablesTool(),
                    new CreateLocaleTool(),
                    new SetLocalizedStringTool(),
                    new ValidateLocalizationTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
