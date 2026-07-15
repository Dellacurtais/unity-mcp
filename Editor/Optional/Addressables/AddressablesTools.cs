using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.Addressables
{
    internal static class AddressablesToolSupport
    {
        public static AddressableAssetSettings Settings => AddressableAssetSettingsDefaultObject.Settings;

        public static JObject SettingsJson(AddressableAssetSettings settings)
        {
            if (settings == null) return new JObject { ["configured"] = false };
            return new JObject
            {
                ["configured"] = true,
                ["target"] = ToolTargetRef.Capture(settings)?.ToJson(),
                ["assetPath"] = settings.AssetPath,
                ["defaultGroup"] = settings.DefaultGroup != null ? settings.DefaultGroup.Name : null,
                ["groupCount"] = settings.groups != null ? settings.groups.Count : 0,
                ["activePlayerDataBuilder"] = settings.ActivePlayerDataBuilder != null ? settings.ActivePlayerDataBuilder.Name : null,
                ["buildRemoteCatalog"] = settings.BuildRemoteCatalog,
                ["bundleLocalCatalog"] = settings.BundleLocalCatalog,
            };
        }

        public static JObject EntryJson(AddressableAssetEntry entry)
        {
            if (entry == null) return null;
            var asset = AssetDatabase.LoadMainAssetAtPath(entry.AssetPath);
            return new JObject
            {
                ["guid"] = entry.guid,
                ["address"] = entry.address,
                ["assetPath"] = entry.AssetPath,
                ["asset"] = ToolTargetRef.Capture(asset)?.ToJson(),
                ["group"] = entry.parentGroup != null ? entry.parentGroup.Name : null,
                ["labels"] = new JArray((entry.labels ?? new HashSet<string>()).OrderBy(value => value, StringComparer.Ordinal)),
                ["readOnly"] = entry.ReadOnly,
                ["isFolder"] = entry.IsFolder,
                ["isScene"] = entry.IsScene,
            };
        }

        public static AddressableAssetGroup FindGroup(AddressableAssetSettings settings, string name)
        {
            if (settings == null) return null;
            if (string.IsNullOrWhiteSpace(name)) return settings.DefaultGroup;
            return settings.groups?.FirstOrDefault(group => group != null
                && string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static JArray AllEntries(AddressableAssetSettings settings)
        {
            var entries = new JArray();
            if (settings?.groups == null) return entries;
            foreach (var group in settings.groups.Where(group => group != null).OrderBy(group => group.Name, StringComparer.Ordinal))
                foreach (var entry in group.entries.OrderBy(entry => entry.address, StringComparer.Ordinal))
                    entries.Add(EntryJson(entry));
            return entries;
        }
    }

    public sealed class GetAddressablesContextTool : IMcpTool
    {
        public string Name => "get_addressables_context";
        public string Description => "Reports Addressables package, settings, groups and active content builder.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments) => ToolResult.Ok("Addressables context", new JObject
        {
            ["package"] = OptionalToolSupport.PackageJson("com.unity.addressables"),
            ["settings"] = AddressablesToolSupport.SettingsJson(AddressablesToolSupport.Settings),
        });
    }

    public sealed class ListAddressableEntriesTool : IMcpTool
    {
        public string Name => "list_addressable_entries";
        public string Description => "Lists Addressables groups and explicit entries with addresses, labels and stable asset references.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments)
        {
            var settings = AddressablesToolSupport.Settings;
            if (settings == null) return ToolResult.Error("ADDRESSABLES_SETTINGS_NOT_FOUND", "Create Addressables settings in the Unity Addressables window first");
            var groups = new JArray(settings.groups.Where(group => group != null).OrderBy(group => group.Name, StringComparer.Ordinal).Select(group => new JObject
            {
                ["target"] = ToolTargetRef.Capture(group)?.ToJson(),
                ["name"] = group.Name,
                ["guid"] = group.Guid,
                ["readOnly"] = group.ReadOnly,
                ["default"] = group.IsDefaultGroup(),
                ["entryCount"] = group.entries.Count,
                ["schemas"] = new JArray(group.SchemaTypes.Select(type => type.FullName)),
            }));
            var entries = AddressablesToolSupport.AllEntries(settings);
            return ToolResult.Ok($"{groups.Count} group(s), {entries.Count} entry/entries", new JObject
            {
                ["settings"] = AddressablesToolSupport.SettingsJson(settings),
                ["groups"] = groups,
                ["entries"] = entries,
            });
        }
    }

    public sealed class ConfigureAddressableEntryTool : IMcpTool
    {
        public string Name => "configure_addressable_entry";
        public string Description => "Creates or moves an Addressables entry and configures its address and labels with Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["groupName"] = new JObject { ["type"] = "string" },
                ["address"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["labels"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["maxItems"] = 128 },
                ["replaceLabels"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "assetPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "addressables",
            Capabilities = McpCapability.AssetWrite | McpCapability.Destructive,
            Risk = McpRiskLevel.High,
            ConfirmationPolicy = McpConfirmationPolicy.Required,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = true,
        };

        public ToolResult Execute(JObject arguments)
        {
            var settings = AddressablesToolSupport.Settings;
            if (settings == null) return ToolResult.Error("ADDRESSABLES_SETTINGS_NOT_FOUND", "Create Addressables settings first");
            var path = ((string)arguments["assetPath"] ?? string.Empty).Replace('\', '/');
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error("BAD_ASSET_PATH", "assetPath must be under Assets/");
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid) || AssetDatabase.LoadMainAssetAtPath(path) == null)
                return ToolResult.Error("ASSET_NOT_FOUND", path);
            var group = AddressablesToolSupport.FindGroup(settings, (string)arguments["groupName"]);
            if (group == null) return ToolResult.Error("ADDRESSABLE_GROUP_NOT_FOUND", (string)arguments["groupName"]);
            if (group.ReadOnly) return ToolResult.Error("ADDRESSABLE_GROUP_READ_ONLY", group.Name);
            var previousGroup = settings.FindAssetEntry(guid)?.parentGroup;
            try
            {
                using (var mutation = new MutationScope("Configure Addressable Entry"))
                {
                    mutation.Record(settings);
                    mutation.Record(group);
                    if (previousGroup != null && previousGroup != group) mutation.Record(previousGroup);
                    var entry = settings.CreateOrMoveEntry(guid, group, false, false);
                    if (entry == null) throw new InvalidOperationException($"Unable to create or move Addressables entry for '{path}'");
                    if (!string.IsNullOrWhiteSpace((string)arguments["address"])) entry.SetAddress((string)arguments["address"], false);
                    var requestedLabels = new HashSet<string>(((JArray)arguments["labels"] ?? new JArray()).Values<string>(), StringComparer.Ordinal);
                    if ((bool?)arguments["replaceLabels"] ?? false)
                        foreach (var existing in entry.labels.ToArray())
                            if (!requestedLabels.Contains(existing)) entry.SetLabel(existing, false, false, false);
                    foreach (var label in requestedLabels) entry.SetLabel(label, true, true, false);
                    settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true, false);
                    mutation.Touch(group);
                    mutation.Touch(settings);
                    mutation.Complete();
                    return ToolResult.Ok($"configured addressable '{entry.address}'", new JObject
                    {
                        ["entry"] = AddressablesToolSupport.EntryJson(entry),
                        ["savedAssets"] = false,
                    });
                }
            }
            catch (Exception exception) { return ToolResult.Error("ADDRESSABLE_ENTRY_FAILED", exception.Message); }
        }
    }

    public sealed class ValidateAddressablesTool : IMcpTool
    {
        public string Name => "validate_addressables";
        public string Description => "Audits missing assets, empty/duplicate addresses and settings/group consistency.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments)
        {
            var settings = AddressablesToolSupport.Settings;
            if (settings == null) return ToolResult.Error("ADDRESSABLES_SETTINGS_NOT_FOUND", "Create Addressables settings first");
            var issues = new JArray();
            var addresses = new Dictionary<string, List<AddressableAssetEntry>>(StringComparer.Ordinal);
            foreach (var group in settings.groups.Where(group => group != null))
            {
                if (group.entries.Count == 0)
                    issues.Add(new JObject { ["severity"] = "warning", ["code"] = "EMPTY_ADDRESSABLE_GROUP", ["group"] = group.Name });
                foreach (var entry in group.entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.address))
                        issues.Add(new JObject { ["severity"] = "error", ["code"] = "EMPTY_ADDRESS", ["guid"] = entry.guid, ["group"] = group.Name });
                    else
                    {
                        if (!addresses.TryGetValue(entry.address, out var list)) addresses[entry.address] = list = new List<AddressableAssetEntry>();
                        list.Add(entry);
                    }
                    if (string.IsNullOrEmpty(entry.AssetPath) || AssetDatabase.LoadMainAssetAtPath(entry.AssetPath) == null)
                        issues.Add(new JObject { ["severity"] = "error", ["code"] = "MISSING_ADDRESSABLE_ASSET", ["guid"] = entry.guid, ["address"] = entry.address });
                }
            }
            foreach (var pair in addresses.Where(pair => pair.Value.Count > 1))
                issues.Add(new JObject { ["severity"] = "error", ["code"] = "DUPLICATE_ADDRESS", ["address"] = pair.Key, ["count"] = pair.Value.Count });
            return ToolResult.Ok($"{issues.Count} Addressables issue(s)", new JObject
            {
                ["valid"] = !issues.Any(token => string.Equals((string)token["severity"], "error", StringComparison.Ordinal)),
                ["entryCount"] = addresses.Sum(pair => pair.Value.Count),
                ["issues"] = issues,
            });
        }
    }

    public sealed class PreviewAddressablesBuildTool : IMcpTool
    {
        public string Name => "preview_addressables_build";
        public string Description => "Previews the synchronous Addressables content build inputs without starting it.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments)
        {
            var settings = AddressablesToolSupport.Settings;
            if (settings == null) return ToolResult.Error("ADDRESSABLES_SETTINGS_NOT_FOUND", "Create Addressables settings first");
            return ToolResult.Ok("Addressables build preview", new JObject
            {
                ["settings"] = AddressablesToolSupport.SettingsJson(settings),
                ["entryCount"] = settings.groups.Where(group => group != null).Sum(group => group.entries.Count),
                ["warning"] = "BuildPlayerContent is synchronous in the public API and cannot be cancelled once started.",
                ["requiresConfirmation"] = true,
            });
        }
    }

    public sealed class StartAddressablesBuildTool : IMcpTool
    {
        public string Name => "start_addressables_build";
        public string Description => "Starts an explicit Addressables content build job. The underlying synchronous build cannot be cancelled.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "addressables",
            Capabilities = McpCapability.Build,
            Risk = McpRiskLevel.Critical,
            ConfirmationPolicy = McpConfirmationPolicy.Required,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = false,
        };

        public ToolResult Execute(JObject arguments)
        {
            if (AddressablesToolSupport.Settings == null)
                return ToolResult.Error("ADDRESSABLES_SETTINGS_NOT_FOUND", "Create Addressables settings first");
            var job = ToolJobManager.Create("addressables_build", arguments, false);
            EditorApplication.delayCall += () => ToolJobManager.Start(job, BuildAsync);
            return ToolResult.Ok("Addressables build queued", job.ToJson());
        }

        private static async Task<JToken> BuildAsync(ToolJobRecord job, CancellationToken cancellationToken)
        {
            return await MainThreadDispatcher.RunAsync<JToken>(() =>
            {
                ToolJobManager.Report(job, 0.1, "building Addressables content (synchronous, not cancellable)");
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                var json = new JObject
                {
                    ["error"] = result?.Error,
                    ["durationSeconds"] = result?.Duration ?? 0,
                    ["locationCount"] = result?.LocationCount ?? 0,
                    ["outputPath"] = result?.OutputPath,
                    ["contentStateFilePath"] = result?.ContentStateFilePath,
                    ["remoteCatalogJsonFilePath"] = result?.RemoteCatalogJsonFilePath,
                    ["remoteCatalogHashFilePath"] = result?.RemoteCatalogHashFilePath,
                };
                if (!string.IsNullOrEmpty(result?.Error)) throw new InvalidOperationException(result.Error);
                return json;
            }, cancellationToken);
        }
    }

    public sealed class GetAddressablesBuildJobTool : IMcpTool
    {
        public string Name => "get_addressables_build_job";
        public string Description => "Returns the state and result of one Addressables content build job.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["jobId"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            },
            ["required"] = new JArray { "jobId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments)
        {
            if (!ToolJobManager.TryGet((string)arguments["jobId"], out var job)
                || !string.Equals(job.Kind, "addressables_build", StringComparison.Ordinal))
                return ToolResult.Error("JOB_NOT_FOUND", "Addressables build job not found");
            return ToolResult.Ok($"Addressables build job {job.Status}", job.ToJson());
        }
    }
}
