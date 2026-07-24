using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Optional.ShaderGraph
{
    internal static class ShaderGraphToolSupport
    {
        public const int MaxSourceBytes = 8 * 1024 * 1024;

        public static bool TryPath(string requested, bool mustExist, out string path, out string absolute, out string error)
        {
            path = (requested ?? string.Empty).Replace('\\', '/').Trim();
            absolute = null;
            error = null;
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || (!path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase))
                || path.Contains("../") || path.EndsWith("/..", StringComparison.Ordinal))
            {
                error = "assetPath must be an Assets/*.shadergraph or Assets/*.shadersubgraph path";
                return false;
            }
            var root = Path.GetFullPath(Bootstrap.UnityProjectRoot);
            absolute = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "assetPath escapes the project root";
                return false;
            }
            if (mustExist && !File.Exists(absolute))
            {
                error = "Shader Graph source not found: " + path;
                return false;
            }
            return true;
        }

        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static bool TryParseSource(string source, out JObject metadata, out string error)
        {
            metadata = new JObject
            {
                ["sourceValid"] = false,
                ["objectRecordCount"] = 0,
                ["typeRecordCount"] = 0,
            };
            error = null;
            if (string.IsNullOrWhiteSpace(source))
            {
                error = "source is empty";
                metadata["sourceError"] = error;
                return false;
            }

            try
            {
                int objectCount = 0;
                int typeCount = 0;
                int objectIdCount = 0;
                string rootType = null;
                bool legacySource = false;
                var objectIds = new HashSet<string>(StringComparer.Ordinal);
                using (var text = new StringReader(source))
                using (var reader = new JsonTextReader(text) { SupportMultipleContent = true, DateParseHandling = DateParseHandling.None })
                {
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.Comment) continue;
                        if (reader.TokenType != JsonToken.StartObject)
                            throw new JsonReaderException($"record {objectCount + 1} must be a JSON object");
                        var record = JObject.Load(reader);
                        objectCount++;
                        var type = (string)record["m_Type"];
                        var objectId = (string)record["m_ObjectId"];
                        if (!string.IsNullOrWhiteSpace(type)) typeCount++;
                        if (objectCount == 1)
                        {
                            rootType = type;
                            legacySource = string.IsNullOrWhiteSpace(type) && record["m_SerializableNodes"] is JArray;
                            if (!legacySource && !string.Equals(type, "UnityEditor.ShaderGraph.GraphData", StringComparison.Ordinal))
                                throw new JsonReaderException("first record must be UnityEditor.ShaderGraph.GraphData or a legacy graph record");
                        }
                        else if (string.IsNullOrWhiteSpace(type))
                        {
                            throw new JsonReaderException($"record {objectCount} has no m_Type");
                        }

                        if (!legacySource)
                        {
                            if (string.IsNullOrWhiteSpace(objectId))
                                throw new JsonReaderException($"record {objectCount} has no m_ObjectId");
                            if (!objectIds.Add(objectId))
                                throw new JsonReaderException($"record {objectCount} repeats m_ObjectId '{objectId}'");
                            objectIdCount++;
                        }
                    }
                }

                if (objectCount == 0) throw new JsonReaderException("source contains no JSON object records");
                metadata["sourceValid"] = true;
                metadata["objectRecordCount"] = objectCount;
                metadata["typeRecordCount"] = typeCount;
                metadata["objectIdRecordCount"] = objectIdCount;
                metadata["rootType"] = rootType;
                return true;
            }
            catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException)
            {
                error = exception.Message;
                metadata["sourceError"] = error;
                return false;
            }
        }

        public static JArray ImportMessages(string path, out int errorCount, out int warningCount)
        {
            errorCount = 0;
            warningCount = 0;
            var messages = new JArray();
            var log = AssetImporter.GetImportLog(path);
            if (log == null) return messages;
            foreach (var entry in log.logEntries)
            {
                var severity = entry.flags.ToString();
                if (severity.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0) errorCount++;
                else if (severity.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) warningCount++;
                messages.Add(new JObject
                {
                    ["severity"] = severity,
                    ["message"] = entry.message,
                    ["file"] = entry.file,
                    ["line"] = entry.line,
                    ["context"] = ToolTargetRef.Capture(entry.context)?.ToJson(),
                });
            }
            return messages;
        }

        public static JArray ShaderMessages(Shader shader, out int errorCount, out int warningCount)
        {
            errorCount = 0;
            warningCount = 0;
            var messages = new JArray();
            if (shader == null) return messages;
            foreach (var entry in ShaderUtil.GetShaderMessages(shader))
            {
                var severity = entry.severity.ToString();
                if (string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase)) errorCount++;
                else if (string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)) warningCount++;
                messages.Add(new JObject
                {
                    ["severity"] = severity,
                    ["message"] = entry.message,
                    ["details"] = entry.messageDetails,
                    ["file"] = entry.file,
                    ["line"] = entry.line,
                    ["platform"] = entry.platform.ToString(),
                });
            }
            return messages;
        }

        public static JObject Inspect(string path, string absolute, bool includeSource, int maxCharacters)
        {
            var info = new FileInfo(absolute);
            if (info.Length > MaxSourceBytes) throw new InvalidOperationException($"Shader Graph exceeds {MaxSourceBytes} bytes");
            var bytes = File.ReadAllBytes(absolute);
            var source = Encoding.UTF8.GetString(bytes);
            TryParseSource(source, out var sourceMetadata, out _);
            var imported = AssetDatabase.LoadMainAssetAtPath(path);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            var importMessages = ImportMessages(path, out var importErrorCount, out var importWarningCount);
            var shaderMessages = ShaderMessages(shader, out var shaderErrorCount, out var shaderWarningCount);
            var json = new JObject
            {
                ["assetPath"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["bytes"] = bytes.Length,
                ["sha256"] = Hash(bytes),
                ["importedAsset"] = ToolTargetRef.Capture(imported)?.ToJson(),
                ["shader"] = ToolTargetRef.Capture(shader)?.ToJson(),
                ["shaderSupported"] = shader != null ? shader.isSupported : (JToken)null,
                ["importMessages"] = importMessages,
                ["importErrorCount"] = importErrorCount,
                ["importWarningCount"] = importWarningCount,
                ["shaderMessages"] = shaderMessages,
                ["shaderErrorCount"] = shaderErrorCount,
                ["shaderWarningCount"] = shaderWarningCount,
            };
            foreach (var property in sourceMetadata.Properties()) json[property.Name] = property.Value.DeepClone();
            json["looksLikeMultiJson"] = json["sourceValid"];
            if (includeSource)
            {
                var limit = Math.Max(1, Math.Min(500000, maxCharacters));
                json["source"] = source.Length <= limit ? source : source.Substring(0, limit);
                json["sourceCharacters"] = source.Length;
                json["truncated"] = source.Length > limit;
            }
            return json;
        }

        public static string[] Paths() => AssetDatabase.FindAssets("t:Shader")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Concat(AssetDatabase.FindAssets("t:DefaultAsset").Select(AssetDatabase.GUIDToAssetPath))
            .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            .Where(path => path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public sealed class GetShaderGraphContextTool : IMcpTool
    {
        public string Name => "get_shader_graph_context";
        public string Description => "Reports Shader Graph package and project graph counts.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments)
        {
            var paths = ShaderGraphToolSupport.Paths();
            return ToolResult.Ok("Shader Graph context", new JObject
            {
                ["package"] = OptionalToolSupport.PackageJson("com.unity.shadergraph"),
                ["graphCount"] = paths.Count(path => path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)),
                ["subGraphCount"] = paths.Count(path => path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase)),
            });
        }
    }

    public sealed class ListShaderGraphAssetsTool : IMcpTool
    {
        public string Name => "list_shader_graph_assets";
        public string Description => "Lists Shader Graph and Shader Sub Graph source assets with import status.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments)
        {
            var items = new JArray();
            foreach (var path in ShaderGraphToolSupport.Paths())
            {
                if (!ShaderGraphToolSupport.TryPath(path, true, out var normalized, out var absolute, out _)) continue;
                items.Add(ShaderGraphToolSupport.Inspect(normalized, absolute, false, 0));
            }
            return ToolResult.Ok($"{items.Count} Shader Graph asset(s)", new JObject { ["assets"] = items });
        }
    }

    public sealed class ReadShaderGraphSourceTool : IMcpTool
    {
        public string Name => "read_shader_graph_source";
        public string Description => "Reads bounded Shader Graph MultiJson source and import metadata.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["maxCharacters"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500000, ["default"] = 100000 },
            },
            ["required"] = new JArray { "assetPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments)
        {
            if (!ShaderGraphToolSupport.TryPath((string)arguments["assetPath"], true, out var path, out var absolute, out var error))
                return ToolResult.Error("SHADER_GRAPH_NOT_FOUND", error);
            try { return ToolResult.Ok($"read '{path}'", ShaderGraphToolSupport.Inspect(path, absolute, true, (int?)arguments["maxCharacters"] ?? 100000)); }
            catch (Exception exception) { return ToolResult.Error("READ_SHADER_GRAPH_FAILED", exception.Message); }
        }
    }

    public sealed class ValidateShaderGraphAssetsTool : IMcpTool
    {
        public string Name => "validate_shader_graph_assets";
        public string Description => "Validates graph source shape, importability and generated Shader support.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments)
        {
            var issues = new JArray();
            var paths = ShaderGraphToolSupport.Paths();
            foreach (var path in paths)
            {
                if (!ShaderGraphToolSupport.TryPath(path, true, out var normalized, out var absolute, out var pathError))
                {
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "BAD_GRAPH_PATH", ["assetPath"] = path, ["message"] = pathError });
                    continue;
                }
                try
                {
                    var info = ShaderGraphToolSupport.Inspect(normalized, absolute, false, 0);
                    if (!(bool)info["sourceValid"])
                        issues.Add(new JObject
                        {
                            ["severity"] = "error", ["code"] = "INVALID_SHADER_GRAPH_SOURCE",
                            ["assetPath"] = path, ["message"] = info["sourceError"],
                        });
                    if (info["importedAsset"] == null || info["importedAsset"].Type == JTokenType.Null || (int)info["importErrorCount"] > 0)
                        issues.Add(new JObject
                        {
                            ["severity"] = "error", ["code"] = "SHADER_GRAPH_IMPORT_FAILED",
                            ["assetPath"] = path, ["messages"] = info["importMessages"].DeepClone(),
                        });
                    if ((int)info["shaderErrorCount"] > 0)
                        issues.Add(new JObject
                        {
                            ["severity"] = "error", ["code"] = "SHADER_COMPILATION_FAILED",
                            ["assetPath"] = path, ["messages"] = info["shaderMessages"].DeepClone(),
                        });
                    if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) && (bool?)info["shaderSupported"] == false)
                        issues.Add(new JObject { ["severity"] = "error", ["code"] = "GENERATED_SHADER_UNSUPPORTED", ["assetPath"] = path });
                }
                catch (Exception exception)
                {
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "SHADER_GRAPH_READ_FAILED", ["assetPath"] = path, ["message"] = exception.Message });
                }
            }
            return ToolResult.Ok($"{issues.Count} Shader Graph issue(s)", new JObject
            {
                ["valid"] = !issues.Any(token => string.Equals((string)token["severity"], "error", StringComparison.Ordinal)),
                ["assetCount"] = paths.Length,
                ["issues"] = issues,
            });
        }
    }

    public sealed class WriteShaderGraphSourceTool : IMcpTool
    {
        public string Name => "write_shader_graph_source";
        public string Description => "Replaces existing Shader Graph MultiJson source, an unsupported internal format, with verified rollback on failed import. Requires explicit overwrite and confirmation.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["source"] = new JObject { ["type"] = "string", ["minLength"] = 2, ["maxLength"] = 8388608 },
                ["overwrite"] = new JObject { ["type"] = "boolean" },
                ["expectedSha256"] = new JObject { ["type"] = "string", ["minLength"] = 64, ["maxLength"] = 64 },
            },
            ["required"] = new JArray { "assetPath", "source", "overwrite", "expectedSha256" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "shadergraph",
            Capabilities = McpCapability.AssetWrite | McpCapability.Destructive,
            Risk = McpRiskLevel.Critical,
            ConfirmationPolicy = McpConfirmationPolicy.Required,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = false,
        };

        public ToolResult Execute(JObject arguments)
        {
            if (!(bool)arguments["overwrite"]) return ToolResult.Error("OVERWRITE_REQUIRED", "overwrite must be true");
            if (!ShaderGraphToolSupport.TryPath((string)arguments["assetPath"], true, out var path, out var absolute, out var pathError))
                return ToolResult.Error("SHADER_GRAPH_NOT_FOUND", pathError);
            var original = File.ReadAllBytes(absolute);
            var currentHash = ShaderGraphToolSupport.Hash(original);
            if (!string.Equals(currentHash, (string)arguments["expectedSha256"], StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error("SOURCE_CHANGED", "expectedSha256 does not match current source", new JObject { ["currentSha256"] = currentHash });
            var source = (string)arguments["source"];
            if (!ShaderGraphToolSupport.TryParseSource(source, out var sourceMetadata, out var sourceError))
                return ToolResult.Error("INVALID_SHADER_GRAPH_SOURCE", sourceError, sourceMetadata);
            var next = new UTF8Encoding(false).GetBytes(source);
            if (next.Length > ShaderGraphToolSupport.MaxSourceBytes)
                return ToolResult.Error("INVALID_SHADER_GRAPH_SOURCE", $"source exceeds {ShaderGraphToolSupport.MaxSourceBytes} bytes");

            JObject originalInspection;
            try { originalInspection = ShaderGraphToolSupport.Inspect(path, absolute, false, 0); }
            catch (Exception exception)
            {
                return ToolResult.Error("SHADER_GRAPH_INSPECTION_FAILED", exception.Message);
            }

            try
            {
                File.WriteAllBytes(absolute, next);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var imported = AssetDatabase.LoadMainAssetAtPath(path);
                var inspection = ShaderGraphToolSupport.Inspect(path, absolute, false, 0);
                if (imported == null || (int)inspection["importErrorCount"] > 0
                    || (int)inspection["shaderErrorCount"] > 0
                    || (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) && (bool?)inspection["shaderSupported"] == false))
                    throw new InvalidOperationException("Unity failed to import and compile the replacement Shader Graph source");
                return ToolResult.Ok($"updated '{path}'", new JObject
                {
                    ["beforeSha256"] = currentHash,
                    ["afterSha256"] = ShaderGraphToolSupport.Hash(next),
                    ["asset"] = ToolTargetRef.Capture(imported)?.ToJson(),
                    ["sourceValid"] = true,
                    ["objectRecordCount"] = sourceMetadata["objectRecordCount"],
                    ["importErrorCount"] = inspection["importErrorCount"],
                    ["shaderErrorCount"] = inspection["shaderErrorCount"],
                    ["savedAssets"] = true,
                    ["undoRegistered"] = false,
                });
            }
            catch (Exception exception)
            {
                JObject failedInspection;
                try { failedInspection = ShaderGraphToolSupport.Inspect(path, absolute, false, 0); }
                catch (Exception inspectionException)
                {
                    failedInspection = new JObject { ["inspectionError"] = inspectionException.Message };
                }

                var rollback = new JObject
                {
                    ["attempted"] = true,
                    ["actionCompleted"] = false,
                    ["verified"] = false,
                    ["expectedSha256"] = currentHash,
                    ["originalImportErrorCount"] = originalInspection["importErrorCount"].DeepClone(),
                    ["originalShaderErrorCount"] = originalInspection["shaderErrorCount"].DeepClone(),
                };
                var errorData = new JObject
                {
                    ["writeError"] = exception.Message,
                    ["failedInspection"] = failedInspection,
                    ["rollback"] = rollback,
                };

                try
                {
                    var currentBytes = File.ReadAllBytes(absolute);
                    if (!string.Equals(ShaderGraphToolSupport.Hash(currentBytes), currentHash, StringComparison.OrdinalIgnoreCase))
                        File.WriteAllBytes(absolute, original);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    rollback["actionCompleted"] = true;
                }
                catch (Exception rollbackException)
                {
                    rollback["actionError"] = rollbackException.Message;
                }

                try
                {
                    var restored = File.ReadAllBytes(absolute);
                    var restoredHash = ShaderGraphToolSupport.Hash(restored);
                    var restoredInspection = ShaderGraphToolSupport.Inspect(path, absolute, false, 0);
                    var hashMatches = string.Equals(restoredHash, currentHash, StringComparison.OrdinalIgnoreCase);
                    var importErrorsMatch = (int)restoredInspection["importErrorCount"] == (int)originalInspection["importErrorCount"];
                    var shaderErrorsMatch = (int)restoredInspection["shaderErrorCount"] == (int)originalInspection["shaderErrorCount"];
                    var originalImported = originalInspection["importedAsset"] != null && originalInspection["importedAsset"].Type != JTokenType.Null;
                    var restoredImported = restoredInspection["importedAsset"] != null && restoredInspection["importedAsset"].Type != JTokenType.Null;
                    var importedStateMatches = originalImported == restoredImported;
                    var shaderSupportedMatches = JToken.DeepEquals(originalInspection["shaderSupported"], restoredInspection["shaderSupported"]);
                    var verified = (bool)rollback["actionCompleted"] && hashMatches && importErrorsMatch
                        && shaderErrorsMatch && importedStateMatches && shaderSupportedMatches;
                    rollback["restoredSha256"] = restoredHash;
                    rollback["hashMatches"] = hashMatches;
                    rollback["importErrorsMatch"] = importErrorsMatch;
                    rollback["shaderErrorsMatch"] = shaderErrorsMatch;
                    rollback["importedStateMatches"] = importedStateMatches;
                    rollback["shaderSupportedMatches"] = shaderSupportedMatches;
                    rollback["restoredInspection"] = restoredInspection;
                    rollback["verified"] = verified;
                }
                catch (Exception verificationException)
                {
                    rollback["verificationError"] = verificationException.Message;
                }

                if (!(bool)rollback["verified"])
                    return ToolResult.Error("ROLLBACK_FAILED", "Replacement failed and the original Shader Graph state could not be verified after rollback", errorData);
                return ToolResult.Error("WRITE_SHADER_GRAPH_FAILED", exception.Message, errorData);
            }
        }
    }
}
