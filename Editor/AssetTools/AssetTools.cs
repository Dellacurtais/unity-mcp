using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AssetTools
{
    internal static class AssetToolSupport
    {
        public sealed class ImporterApplyException : Exception
        {
            public bool RolledBack { get; }

            public ImporterApplyException(string message, bool rolledBack, Exception innerException)
                : base(message, innerException)
            {
                RolledBack = rolledBack;
            }
        }

        public static JObject AssetPathSchema(bool allowRoot = false) => new JObject
        {
            ["type"] = "string",
            ["pattern"] = allowRoot
                ? "^Assets(?:/(?!.*(?:^|/)\\.{1,2}(?:/|$))(?!.*//)(?!.*\\\\).+)?$"
                : "^Assets/(?!.*(?:^|/)\\.{1,2}(?:/|$))(?!.*//)(?!.*\\\\).+$",
            ["description"] = allowRoot
                ? "Canonical project-relative path at or below Assets/."
                : "Canonical project-relative path below Assets/; the Assets root itself is rejected.",
        };

        public static JObject PathsSchema(string propertyName = "paths") => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                [propertyName] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = AssetPathSchema(),
                    ["minItems"] = 1,
                    ["maxItems"] = 1000,
                    ["uniqueItems"] = true,
                },
            },
            ["required"] = new JArray { propertyName },
            ["additionalProperties"] = false,
        };

        public static JObject AssetSchema => ObjectSchema(new JObject
        {
            ["path"] = new JObject { ["type"] = "string" },
            ["guid"] = new JObject { ["type"] = "string" },
            ["localFileId"] = new JObject { ["type"] = "integer" },
            ["name"] = Nullable(new JObject { ["type"] = "string" }),
            ["type"] = Nullable(new JObject { ["type"] = "string" }),
            ["isFolder"] = new JObject { ["type"] = "boolean" },
            ["target"] = Nullable(SchemaHelpers.TargetRefSchema),
            ["dependencyHash"] = new JObject { ["type"] = "string" },
        }, "path", "guid", "localFileId", "name", "type", "isFolder", "target", "dependencyHash");

        public static JObject AssetListOutput(string arrayName) => ObjectSchema(new JObject
        {
            ["catalogTotal"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["total"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["returned"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["truncated"] = new JObject { ["type"] = "boolean" },
            [arrayName] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
        }, "catalogTotal", "total", "returned", "offset", "truncated", arrayName);

        public static JObject AssetWrapperOutput => ObjectSchema(new JObject { ["asset"] = AssetSchema }, "asset");
        public static JObject JobOutput => ObjectSchema(new JObject
        {
            ["jobId"] = new JObject { ["type"] = "string" },
            ["kind"] = new JObject { ["type"] = "string" },
            ["status"] = new JObject { ["type"] = "string" },
            ["progress"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
            ["message"] = Nullable(new JObject { ["type"] = "string" }),
            ["error"] = Nullable(new JObject { ["type"] = "string" }),
            ["result"] = new JObject(),
            ["input"] = new JObject { ["type"] = "object" },
            ["createdUtc"] = new JObject { ["type"] = "string" },
            ["startedUtc"] = Nullable(new JObject { ["type"] = "string" }),
            ["completedUtc"] = Nullable(new JObject { ["type"] = "string" }),
            ["canCancel"] = new JObject { ["type"] = "boolean" },
        }, "jobId", "kind", "status", "progress", "message", "error", "result", "input", "createdUtc", "startedUtc", "completedUtc", "canCancel");

        public static bool TryAssetPath(string path, bool mustExist, out string normalized, out string error, bool allowRoot = false)
        {
            normalized = null;
            error = null;
            if (string.IsNullOrEmpty(path) || !string.Equals(path, path.Trim(), StringComparison.Ordinal)
                || path.IndexOf('\\') >= 0 || (!path.StartsWith("Assets/", StringComparison.Ordinal) && path != "Assets"))
            {
                error = "asset paths must be canonical project-relative paths using forward slashes under Assets/";
                return false;
            }
            if (!allowRoot && path == "Assets")
            {
                error = "the Assets root cannot be used for this operation";
                return false;
            }
            var segments = path.Split('/');
            var invalid = Path.GetInvalidFileNameChars();
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." || segment.IndexOfAny(invalid) >= 0))
            {
                error = "asset path contains an empty, dot, whitespace-only, or invalid segment";
                return false;
            }
            normalized = string.Join("/", segments);
            if (mustExist && !AssetDatabase.AssetPathExists(normalized))
            {
                error = $"asset not found: {normalized}";
                return false;
            }
            return true;
        }

        public static string ResolveGuidOrPath(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.StartsWith("Assets/", StringComparison.Ordinal) || value == "Assets"
                ? value
                : AssetDatabase.GUIDToAssetPath(value);
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

        public static void EnsureFolder(string folder, List<string> createdFolders = null)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            if (!TryAssetPath(folder, false, out var normalized, out var error, allowRoot: true))
                throw new InvalidOperationException(error);
            if (AssetDatabase.AssetPathExists(normalized))
                throw new InvalidOperationException($"cannot create folder because an asset already exists at '{normalized}'");
            var slash = normalized.LastIndexOf('/');
            var parent = slash > 0 ? normalized.Substring(0, slash) : "Assets";
            var name = slash > 0 ? normalized.Substring(slash + 1) : normalized;
            EnsureFolder(parent, createdFolders);
            var guid = AssetDatabase.CreateFolder(parent, name);
            if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException($"could not create folder '{normalized}'");
            createdFolders?.Add(normalized);
        }

        public static void CleanupEmptyFolders(List<string> createdFolders)
        {
            if (createdFolders == null) return;
            for (var index = createdFolders.Count - 1; index >= 0; index--)
                if (AssetDatabase.IsValidFolder(createdFolders[index]) && AssetDatabase.FindAssets(string.Empty, new[] { createdFolders[index] }).Length == 0)
                    AssetDatabase.DeleteAsset(createdFolders[index]);
        }

        public static JObject ImporterSnapshot(string path, int maxProperties = 1000)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return null;
            var serialized = new SerializedObject(importer);
            var iterator = serialized.GetIterator();
            var properties = new JObject();
            bool enterChildren = true;
            bool truncated = false;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;
                if (properties.Count >= maxProperties) { truncated = true; break; }
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
                ["propertyCount"] = properties.Count,
                ["properties"] = properties,
                ["truncated"] = truncated,
            };
        }

        public static JObject ApplyImporterChanges(string path, JObject changes, bool reimport)
        {
            if (changes == null || changes.Count == 0) throw new InvalidOperationException("changes must contain at least one importer property");
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
                before[change.Name] = SerializedValueCodec.Read(property, new SerializedValueCodec.ReadOptions
                {
                    MaxDepth = 64,
                    MaxArrayItems = int.MaxValue,
                });
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
            catch (Exception applyException)
            {
                bool rolledBack = false;
                Exception rollbackException = null;
                try
                {
                    var rollbackImporter = AssetImporter.GetAtPath(path);
                    if (rollbackImporter == null) throw new InvalidOperationException($"no importer for rollback of '{path}'");
                    var rollbackSerialized = new SerializedObject(rollbackImporter);
                    foreach (var previous in before.Properties())
                    {
                        var property = rollbackSerialized.FindProperty(previous.Name);
                        if (property == null) throw new InvalidOperationException($"rollback property not found: {previous.Name}");
                        SerializedValueCodec.Apply(property, previous.Value);
                    }
                    rollbackSerialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(rollbackImporter);
                    if (reimport) rollbackImporter.SaveAndReimport();
                    else AssetDatabase.WriteImportSettingsIfDirty(path);
                    rolledBack = true;
                }
                catch (Exception exception)
                {
                    rollbackException = exception;
                }
                var message = rolledBack
                    ? applyException.Message
                    : $"{applyException.Message}; rollback also failed: {rollbackException?.Message}";
                throw new ImporterApplyException(message, rolledBack, applyException);
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

        public static JObject ObjectSchema(JObject properties, params string[] required)
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
            if (required != null && required.Length > 0) schema["required"] = new JArray(required);
            return schema;
        }

        public static JObject Nullable(JObject schema) => new JObject
        {
            ["oneOf"] = new JArray { schema, new JObject { ["type"] = "null" } },
        };
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
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["asset"] = AssetToolSupport.AssetSchema,
            ["dependencies"] = new JObject { ["type"] = "array", ["items"] = AssetToolSupport.AssetSchema },
            ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["total"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["truncated"] = new JObject { ["type"] = "boolean" },
        }, "asset", "dependencies", "offset", "count", "total", "truncated");
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
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["asset"] = AssetToolSupport.AssetSchema,
            ["dependents"] = new JObject { ["type"] = "array", ["items"] = AssetToolSupport.AssetSchema },
            ["scanOffset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["scanned"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["totalProjectAssets"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["nextScanOffset"] = AssetToolSupport.Nullable(new JObject { ["type"] = "integer", ["minimum"] = 0 }),
            ["scanComplete"] = new JObject { ["type"] = "boolean" },
            ["resultsTruncated"] = new JObject { ["type"] = "boolean" },
        }, "asset", "dependents", "scanOffset", "scanned", "totalProjectAssets", "nextScanOffset", "scanComplete", "resultsTruncated");
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
            ["properties"] = new JObject { ["folderPath"] = AssetToolSupport.AssetPathSchema() },
            ["required"] = new JArray { "folderPath" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["path"] = new JObject { ["type"] = "string" },
            ["guid"] = new JObject { ["type"] = "string" },
            ["created"] = new JObject { ["type"] = "boolean" },
        }, "path", "guid", "created");
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["folderPath"], false, out var path, out var error)) return ToolResult.Error("BAD_ARGS", error);
            bool existed = AssetDatabase.IsValidFolder(path);
            try { if (!existed) AssetToolSupport.EnsureFolder(path); }
            catch (Exception exception) { return ToolResult.Error("FOLDER_CREATE_FAILED", exception.Message); }
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
        public JObject OutputSchema => AssetToolSupport.AssetWrapperOutput;
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["sourcePath"], true, out var source, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            if (!AssetToolSupport.TryAssetPath((string)args["destinationPath"], false, out var destination, out error)) return ToolResult.Error("BAD_ARGS", error);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return ToolResult.Error("BAD_ARGS", "sourcePath and destinationPath must differ");
            if (AssetDatabase.AssetPathExists(destination)) return ToolResult.Error("ASSET_EXISTS", destination);
            var createdFolders = new List<string>();
            try
            {
                AssetToolSupport.EnsureFolder(Path.GetDirectoryName(destination)?.Replace('\\', '/'), createdFolders);
                if (!AssetDatabase.CopyAsset(source, destination))
                {
                    AssetToolSupport.CleanupEmptyFolders(createdFolders);
                    return ToolResult.Error("COPY_FAILED", $"could not copy '{source}' to '{destination}'");
                }
            }
            catch (Exception exception)
            {
                if (AssetDatabase.AssetPathExists(destination)) AssetDatabase.DeleteAsset(destination);
                AssetToolSupport.CleanupEmptyFolders(createdFolders);
                return ToolResult.Error("COPY_FAILED", exception.Message);
            }
            return ToolResult.Ok($"copied '{source}' to '{destination}'", new JObject { ["asset"] = AssetToolSupport.AssetJson(destination) });
        }

        internal static JObject MoveCopySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sourcePath"] = AssetToolSupport.AssetPathSchema(),
                ["destinationPath"] = AssetToolSupport.AssetPathSchema(),
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
        public JObject OutputSchema => AssetToolSupport.AssetWrapperOutput;
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["sourcePath"], true, out var source, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            if (!AssetToolSupport.TryAssetPath((string)args["destinationPath"], false, out var destination, out error)) return ToolResult.Error("BAD_ARGS", error);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return ToolResult.Error("BAD_ARGS", "sourcePath and destinationPath must differ");
            if (AssetDatabase.AssetPathExists(destination)) return ToolResult.Error("ASSET_EXISTS", destination);
            if (!AssetDatabase.IsOpenForEdit(source, out var editMessage, StatusQueryOptions.ForceUpdate)) return ToolResult.Error("ASSET_NOT_EDITABLE", editMessage);
            var createdFolders = new List<string>();
            try { AssetToolSupport.EnsureFolder(Path.GetDirectoryName(destination)?.Replace('\\', '/'), createdFolders); }
            catch (Exception exception) { AssetToolSupport.CleanupEmptyFolders(createdFolders); return ToolResult.Error("FOLDER_CREATE_FAILED", exception.Message); }
            var moveError = AssetDatabase.MoveAsset(source, destination);
            if (!string.IsNullOrEmpty(moveError))
            {
                AssetToolSupport.CleanupEmptyFolders(createdFolders);
                return ToolResult.Error("MOVE_FAILED", moveError);
            }
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
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["targets"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["nextTool"] = new JObject { ["type"] = "string", ["const"] = "confirm_delete_assets" },
            ["requiresConfirmation"] = new JObject { ["type"] = "boolean", ["const"] = true },
        }, "targets", "nextTool", "requiresConfirmation");
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var requested = ((JArray)args["paths"]).Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            int scanLimit = (int?)args["maxAssetsToScan"] ?? 5000;
            var allCandidates = AssetDatabase.GetAllAssetPaths()
                .Where(candidate => candidate.StartsWith("Assets/", StringComparison.Ordinal))
                .OrderBy(candidate => candidate, StringComparer.Ordinal).ToArray();
            var candidates = allCandidates.Take(scanLimit).ToArray();
            var targets = new JArray();
            foreach (var requestedPath in requested)
            {
                if (!AssetToolSupport.TryAssetPath(requestedPath, true, out var path, out var error))
                    return ToolResult.Error("ASSET_NOT_FOUND", error);
                var dependentCandidates = candidates
                    .Where(candidate => !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)
                        && AssetDatabase.GetDependencies(candidate, false).Any(dependency => string.Equals(dependency, path, StringComparison.OrdinalIgnoreCase)))
                    .Take(101).ToArray();
                var directDependents = dependentCandidates.Take(100).ToArray();
                targets.Add(new JObject
                {
                    ["asset"] = AssetToolSupport.AssetJson(path),
                    ["directDependents"] = new JArray(directDependents),
                    ["dependentsTruncated"] = dependentCandidates.Length > directDependents.Length,
                    ["scanTruncated"] = candidates.Length < allCandidates.Length,
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
        public string Description => "Deletes exactly the supplied Assets/ paths after destructive capability confirmation. All paths and editability are preflighted before the batch delete begins.";
        public JObject InputSchema => AssetToolSupport.PathsSchema();
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["deleted"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
        }, "deleted");
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var requestedPaths = ((JArray)args["paths"]).Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var normalizedPaths = new List<string>();
            var preflightFailures = new JArray();
            foreach (var requestedPath in requestedPaths)
            {
                if (!AssetToolSupport.TryAssetPath(requestedPath, true, out var path, out var error))
                {
                    preflightFailures.Add(new JObject { ["path"] = requestedPath, ["error"] = error });
                    continue;
                }
                if (!AssetDatabase.IsOpenForEdit(path, out var editMessage, StatusQueryOptions.ForceUpdate))
                {
                    preflightFailures.Add(new JObject { ["path"] = path, ["error"] = editMessage });
                    continue;
                }
                normalizedPaths.Add(path);
            }
            if (preflightFailures.Count > 0)
                return ToolResult.Error("DELETE_PREFLIGHT_FAILED", "no assets were deleted because one or more paths failed preflight", new JObject
                {
                    ["requested"] = new JArray(requestedPaths),
                    ["failed"] = preflightFailures,
                });

            var failedPaths = new List<string>();
            var success = AssetDatabase.DeleteAssets(normalizedPaths.ToArray(), failedPaths);
            var failedSet = new HashSet<string>(failedPaths, StringComparer.OrdinalIgnoreCase);
            var deleted = new JArray(normalizedPaths.Where(path => !failedSet.Contains(path)));
            if (!success)
                return ToolResult.Error("PARTIAL_DELETE", $"deleted {deleted.Count}; failed {failedPaths.Count}", new JObject
                {
                    ["deleted"] = deleted,
                    ["failed"] = new JArray(failedPaths),
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
                ["assetPath"] = AssetToolSupport.AssetPathSchema(),
                ["maxProperties"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 1000 },
            },
            ["required"] = new JArray { "assetPath" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["assetPath"] = new JObject { ["type"] = "string" },
            ["importerType"] = new JObject { ["type"] = "string" },
            ["propertyCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["properties"] = new JObject { ["type"] = "object" },
            ["truncated"] = new JObject { ["type"] = "boolean" },
        }, "assetPath", "importerType", "propertyCount", "properties", "truncated");
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
        public string Description => "Validates importer property paths and values, then previews the normalized before/after values without applying them to the importer.";
        public JObject InputSchema => ImportSettingsSchema();
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["asset"] = AssetToolSupport.AssetSchema,
            ["importerType"] = new JObject { ["type"] = "string" },
            ["before"] = new JObject { ["type"] = "object" },
            ["after"] = new JObject { ["type"] = "object" },
            ["nextTool"] = new JObject { ["type"] = "string", ["const"] = "apply_import_settings" },
        }, "asset", "importerType", "before", "after", "nextTool");
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["assetPath"], true, out var path, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return ToolResult.Error("IMPORTER_NOT_FOUND", $"no importer for '{path}'");
            var serialized = new SerializedObject(importer);
            var changes = args["changes"] as JObject ?? new JObject();
            if (changes.Count == 0) return ToolResult.Error("BAD_ARGS", "changes must contain at least one importer property");
            var before = new JObject();
            var after = new JObject();
            try
            {
                foreach (var change in changes.Properties())
                {
                    var property = serialized.FindProperty(change.Name);
                    if (property == null) return ToolResult.Error("PROPERTY_NOT_FOUND", $"importer property not found: {change.Name}");
                    before[change.Name] = SerializedValueCodec.Read(property);
                    SerializedValueCodec.Apply(property, change.Value);
                    after[change.Name] = SerializedValueCodec.Read(property);
                }
            }
            catch (Exception exception)
            {
                return ToolResult.Error("INVALID_IMPORT_VALUE", exception.Message);
            }
            finally
            {
                serialized.Update();
            }
            return ToolResult.Ok($"would change {changes.Count} importer propert{(changes.Count == 1 ? "y" : "ies")}", new JObject
            {
                ["asset"] = AssetToolSupport.AssetJson(path),
                ["importerType"] = importer.GetType().FullName,
                ["before"] = before,
                ["after"] = after,
                ["nextTool"] = "apply_import_settings",
            });
        }

        internal static JObject ImportSettingsSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetPath"] = AssetToolSupport.AssetPathSchema(),
                ["changes"] = new JObject { ["type"] = "object", ["minProperties"] = 1 },
                ["reimport"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "assetPath", "changes" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class ApplyImportSettingsTool : IMcpTool
    {
        public string Name => "apply_import_settings";
        public string Description => "Applies serialized importer property changes with best-effort rollback on failure; optional explicit reimport.";
        public JObject InputSchema => PreviewImportSettingsTool.ImportSettingsSchema();
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["asset"] = AssetToolSupport.AssetSchema,
            ["importerType"] = new JObject { ["type"] = "string" },
            ["before"] = new JObject { ["type"] = "object" },
            ["changes"] = new JObject { ["type"] = "object" },
            ["appliedProperties"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            ["reimported"] = new JObject { ["type"] = "boolean" },
            ["savedAssets"] = new JObject { ["type"] = "boolean" },
        }, "asset", "importerType", "before", "changes", "appliedProperties", "reimported", "savedAssets");
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!AssetToolSupport.TryAssetPath((string)args["assetPath"], true, out var path, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
            try
            {
                var result = AssetToolSupport.ApplyImporterChanges(path, args["changes"] as JObject ?? new JObject(), (bool?)args["reimport"] ?? true);
                return ToolResult.Ok($"applied import settings to '{path}'", result);
            }
            catch (AssetToolSupport.ImporterApplyException exception)
            {
                return ToolResult.Error("IMPORT_SETTINGS_FAILED", exception.Message, new JObject
                {
                    ["assetPath"] = path,
                    ["rolledBack"] = exception.RolledBack,
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("IMPORT_SETTINGS_FAILED", exception.Message, new JObject
                {
                    ["assetPath"] = path,
                    ["rolledBack"] = false,
                });
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
        public JObject OutputSchema => AssetToolSupport.JobOutput;
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var requestedPaths = ((JArray)args["paths"]).Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var normalizedPaths = new List<string>();
            foreach (var requested in requestedPaths)
            {
                if (!AssetToolSupport.TryAssetPath(requested, true, out var path, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
                if (AssetDatabase.IsValidFolder(path)) return ToolResult.Error("BAD_ARGS", $"reimport_assets requires asset files, not folders: {path}");
                normalizedPaths.Add(path);
            }
            var paths = normalizedPaths.ToArray();
            var jobInput = (JObject)args.DeepClone();
            jobInput["paths"] = new JArray(paths);
            var job = ToolJobManager.Create("asset_reimport", jobInput);
            EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
            {
                var completed = new JArray();
                for (int index = 0; index < paths.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var path = paths[index];
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
        public JObject OutputSchema => AssetToolSupport.JobOutput;
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
            ["properties"] = new JObject { ["jobId"] = new JObject { ["type"] = "string", ["minLength"] = 1 } },
            ["required"] = new JArray { "jobId" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class CancelReimportJobTool : IMcpTool
    {
        public string Name => "cancel_reimport_job";
        public string Description => "Requests cancellation between assets in a reimport job. An import already executing on Unity's main thread cannot be interrupted; cancellation takes effect before the next asset.";
        public JObject InputSchema => GetReimportJobTool.JobSchema();
        public JObject OutputSchema => AssetToolSupport.JobOutput;
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
        public string Description => "Reports AssetDatabase editability for canonical project asset paths without requiring a provider-specific VCS API. ForceUpdate is accurate but may be slow.";
        public JObject InputSchema => AssetToolSupport.PathsSchema();
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["statuses"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
        }, "statuses");
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var statuses = new JArray();
            foreach (var requested in ((JArray)args["paths"]).Values<string>())
            {
                if (!AssetToolSupport.TryAssetPath(requested, false, out var path, out var pathError))
                    return ToolResult.Error("BAD_ARGS", pathError);
                bool editable = AssetDatabase.IsOpenForEdit(path, out var message, StatusQueryOptions.ForceUpdate);
                statuses.Add(new JObject
                {
                    ["path"] = path,
                    ["exists"] = AssetDatabase.AssetPathExists(path),
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
        public string Description => "Requests checkout/editability for existing assets through AssetDatabase.MakeEditable without showing an interactive prompt.";
        public JObject InputSchema => AssetToolSupport.PathsSchema();
        public JObject OutputSchema => AssetToolSupport.ObjectSchema(new JObject
        {
            ["editable"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            ["notEditable"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
        }, "editable", "notEditable");
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var requestedPaths = ((JArray)args["paths"]).Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var normalizedPaths = new List<string>();
            foreach (var requested in requestedPaths)
            {
                if (!AssetToolSupport.TryAssetPath(requested, true, out var path, out var error)) return ToolResult.Error("ASSET_NOT_FOUND", error);
                normalizedPaths.Add(path);
            }
            var paths = normalizedPaths.ToArray();
            var notEditable = new List<string>();
            bool success = AssetDatabase.MakeEditable(paths, null, notEditable);
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
