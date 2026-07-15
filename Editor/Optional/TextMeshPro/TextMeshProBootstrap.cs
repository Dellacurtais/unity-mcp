using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.TextMeshPro
{
    [InitializeOnLoad]
    internal static class TextMeshProBootstrap
    {
        private static string PackageId => OptionalToolSupport.FindPackage("com.unity.ugui") != null
            ? "com.unity.ugui"
            : "com.unity.textmeshpro";
        internal static readonly string[] TextTypes = { "TMPro.TextMeshProUGUI", "TMPro.TextMeshPro", "TMPro.TMP_Text" };
        internal static readonly string[] UiTypes = { "TMPro.TextMeshProUGUI" };
        internal static readonly string[] WorldTypes = { "TMPro.TextMeshPro" };
        internal static readonly string[] FontTypes = { "TMPro.TMP_FontAsset" };
        private static readonly IReadOnlyDictionary<string, string[]> Types = new Dictionary<string, string[]>
        {
            ["uiText"] = UiTypes,
            ["worldText"] = WorldTypes,
            ["fontAsset"] = FontTypes,
        };
        private static readonly IReadOnlyDictionary<string, string[]> TextProperties = new Dictionary<string, string[]>
        {
            ["text"] = new[] { "m_text" },
            ["font"] = new[] { "m_fontAsset" },
            ["fontSize"] = new[] { "m_fontSize" },
            ["fontStyle"] = new[] { "m_fontStyle" },
            ["color"] = new[] { "m_fontColor" },
            ["alignment"] = new[] { "m_textAlignment" },
            ["enableWordWrapping"] = new[] { "m_enableWordWrapping" },
            ["overflowMode"] = new[] { "m_overflowMode" },
            ["raycastTarget"] = new[] { "m_RaycastTarget" },
            ["autoSize"] = new[] { "m_enableAutoSizing" },
            ["fontSizeMin"] = new[] { "m_fontSizeMin" },
            ["fontSizeMax"] = new[] { "m_fontSizeMax" },
        };

        static TextMeshProBootstrap() { EditorApplication.delayCall += Initialize; }
        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("textmeshpro", PackageId,
                    new OptionalContextTool("get_tmp_context", "Reports TextMeshPro package, text and font asset context.", PackageId, Types),
                    new OptionalListComponentsTool("list_tmp_text_components", "Lists TextMeshPro UI and world text components.", PackageId, UiTypes, WorldTypes),
                    new OptionalListAssetsTool("list_tmp_font_assets", "Lists TextMeshPro font assets.", PackageId, FontTypes),
                    new CreateTmpTextTool(),
                    new OptionalConfigureComponentTool("configure_tmp_text", "Configure TextMeshPro text", TextTypes, TextProperties),
                    new ValidateTmpSetupTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
