using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Optional
{
    public static class OptionalAssetToolSupport
    {
        public static UnityEngine.Object[] FindAssets(params string[][] typeGroups)
        {
            var found = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            foreach (var names in typeGroups ?? Array.Empty<string[]>())
            {
                var type = OptionalToolSupport.ResolveType(names);
                if (type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type)) continue;
                foreach (var guid in AssetDatabase.FindAssets("t:" + type.Name))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        if (asset == null || !type.IsAssignableFrom(asset.GetType())) continue;
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string assetGuid, out long localId);
                        found[assetGuid + ":" + localId] = asset;
                    }
                }
            }
            return found.Values
                .OrderBy(asset => AssetDatabase.GetAssetPath(asset), StringComparer.Ordinal)
                .ThenBy(asset => asset.name, StringComparer.Ordinal)
                .ToArray();
        }

        public static ToolResult ValidateRenderPipelineSetup(
            string pipelineId,
            string packageId,
            string[] pipelineAssetTypes,
            string missingAssetCode,
            string notCurrentCode)
        {
            var expectedType = OptionalToolSupport.ResolveType(pipelineAssetTypes);
            var projectAssets = FindAssets(pipelineAssetTypes)
                .Where(asset => AssetDatabase.GetAssetPath(asset).StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var current = GraphicsSettings.currentRenderPipeline;
            var defaultPipeline = GraphicsSettings.defaultRenderPipeline;
            var activeQualityIndex = QualitySettings.GetQualityLevel();
            var activeQualityOverride = QualitySettings.renderPipeline;
            var issues = new JArray();
            var hasProjectAsset = projectAssets.Length > 0;
            var currentMatches = MatchesPipeline(current, expectedType, pipelineAssetTypes);
            var allQualityLevelsMatch = true;

            if (!hasProjectAsset)
                issues.Add(new JObject
                {
                    ["severity"] = "error",
                    ["code"] = missingAssetCode,
                    ["message"] = $"No {pipelineId.ToUpperInvariant()} pipeline asset exists under Assets/.",
                });
            if (!currentMatches)
                issues.Add(new JObject
                {
                    ["severity"] = "warning",
                    ["code"] = notCurrentCode,
                    ["current"] = PipelineJson(current),
                });

            var qualityLevels = new JArray();
            for (var index = 0; index < QualitySettings.names.Length; index++)
            {
                var qualityOverride = QualitySettings.GetRenderPipelineAssetAt(index);
                var effective = qualityOverride != null ? qualityOverride : defaultPipeline;
                var matches = MatchesPipeline(effective, expectedType, pipelineAssetTypes);
                allQualityLevelsMatch &= matches;
                qualityLevels.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = QualitySettings.names[index],
                    ["active"] = index == activeQualityIndex,
                    ["matches"] = matches,
                    ["override"] = PipelineJson(qualityOverride),
                    ["effective"] = PipelineJson(effective),
                });
                if (!matches)
                    issues.Add(new JObject
                    {
                        ["severity"] = "warning",
                        ["code"] = pipelineId.ToUpperInvariant() + "_NOT_EFFECTIVE_FOR_QUALITY",
                        ["qualityIndex"] = index,
                        ["qualityName"] = QualitySettings.names[index],
                        ["effective"] = PipelineJson(effective),
                    });
            }

            return ToolResult.Ok($"{issues.Count} {pipelineId.ToUpperInvariant()} issue(s)", new JObject
            {
                ["pipelineId"] = pipelineId,
                ["package"] = OptionalToolSupport.PackageJson(packageId),
                ["expectedType"] = expectedType?.FullName ?? pipelineAssetTypes?.FirstOrDefault(),
                ["hasProjectAsset"] = hasProjectAsset,
                ["currentMatches"] = currentMatches,
                ["allQualityLevelsMatch"] = allQualityLevelsMatch,
                ["assignmentsValid"] = allQualityLevelsMatch,
                ["valid"] = hasProjectAsset && allQualityLevelsMatch,
                ["current"] = PipelineJson(current),
                ["default"] = PipelineJson(defaultPipeline),
                ["activeQuality"] = new JObject
                {
                    ["index"] = activeQualityIndex,
                    ["name"] = QualitySettings.names.ElementAtOrDefault(activeQualityIndex),
                    ["override"] = PipelineJson(activeQualityOverride),
                    ["effective"] = PipelineJson(activeQualityOverride != null ? activeQualityOverride : defaultPipeline),
                },
                ["qualityLevels"] = qualityLevels,
                ["projectAssets"] = new JArray(projectAssets.Select(OptionalToolSupport.AssetJson)),
                ["issues"] = issues,
            });
        }

        private static bool MatchesPipeline(
            RenderPipelineAsset asset,
            Type expectedType,
            IEnumerable<string> expectedTypeNames)
        {
            if (asset == null) return false;
            if (expectedType != null) return expectedType.IsAssignableFrom(asset.GetType());
            var fullName = asset.GetType().FullName;
            return (expectedTypeNames ?? Array.Empty<string>())
                .Any(name => string.Equals(name, fullName, StringComparison.Ordinal));
        }

        private static JObject PipelineJson(RenderPipelineAsset asset)
        {
            if (asset == null)
                return new JObject
                {
                    ["assigned"] = false,
                    ["name"] = null,
                    ["type"] = null,
                    ["assetPath"] = null,
                    ["target"] = null,
                };
            var json = OptionalToolSupport.AssetJson(asset);
            json["assigned"] = true;
            return json;
        }

        public static ToolResult ListAssets(string packageId, params string[][] typeGroups)
        {
            var assets = FindAssets(typeGroups);
            return ToolResult.Ok($"{assets.Length} optional package asset(s)", new JObject
            {
                ["package"] = OptionalToolSupport.PackageJson(packageId),
                ["assets"] = new JArray(assets.Select(asset =>
                {
                    var json = OptionalToolSupport.AssetJson(asset);
                    json["properties"] = OptionalToolSupport.ReadSerialized(asset);
                    return json;
                })),
            });
        }

        public static ToolResult ConfigureAsset(
            JObject arguments,
            string undoName,
            string[][] allowedTypeGroups,
            IReadOnlyDictionary<string, string[]> allowlist)
        {
            var target = ToolTargetResolver.Resolve(ToolTargetResolver.ReadTarget(arguments), out var resolveError);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", resolveError);
            if (EditorUtility.IsPersistent(target) == false)
                return ToolResult.Error("ASSET_REQUIRED", "target must be a persistent project asset or sub-asset");
            var assetPath = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error("ASSET_NOT_EDITABLE", "target must be a writable asset under Assets/");
            if (!AssetDatabase.IsOpenForEdit(assetPath, out var editMessage, StatusQueryOptions.ForceUpdate))
                return ToolResult.Error("ASSET_NOT_EDITABLE", editMessage);
            var allowedTypes = (allowedTypeGroups ?? Array.Empty<string[]>())
                .Select(OptionalToolSupport.ResolveType).Where(type => type != null).ToArray();
            if (allowedTypes.Length == 0)
                return ToolResult.Error("OPTIONAL_TYPE_UNAVAILABLE", "No allowed asset type is available in the installed package version");
            if (!allowedTypes.Any(type => type.IsAssignableFrom(target.GetType())))
                return ToolResult.Error("TARGET_TYPE_MISMATCH", target.GetType().FullName);
            var values = arguments["properties"] as JObject;
            if (values == null || values.Count == 0)
                return ToolResult.Error("BAD_ARGS", "properties must not be empty");

            var serialized = new SerializedObject(target);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var input in values.Properties())
            {
                if (allowlist == null || !allowlist.TryGetValue(input.Name, out var candidates))
                    return ToolResult.Error("PROPERTY_NOT_ALLOWED", input.Name);
                SerializedProperty property = null;
                foreach (var candidate in candidates ?? Array.Empty<string>())
                {
                    property = serialized.FindProperty(candidate);
                    if (property != null) break;
                }
                if (property == null)
                    return ToolResult.Error("PROPERTY_UNAVAILABLE", $"{input.Name} is unavailable on {target.GetType().FullName}");
                paths[input.Name] = property.propertyPath;
            }

            try
            {
                using (var mutation = new MutationScope(undoName))
                {
                    mutation.Record(target);
                    serialized.Update();
                    foreach (var input in values.Properties())
                        SerializedValueCodec.Apply(serialized.FindProperty(paths[input.Name]), input.Value);
                    mutation.Apply(serialized, target);
                    mutation.Complete();
                }
                return ToolResult.Ok($"updated {values.Count} optional asset property value(s)", new JObject
                {
                    ["asset"] = OptionalToolSupport.AssetJson(target),
                    ["changed"] = values.DeepClone(),
                    ["savedAssets"] = false,
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("OPTIONAL_ASSET_PROPERTY_FAILED", exception.Message);
            }
        }
    }

    public sealed class OptionalListAssetsTool : IMcpTool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly string _packageId;
        private readonly string[][] _typeGroups;

        public OptionalListAssetsTool(string name, string description, string packageId, params string[][] typeGroups)
        {
            _name = name;
            _description = description;
            _packageId = packageId;
            _typeGroups = typeGroups;
        }

        public string Name => _name;
        public string Description => _description;
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public JObject OutputSchema => OptionalToolSupport.OutputSchema(new JObject
        {
            ["package"] = new JObject { ["type"] = "object" },
            ["assets"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
        }, "package", "assets");
        public ToolResult Execute(JObject arguments) => OptionalAssetToolSupport.ListAssets(_packageId, _typeGroups);
    }

    public sealed class OptionalConfigureAssetTool : IMcpTool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly string[][] _types;
        private readonly IReadOnlyDictionary<string, string[]> _allowlist;

        public OptionalConfigureAssetTool(
            string name,
            string description,
            string[][] types,
            IReadOnlyDictionary<string, string[]> allowlist)
        {
            _name = name;
            _description = description;
            _types = types;
            _allowlist = allowlist;
        }

        public string Name => _name;
        public string Description => _description;
        public JObject InputSchema => OptionalToolSupport.TargetSchema(new JObject
        {
            ["properties"] = OptionalToolSupport.PropertiesSchema(_allowlist),
        }, "properties");
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "optional_asset",
            Capabilities = McpCapability.AssetWrite,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = true,
        };
        public ToolResult Execute(JObject arguments) =>
            OptionalAssetToolSupport.ConfigureAsset(arguments, _description, _types, _allowlist);
    }
}
