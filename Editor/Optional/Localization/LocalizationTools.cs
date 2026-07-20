using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

namespace Maicon.SceneAnalyzer.Editor.Optional.Localization
{
    internal static class LocalizationToolSupport
    {
        public static StringTableCollection FindCollection(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            return LocalizationEditorSettings.GetStringTableCollections().FirstOrDefault(collection =>
                string.Equals(collection.TableCollectionName, reference, StringComparison.OrdinalIgnoreCase)
                || string.Equals(AssetDatabase.GetAssetPath(collection), reference, StringComparison.OrdinalIgnoreCase));
        }

        public static JObject LocaleJson(Locale locale) => locale == null ? null : new JObject
        {
            ["target"] = ToolTargetRef.Capture(locale)?.ToJson(),
            ["name"] = locale.name,
            ["identifier"] = locale.Identifier.Code,
            ["assetPath"] = AssetDatabase.GetAssetPath(locale),
        };

        public static JObject CollectionJson(StringTableCollection collection)
        {
            return new JObject
            {
                ["target"] = ToolTargetRef.Capture(collection)?.ToJson(),
                ["name"] = collection.TableCollectionName,
                ["assetPath"] = AssetDatabase.GetAssetPath(collection),
                ["sharedData"] = ToolTargetRef.Capture(collection.SharedData)?.ToJson(),
                ["tables"] = new JArray(collection.StringTables.Where(table => table != null).Select(table => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(table)?.ToJson(),
                    ["locale"] = table.LocaleIdentifier.Code,
                    ["assetPath"] = AssetDatabase.GetAssetPath(table),
                    ["entryCount"] = table.Count,
                })),
            };
        }
    }

    public sealed class GetLocalizationContextTool : IMcpTool
    {
        public string Name => "get_localization_context";
        public string Description => "Reports Localization package, locales and string table collection counts.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments) => ToolResult.Ok("Localization context", new JObject
        {
            ["package"] = OptionalToolSupport.PackageJson("com.unity.localization"),
            ["locales"] = new JArray(LocalizationEditorSettings.GetLocales().Select(LocalizationToolSupport.LocaleJson)),
            ["stringTableCollectionCount"] = LocalizationEditorSettings.GetStringTableCollections().Count,
            ["activeSettings"] = ToolTargetRef.Capture(LocalizationEditorSettings.ActiveLocalizationSettings)?.ToJson(),
        });
    }

    public sealed class ListLocalizationTablesTool : IMcpTool
    {
        public string Name => "list_localization_tables";
        public string Description => "Lists Localization string table collections and locale tables with stable references.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments)
        {
            var collections = LocalizationEditorSettings.GetStringTableCollections();
            return ToolResult.Ok($"{collections.Count} string table collection(s)", new JObject
            {
                ["collections"] = new JArray(collections.Select(LocalizationToolSupport.CollectionJson)),
            });
        }
    }

    public sealed class CreateLocaleTool : IMcpTool
    {
        public string Name => "create_localization_locale";
        public string Description => "Creates and registers a Locale asset with Undo; no global asset save is performed.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["code"] = new JObject { ["type"] = "string", ["minLength"] = 2, ["maxLength"] = 32 },
                ["assetPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "code", "assetPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;

        public ToolResult Execute(JObject arguments)
        {
            var path = ((string)arguments["assetPath"] ?? string.Empty).Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error("BAD_ASSET_PATH", "assetPath must be an Assets/*.asset path");
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return ToolResult.Error("ASSET_EXISTS", path);
            var code = (string)arguments["code"];
            if (LocalizationEditorSettings.GetLocale(new LocaleIdentifier(code)) != null)
                return ToolResult.Error("LOCALE_EXISTS", code);
            Locale locale = null;
            try
            {
                var folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
                    return ToolResult.Error("FOLDER_NOT_FOUND", folder);
                locale = Locale.CreateLocale(code);
                AssetDatabase.CreateAsset(locale, path);
                Undo.RegisterCreatedObjectUndo(locale, "Create Locale");
                LocalizationEditorSettings.AddLocale(locale, true);
                EditorUtility.SetDirty(locale);
                return ToolResult.Ok($"created Locale '{code}'", new JObject
                {
                    ["locale"] = LocalizationToolSupport.LocaleJson(locale),
                    ["savedAssets"] = false,
                });
            }
            catch (Exception exception)
            {
                if (locale != null && AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
                return ToolResult.Error("CREATE_LOCALE_FAILED", exception.Message);
            }
        }
    }

    public sealed class SetLocalizedStringTool : IMcpTool
    {
        public string Name => "set_localized_string";
        public string Description => "Creates or updates one StringTable entry for a collection and locale with Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["collection"] = new JObject { ["type"] = "string" },
                ["locale"] = new JObject { ["type"] = "string" },
                ["key"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["value"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "collection", "locale", "key", "value" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;

        public ToolResult Execute(JObject arguments)
        {
            var collection = LocalizationToolSupport.FindCollection((string)arguments["collection"]);
            if (collection == null) return ToolResult.Error("STRING_TABLE_COLLECTION_NOT_FOUND", (string)arguments["collection"]);
            var localeIdentifier = new LocaleIdentifier((string)arguments["locale"]);
            var table = collection.GetTable(localeIdentifier) as StringTable;
            if (table == null) return ToolResult.Error("STRING_TABLE_NOT_FOUND", localeIdentifier.Code);
            try
            {
                Undo.RecordObjects(new UnityEngine.Object[] { collection, collection.SharedData, table }, "Set Localized String");
                var entry = table.AddEntry((string)arguments["key"], (string)arguments["value"]);
                EditorUtility.SetDirty(collection.SharedData);
                EditorUtility.SetDirty(table);
                EditorUtility.SetDirty(collection);
                return ToolResult.Ok($"set '{(string)arguments["key"]}' for {localeIdentifier.Code}", new JObject
                {
                    ["collection"] = LocalizationToolSupport.CollectionJson(collection),
                    ["locale"] = localeIdentifier.Code,
                    ["key"] = (string)arguments["key"],
                    ["value"] = entry?.Value,
                    ["savedAssets"] = false,
                });
            }
            catch (Exception exception) { return ToolResult.Error("SET_LOCALIZED_STRING_FAILED", exception.Message); }
        }
    }

    public sealed class ValidateLocalizationTool : IMcpTool
    {
        public string Name => "validate_localization";
        public string Description => "Audits locale registration and missing locale tables in string table collections.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments)
        {
            var issues = new JArray();
            var locales = LocalizationEditorSettings.GetLocales();
            var collections = LocalizationEditorSettings.GetStringTableCollections();
            if (locales.Count == 0) issues.Add(new JObject { ["severity"] = "error", ["code"] = "NO_LOCALES" });
            foreach (var locale in locales)
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(locale)))
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "LOCALE_NOT_ASSET", ["locale"] = locale.Identifier.Code });
            foreach (var collection in collections)
            {
                if (collection.SharedData == null)
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "MISSING_SHARED_TABLE_DATA", ["collection"] = collection.TableCollectionName });
                foreach (var locale in locales)
                    if (collection.GetTable(locale.Identifier) == null)
                        issues.Add(new JObject { ["severity"] = "error", ["code"] = "MISSING_LOCALE_TABLE", ["collection"] = collection.TableCollectionName, ["locale"] = locale.Identifier.Code });
            }
            return ToolResult.Ok($"{issues.Count} Localization issue(s)", new JObject
            {
                ["valid"] = !issues.Any(token => string.Equals((string)token["severity"], "error", StringComparison.Ordinal)),
                ["localeCount"] = locales.Count,
                ["collectionCount"] = collections.Count,
                ["issues"] = issues,
            });
        }
    }
}
