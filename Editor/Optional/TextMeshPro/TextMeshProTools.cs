using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Optional.TextMeshPro
{
    public sealed class CreateTmpTextTool : IMcpTool
    {
        public string Name => "create_tmp_text";
        public string Description => "Creates TextMeshPro UI or world text under an optional parent with Undo.";
        public JObject InputSchema => OptionalToolSupport.CreateSchema(new JObject
        {
            ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "ui", "world" }, ["default"] = "ui" },
            ["text"] = new JObject { ["type"] = "string", ["default"] = "Text" },
            ["fontSize"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 36 },
        });
        public bool IsWrite => true;

        public ToolResult Execute(JObject arguments)
        {
            var ui = !string.Equals((string)arguments["kind"], "world", StringComparison.OrdinalIgnoreCase);
            var parent = OptionalToolSupport.ResolveOptionalGameObject(
                arguments["parentTarget"],
                (int?)arguments["parentInstanceId"],
                "parent",
                out var parentError);
            if (parentError != null) return parentError;
            var gameObject = ui
                ? new GameObject((string)arguments["name"] ?? "TMP Text", typeof(RectTransform), typeof(CanvasRenderer))
                : new GameObject((string)arguments["name"] ?? "TMP Text");
            TMP_Text text = null;
            try
            {
                using (var mutation = new MutationScope("Create TMP Text"))
                {
                    mutation.RegisterCreated(gameObject);
                    if (parent != null)
                    {
                        SceneManager.MoveGameObjectToScene(gameObject, parent.scene);
                        mutation.SetParent(gameObject.transform, parent.transform);
                    }
                    text = ui
                        ? (TMP_Text)mutation.AddComponent(gameObject, typeof(TextMeshProUGUI))
                        : mutation.AddComponent<TMPro.TextMeshPro>(gameObject);
                    text.text = (string)arguments["text"] ?? "Text";
                    text.fontSize = (float?)arguments["fontSize"] ?? 36f;
                    mutation.Touch(text);
                    mutation.Complete();
                }
                return ToolResult.Ok($"created TMP {(ui ? "UI" : "world")} text", new JObject
                {
                    ["target"] = ToolTargetRef.Capture(text)?.ToJson(),
                    ["component"] = OptionalToolSupport.ComponentJson(text),
                });
            }
            catch (Exception exception)
            {
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
                return ToolResult.Error("CREATE_TMP_TEXT_FAILED", exception.Message);
            }
        }
    }

    public sealed class ValidateTmpSetupTool : IMcpTool
    {
        public string Name => "validate_tmp_setup";
        public string Description => "Audits TextMeshPro text components for missing font assets.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments)
        {
            var issues = new JArray();
            var texts = OptionalToolSupport.FindSceneComponents(typeof(TMP_Text))
                .OfType<TMP_Text>()
                .ToArray();
            foreach (var text in texts)
                if (text.font == null)
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "TMP_FONT_MISSING", ["target"] = ToolTargetRef.Capture(text)?.ToJson() });
            var fontCount = AssetDatabase.FindAssets("t:TMP_FontAsset").Length;
            if (fontCount == 0)
                issues.Add(new JObject { ["severity"] = "warning", ["code"] = "NO_TMP_FONT_ASSETS" });
            return ToolResult.Ok($"{issues.Count} TMP issue(s)", new JObject
            {
                ["valid"] = !issues.Any(token => string.Equals((string)token["severity"], "error", StringComparison.Ordinal)),
                ["textCount"] = texts.Length,
                ["fontAssetCount"] = fontCount,
                ["issues"] = issues,
            });
        }
    }
}
