using System;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.Addressables
{
    [InitializeOnLoad]
    internal static class AddressablesBootstrap
    {
        static AddressablesBootstrap() { EditorApplication.delayCall += Initialize; }

        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("addressables", "com.unity.addressables",
                    new GetAddressablesContextTool(),
                    new ListAddressableEntriesTool(),
                    new ConfigureAddressableEntryTool(),
                    new ValidateAddressablesTool(),
                    new PreviewAddressablesBuildTool(),
                    new StartAddressablesBuildTool(),
                    new GetAddressablesBuildJobTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
