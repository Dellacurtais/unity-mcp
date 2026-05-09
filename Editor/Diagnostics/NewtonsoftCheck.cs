using System;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Diagnostics
{
    /// Standalone asmdef with NO references. Compiles even when the main
    /// plugin asmdef is broken (e.g. missing Newtonsoft.Json), so it can
    /// surface a clear actionable error pointing at the missing package
    /// instead of dumping cryptic CS0122 'JObject inaccessible' messages.
    [InitializeOnLoad]
    internal static class NewtonsoftCheck
    {
        private const string Header = "[scene-analyzer]";

        static NewtonsoftCheck()
        {
            EditorApplication.delayCall += Check;
        }

        private static void Check()
        {
            try
            {
                // Probe the public Newtonsoft type by string. Don't fail compilation
                // when missing — just log a guided error.
                var publicType =
                    Type.GetType("Newtonsoft.Json.Linq.JObject, Unity.Newtonsoft.Json", false)
                    ?? Type.GetType("Newtonsoft.Json.Linq.JObject, Newtonsoft.Json", false);

                bool ok = publicType != null && publicType.IsPublic;

                if (!ok)
                {
                    Debug.LogError(
                        $"{Header} Newtonsoft.Json is missing OR another package is shadowing it with an internal copy.\n" +
                        "Effects: Editor scripts that use JObject (most of this plugin) won't compile and you may see\n" +
                        "  CS0122 'JObject is inaccessible due to its protection level'\n" +
                        "  + Mono.Cecil 'Failed to resolve assembly com.maicon.scene-analyzer.Editor.PostFx'.\n\n" +
                        "Fix: install the official Newtonsoft Json package — Window → Package Manager → '+' → \n" +
                        "  'Add package by name' → com.unity.nuget.newtonsoft-json (any 3.x version, e.g. 3.2.1).\n" +
                        "Alternatively edit Packages/manifest.json and add:\n" +
                        "  \"com.unity.nuget.newtonsoft-json\": \"3.2.1\"\n\n" +
                        "If the plugin was placed under Assets/Plugins/, prefer the UPM route instead:\n" +
                        "  Package Manager → '+' → Add package from disk → select the plugin's package.json.\n" +
                        "That auto-resolves package.json dependencies (Newtonsoft included).");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
