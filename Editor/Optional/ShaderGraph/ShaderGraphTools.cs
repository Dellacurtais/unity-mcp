using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

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

        public static JObject Inspect(string path, string absolute, bool includeSource, int maxCharacters)
        {
            var info = new FileInfo(absolute);
            if (info.Length > MaxSourceBytes) throw new InvalidOperationException($"Shader Graph exceeds {MaxSourceBytes} bytes");
            var bytes = File.ReadAllBytes(absolute);
            var source = Encoding.UTF8.GetString(bytes);
            var objectIds = Count(source, "m_ObjectId");
            var types = Count(source, "m_Type");
            var imported = AssetDatabase.LoadMainAssetAtPath(path);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            var json = new JObject
            {
                ["assetPath"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["bytes"] = bytes.Length,
                ["sha256"] = Hash(bytes),
                ["objectRecordCount"] = objectIds,
                ["typeRecordCount"] = types,
                ["looksLikeMultiJson"] = objectIds > 0 && types > 0,
                ["importedAsset"] = ToolTargetRef.Capture(imported)?.ToJson(),
                ["shader"] = ToolTargetRef.Capture(shader)?.ToJson(),
                ["shaderSupported"] = shader != null ? shader.isSupported : (JToken)null,
            };
            if (includeSource)
            {
                var limit = Math.Max(1, Math.Min(500000, maxCharacters));
                json["source"] = source.Length <= limit ? source : source.Substring(0, limit);
                json["sourceCharacters"] = source.Length;
                json["truncated"] = source.Length > limit;
            }
            return json;
        }

        private static int Count(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        public static string[] Paths() => AssetDatabase.FindAssets("t:Shader")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Concat(AssetDatabase.FindAssets("t:DefaultAsset").Select(AssetDatabase.GUIDToAssetPath))
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
                    if (!(bool)info["looksLikeMultiJson"])
                        issues.Add(new JObject { ["severity"] = "error", ["code"] = "INVALID_SHADER_GRAPH_SOURCE", ["assetPath"] = path });
                    if (info["importedAsset"] == null || info["importedAsset"].Type == JTokenType.Null)
                        issues.Add(new JObject { ["severity"] = "error", ["code"] = "SHADER_GRAPH_IMPORT_FAILED", ["assetPath"] = path });
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
        public string Description => "Replaces existing Shader Graph MultiJson source with rollback on failed import. Requires explicit overwrite and confirmation.";
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
            if (source.IndexOf("m_ObjectId", StringComparison.Ordinal) < 0 || source.IndexOf("m_Type", StringComparison.Ordinal) < 0)
                return ToolResult.Error("INVALID_SHADER_GRAPH_SOURCE", "source must contain Shader Graph MultiJson object and type records");
            var next = new UTF8Encoding(false).GetBytes(source);
            try
            {
                File.WriteAllBytes(absolute, next);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var imported = AssetDatabase.LoadMainAssetAtPath(path);
                if (imported == null) throw new InvalidOperationException("Unity failed to import the replacement Shader Graph source");
                return ToolResult.Ok($"updated '{path}'", new JObject
                {
                    ["beforeSha256"] = currentHash,
                    ["afterSha256"] = ShaderGraphToolSupport.Hash(next),
                    ["asset"] = ToolTargetRef.Capture(imported)?.ToJson(),
                    ["savedAssets"] = false,
                });
            }
            catch (Exception exception)
            {
                try { File.WriteAllBytes(absolute, original); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate); } catch { }
                return ToolResult.Error("WRITE_SHADER_GRAPH_FAILED", exception.Message);
            }
        }
    }
}
