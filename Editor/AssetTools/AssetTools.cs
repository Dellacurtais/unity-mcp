using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AssetTools
{
    internal static class AssetToolSupport
    {
        public static JObject PathsSchema(string propertyName = "paths") => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                [propertyName] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["minItems"] = 1,
                    ["maxItems"] = 1000,
                },
            },
            ["required"] = new JArray { propertyName },
            ["additionalProperties"] = false,
        };

        public static bool TryAssetPath(string path, bool mustExist, out string normalized, out string error)
        {
            normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
            error = null;
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && normalized != "Assets")
            {
                error = "asset paths must stay under Assets/";
                return false;
            }
            if (normalized.Contains("../") || normalized.EndsWith("/..", StringComparison.Ordinal))
            {
                error = "asset paths must not contain parent traversal";
                return false;
            }
            if (mustExist && AssetDatabase.LoadMainAssetAtPath(normalized) == null && !AssetDatabase.IsValidFolder(normalized))
            {
                error = $"asset not found: {normalized}";
                return false;
            }
            return true;
        }

        public static string ResolveGuidOrPath(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var candidate = value.Replace('\\', '/');
            return candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? candidate
                : AssetDatabase.GUIDToAssetPath(candidate);
        }

        public static JObject AssetJson(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var guid = AssetDatabase.AssetPathToGUID(path);
            long localFileId = 0;
            if (asset != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out localFileId);
            return new JObject
            {
                ["path"] = path,
                ["guid"] = guid,
                ["localFileId"] = localFileId,
                ["name"] = asset != null ? asset.name : null,
                ["type"] = asset != null ? asset.GetType().FullName : null,
                ["isFolder"] = AssetDatabase.IsValidFolder(path),
                ["target"] = ToolTargetRef.Capture(asset)?.ToJson(),
                ["dependencyHash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
            };
        }

        public static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var normalized = folder.Replace('\\', '/').TrimEnd('/');
            var slash = normalized.LastIndexOf('/');
            var parent = slash > 0 ? normalized.Substring(0, slash) : "Assets";
            var name = slash > 0 ? normalized.Substring(slash + 1) : normalized;
            EnsureFolder(parent);
            var guid = AssetDatabase.CreateFolder(parent, name);
            if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException($"could not create folder '{normalized}'");
        }

        public static JObject ImporterSnapshot(string path, int maxProperties = 1000)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return null;
            var serialized = new SerializedObject(importer);
            var iterator = serialized.GetIterator();
            var properties = new JObject();
            bool enterChildren = true;
            int count = 0;
            while (iterator.NextVisible(enterChildren) && count++ < maxProperties)
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;
                properties[iterator.propertyPath] = new JObject
                {
                    ["propertyType"] = iterator.propertyType.ToString(),
                    ["unityType"] = iterator.type,
                    ["value"] = SerializedValueCodec.Read(iterator),
                };
            }
            return new JObject
            {
                ["assetPath"] = path,
                ["importerType"] = importer.GetType().FullName,
                ["properties"] = properties,
                ["truncated"] = count > maxProperties,
            };
        }

        public static JObject ApplyImporterChanges(string path, JObject changes, bool reimport)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) throw new InvalidOperationException($"no importer for '{path}'");
            if (!AssetDatabase.IsOpenForEdit(path, out var editMessage, StatusQueryOptions.ForceUpdate))
                throw new InvalidOperationException($"asset is not editable: {editMessage}");

            var serialized = new SerializedObject(importer);
            var before = new JObject();
            var applied = new JArray();
            foreach (var change in changes.Properties())
            {
                var property = serialized.FindProperty(change.Name);
                if (property == null) throw new InvalidOperationException($"importer property not found: {change.Name}");
                before[change.Name] = SerializedValueCodec.Read(property);
            }

            Undo.RecordObject(importer, "Apply Import Settings");
            try
            {
                foreach (var change in changes.Properties())
                {
                    var property = serialized.FindProperty(change.Name);
                    SerializedValueCodec.Apply(property, change.Value);
                    applied.Add(change.Name);
                }
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(importer);
                if (reimport) importer.SaveAndReimport();
                else AssetDatabase.WriteImportSettingsIfDirty(path);
            }
            catch
            {
                serialized.Update();
                foreach (var previous in before.Properties())
                {
                    var property = serialized.FindProperty(previous.Name);
                    if (property != null) SerializedValueCodec.Apply(property, previous.Value);
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(importer);
                AssetDatabase.WriteImportSettingsIfDirty(path);
                throw;
            }

            return new JObject
            {
                ["asset"] = AssetJson(path),
                ["importerType"] = importer.GetType().FullName,
                ["before"] = before,
                ["changes"] = changes.DeepClone(),
                ["appliedProperties"] = applied,
                ["reimported"] = reimport,
                ["savedAssets"] = false,
            };
        }
    }

    public sealed class GetAssetDependenciesTool : IMcpTool
    {
        public string Name => "get_asset_dependencies";
        public string Description => "Returns direct or recursive AssetDatabase dependencies with stable asset identities.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["guidOrPath"] = new JObject { ["type"] = "string" },
                ["recursive"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["includeSelf"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 500 },
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var path = AssetToolSupport.ResolveGuidOrPath((string)args["guidOrPath"]);
            if (!AssetToolSupport.TryAssetPath(path, true, out path, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            bool includeSelf = (bool?)args["includeSelf"] ?? false;
            var dependencies = AssetDatabase.GetDependencies(path, (bool?)args["recursive"] ?? true)
                .Where(item => includeSelf || !string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item, StringComparer.Ordinal).ToArray();
            int offset = Math.Min((int?)args["offset"] ?? 0, dependencies.Length);
            int limit = (int?)args["limit"] ?? 500;
            var page = new JArray(dependencies.Skip(offset).Take(limit).Select(AssetToolSupport.AssetJson));
            return ToolResult.Ok($"{dependencies.Length} dependency asset(s)", new JObject
            {
                ["asset"] = AssetToolSupport.AssetJson(path),
                ["dependencies"] = page,
                ["offset"] = offset,
                ["count"] = page.Count,
                ["total"] = dependencies.Length,
                ["truncated"] = offset + page.Count < dependencies.Length,
            });
        }
    }

    public sealed class GetAssetDependentsTool : IMcpTool
    {
        public string Name => "get_asset_dependents";
        public string Description => "Scans project assets for direct references to one asset, with bounded scan pagination.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["guidOrPath"] = new JObject { ["type"] = "string" },
                ["scanOffset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["maxAssetsToScan"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20000, ["default"] = 2000 },
                ["resultLimit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 500 },
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var targetPath = AssetToolSupport.ResolveGuidOrPath((string)args["guidOrPath"]);
            if (!AssetToolSupport.TryAssetPath(targetPath, true, out targetPath, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            var all = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            int offset = Math.Min((int?)args["scanOffset"] ?? 0, all.Length);
            int scanLimit = (int?)args["maxAssetsToScan"] ?? 2000;
            int resultLimit = (int?)args["resultLimit"] ?? 500;
            var matches = new JArray();
            int scanned = 0;
            foreach (var candidate in all.Skip(offset).Take(scanLimit))
            {
                scanned++;
                if (string.Equals(candidate, targetPath, StringComparison.OrdinalIgnoreCase)) continue;
                var dependencies = AssetDatabase.GetDependencies(candidate, false);
                if (dependencies.Any(dependency => string.Equals(dependency, targetPath, StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Add(AssetToolSupport.AssetJson(candidate));
                    if (matches.Count >= resultLimit) break;
                }
            }
            return ToolResult.Ok($"found {matches.Count} direct dependent(s) in {scanned} scanned assets", new JObject
            {
                ["asset"] = AssetToolSupport.AssetJson(targetPath),
                ["dependents"] = matches,
                ["scanOffset"] = offset,
                ["scanned"] = scanned,
                ["totalProjectAssets"] = all.Length,
                ["nextScanOffset"] = offset + scanned < all.Length ? offset + scanned : (int?)null,
                ["scanComplete"] = offset + scanned >= all.Length,
                ["resultsTruncated"] = matches.Count >= resultLimit,
            });
        }
    }

    public sealed class CreateFolderTool : IMcpTool
    {
        public string Name => "create_folder";
        public string Description => "Creates an Assets/ folder hierarchy without saving unrelated assets.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["folderPath"] = new JObject { ["type"] = "string" } },
            ["required"] = new JArray { "folderPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["folderPath"], false, out var path, out var error)) return ToolResult.Error("BAD_ARGS", error);
            bool existed = AssetDatabase.IsValidFolder(path);
            if (!existed) AssetToolSupport.EnsureFolder(path);
            return ToolResult.Ok(existed ? $"folder already exists: {path}" : $"created folder: {path}", new JObject
            {
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["created"] = !existed,
            });
        }
    }

    public sealed class CopyAssetTool : IMcpTool
    {
        public string Name => "copy_asset";
        public string Description => "Copies one project asset to a new Assets/ path.";
        public JObject InputSchema => MoveCopySchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["sourcePath"], true, out var source, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            if (!AssetToolSupport.TryAssetPath((string)args["destinationPath"], false, out var destination, out error)) return ToolResult.Error("BAD_ARGS", error);
            if (AssetDatabase.LoadMainAssetAtPath(destination) != null || AssetDatabase.IsValidFolder(destination)) return ToolResult.Error("ASSET_EXISTS", destination);
            AssetToolSupport.EnsureFolder(System.IO.Path.GetDirectoryName(destination)?.Replace('\\', '/'));
            if (!AssetDatabase.CopyAsset(source, destination)) return ToolResult.Error("COPY_FAILED", $"could not copy '{source}' to '{destination}'");
            return ToolResult.Ok($"copied '{source}' to '{destination}'", new JObject { ["asset"] = AssetToolSupport.AssetJson(destination) });
        }

        internal static JObject MoveCopySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sourcePath"] = new JObject { ["type"] = "string" },
                ["destinationPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "sourcePath", "destinationPath" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class MoveAssetTool : IMcpTool
    {
        public string Name => "move_asset";
        public string Description => "Moves or renames one project asset while preserving its GUID.";
        public JObject InputSchema => CopyAssetTool.MoveCopySchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["sourcePath"], true, out var source, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            if (!AssetToolSupport.TryAssetPath((string)args["destinationPath"], false, out var destination, out error)) return ToolResult.Error("BAD_ARGS", error);
            if (!AssetDatabase.IsOpenForEdit(source, out var editMessage, StatusQueryOptions.ForceUpdate)) return ToolResult.Error("ASSET_NOT_EDITABLE", editMessage);
            AssetToolSupport.EnsureFolder(System.IO.Path.GetDirectoryName(destination)?.Replace('\\', '/'));
            var moveError = AssetDatabase.MoveAsset(source, destination);
            if (!string.IsNullOrEmpty(moveError)) return ToolResult.Error("MOVE_FAILED", moveError);
            return ToolResult.Ok($"moved '{source}' to '{destination}'", new JObject { ["asset"] = AssetToolSupport.AssetJson(destination) });
        }
    }

    public sealed class PreviewDeleteAssetsTool : IMcpTool
    {
        public string Name => "preview_delete_assets";
        public string Description => "Previews deletion targets and their direct dependents without deleting anything.";
        public JObject InputSchema
        {
            get
            {
                var schema = AssetToolSupport.PathsSchema();
                ((JObject)schema["properties"])["maxAssetsToScan"] = new JObject
                {
                    ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20000, ["default"] = 5000,
                };
                return schema;
            }
        }
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var requested = ((JArray)args["paths"]).Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            int scanLimit = (int?)args["maxAssetsToScan"] ?? 5000;
            var candidates = AssetDatabase.GetAllAssetPaths()
                .Where(candidate => candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate, StringComparer.Ordinal)
                .Take(scanLimit).ToArray();
            var targets = new JArray();
            foreach (var requestedPath in requested)
            {
                if (!AssetToolSupport.TryAssetPath(requestedPath, true, out var path, out var error))
                    return ToolResult.Error("ASSET_NOT_FOUND", error);
                var directDependents = candidates
                    .Where(candidate => !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)
                        && AssetDatabase.GetDependencies(candidate, false).Any(dependency => string.Equals(dependency, path, StringComparison.OrdinalIgnoreCase)))
                    .Take(100).ToArray();
                targets.Add(new JObject
                {
                    ["asset"] = AssetToolSupport.AssetJson(path),
                    ["directDependents"] = new JArray(directDependents),
                    ["dependentsTruncated"] = directDependents.Length >= 100,
                    ["scanTruncated"] = candidates.Length >= scanLimit,
                });
            }
            return ToolResult.Ok($"would delete {targets.Count} asset(s)", new JObject
            {
                ["targets"] = targets,
                ["nextTool"] = "confirm_delete_assets",
                ["requiresConfirmation"] = true,
            });
        }
    }

    public sealed class ConfirmDeleteAssetsTool : IMcpTool
    {
        public string Name => "confirm_delete_assets";
        public string Description => "Deletes exactly the supplied Assets/ paths after destructive capability confirmation.";
        public JObject InputSchema => AssetToolSupport.PathsSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var paths = ((JArray)args["paths"]).Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var deleted = new JArray();
            var failed = new JArray();
            foreach (var requestedPath in paths)
            {
                if (!AssetToolSupport.TryAssetPath(requestedPath, true, out var path, out var error))
                {
                    failed.Add(new JObject { ["path"] = requestedPath, ["error"] = error });
                    continue;
                }
                if (!AssetDatabase.IsOpenForEdit(path, out var editMessage, StatusQueryOptions.ForceUpdate))
                {
                    failed.Add(new JObject { ["path"] = path, ["error"] = editMessage });
                    continue;
                }
                if (AssetDatabase.DeleteAsset(path)) deleted.Add(path);
                else failed.Add(new JObject { ["path"] = path, ["error"] = "DeleteAsset returned false" });
            }
            if (failed.Count > 0) return ToolResult.Error("PARTIAL_DELETE", $"deleted {deleted.Count}; failed {failed.Count}", new JObject
            {
                ["deleted"] = deleted,
                ["failed"] = failed,
            });
            return ToolResult.Ok($"deleted {deleted.Count} asset(s)", new JObject { ["deleted"] = deleted });
        }
    }

    public sealed class ReadImportSettingsTool : IMcpTool
    {
        public string Name => "read_import_settings";
        public string Description => "Reads serialized importer properties for an asset using the stable SerializedValueCodec.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["maxProperties"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 1000 },
            },
            ["required"] = new JArray { "assetPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["assetPath"], true, out var path, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            var snapshot = AssetToolSupport.ImporterSnapshot(path, (int?)args["maxProperties"] ?? 1000);
            if (snapshot == null) return ToolResult.Error("IMPORTER_NOT_FOUND", $"no importer for '{path}'");
            return ToolResult.Ok($"read import settings for '{path}'", snapshot);
        }
    }

    public sealed class PreviewImportSettingsTool : IMcpTool
    {
        public string Name => "preview_import_settings";
        public string Description => "Validates importer property paths and previews before/after values without mutation.";
        public JObject InputSchema => ImportSettingsSchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["assetPath"], true, out var path, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return ToolResult.Error("IMPORTER_NOT_FOUND", $"no importer for '{path}'");
            var serialized = new SerializedObject(importer);
            var changes = args["changes"] as JObject ?? new JObject();
            var before = new JObject();
            foreach (var change in changes.Properties())
            {
                var property = serialized.FindProperty(change.Name);
                if (property == null) return ToolResult.Error("PROPERTY_NOT_FOUND", $"importer property not found: {change.Name}");
                before[change.Name] = SerializedValueCodec.Read(property);
            }
            return ToolResult.Ok($"would change {changes.Count} importer propert{(changes.Count == 1 ? "y" : "ies")}", new JObject
            {
                ["asset"] = AssetToolSupport.AssetJson(path),
                ["importerType"] = importer.GetType().FullName,
                ["before"] = before,
                ["after"] = changes.DeepClone(),
                ["nextTool"] = "apply_import_settings",
            });
        }

        internal static JObject ImportSettingsSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["changes"] = new JObject { ["type"] = "object" },
                ["reimport"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "assetPath", "changes" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class ApplyImportSettingsTool : IMcpTool
    {
        public string Name => "apply_import_settings";
        public string Description => "Applies serialized importer property changes with rollback on failure; optional explicit reimport.";
        public JObject InputSchema => PreviewImportSettingsTool.ImportSettingsSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["assetPath"], true, out var path, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            try
            {
                var result = AssetToolSupport.ApplyImporterChanges(path, args["changes"] as JObject ?? new JObject(), (bool?)args["reimport"] ?? true);
                return ToolResult.Ok($"applied import settings to '{path}'", result);
            }
            catch (Exception exception)
            {
                return ToolResult.Error("IMPORT_SETTINGS_FAILED", exception.Message, new JObject { ["assetPath"] = path, ["rolledBack"] = true });
            }
        }
    }

    public sealed class ReimportAssetsTool : IMcpTool
    {
        public string Name => "reimport_assets";
        public string Description => "Starts a cancellable job that explicitly reimports assets one at a time with progress.";
        public JObject InputSchema
        {
            get
            {
                var schema = AssetToolSupport.PathsSchema();
                ((JObject)schema["properties"])["forceUpdate"] = new JObject { ["type"] = "boolean", ["default"] = true };
                return schema;
            }
        }
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var paths = ((JArray)args["paths"]).Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var requested in paths)
                if (!AssetToolSupport.TryAssetPath(requested, true, out _, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            var job = ToolJobManager.Create("asset_reimport", args);
            EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
            {
                var completed = new JArray();
                for (int index = 0; index < paths.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var path = paths[index].Replace('\\', '/');
                    ToolJobManager.Report(record, (double)index / paths.Length, $"reimporting {path}");
                    await MainThreadDispatcher.RunAsync(() => AssetDatabase.ImportAsset(path,
                        ((bool?)args["forceUpdate"] ?? true) ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default), token);
                    completed.Add(path);
                    await Task.Yield();
                }
                return new JObject { ["reimported"] = completed };
            });
            return ToolResult.Ok($"reimport job {job.Id} queued", job.ToJson());
        }
    }

    public sealed class GetReimportJobTool : IMcpTool
    {
        public string Name => "get_reimport_job";
        public string Description => "Returns one asset reimport job state and progress.";
        public JObject InputSchema => JobSchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            if (!ToolJobManager.TryGet((string)args["jobId"], out var job) || job.Kind != "asset_reimport")
                return ToolResult.Error("JOB_NOT_FOUND", "asset reimport job not found");
            return ToolResult.Ok($"reimport job {job.Status}", job.ToJson());
        }

        internal static JObject JobSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["jobId"] = new JObject { ["type"] = "string" } },
            ["required"] = new JArray { "jobId" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class CancelReimportJobTool : IMcpTool
    {
        public string Name => "cancel_reimport_job";
        public string Description => "Requests cancellation between assets in a reimport job.";
        public JObject InputSchema => GetReimportJobTool.JobSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["jobId"];
            if (!ToolJobManager.TryGet(id, out var job) || job.Kind != "asset_reimport")
                return ToolResult.Error("JOB_NOT_FOUND", "asset reimport job not found");
            if (!ToolJobManager.Cancel(id, out job))
                return ToolResult.Error("REIMPORT_NOT_CANCELLABLE", "reimport job is terminal or already cancelling", job.ToJson());
            return ToolResult.Ok("reimport cancellation requested", job.ToJson());
        }
    }

    public sealed class GetVersionControlStatusTool : IMcpTool
    {
        public string Name => "get_version_control_status";
        public string Description => "Reports AssetDatabase editability for project assets without requiring a VCS provider API.";
        public JObject InputSchema => AssetToolSupport.PathsSchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var statuses = new JArray();
            foreach (var requested in ((JArray)args["paths"]).Values<string>())
            {
                var path = requested.Replace('\\', '/');
                bool editable = AssetDatabase.IsOpenForEdit(path, out var message, StatusQueryOptions.ForceUpdate);
                statuses.Add(new JObject
                {
                    ["path"] = path,
                    ["exists"] = AssetDatabase.LoadMainAssetAtPath(path) != null || AssetDatabase.IsValidFolder(path),
                    ["editable"] = editable,
                    ["message"] = message,
                });
            }
            return ToolResult.Ok($"checked {statuses.Count} asset path(s)", new JObject { ["statuses"] = statuses });
        }
    }

    public sealed class MakeAssetsEditableTool : IMcpTool
    {
        public string Name => "make_assets_editable";
        public string Description => "Requests checkout/editability for assets through AssetDatabase.MakeEditable.";
        public JObject InputSchema => AssetToolSupport.PathsSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var paths = ((JArray)args["paths"]).Values<string>().Select(path => path.Replace('\\', '/')).ToArray();
            var notEditable = new List<string>();
            bool success = AssetDatabase.MakeEditable(paths, "Scene Analyzer needs to edit these assets", notEditable);
            return success
                ? ToolResult.Ok($"{paths.Length} asset(s) are editable", new JObject { ["editable"] = new JArray(paths), ["notEditable"] = new JArray() })
                : ToolResult.Error("ASSETS_NOT_EDITABLE", $"{notEditable.Count} asset(s) remain non-editable", new JObject
                {
                    ["requested"] = new JArray(paths),
                    ["notEditable"] = new JArray(notEditable),
                });
        }
    }
}
