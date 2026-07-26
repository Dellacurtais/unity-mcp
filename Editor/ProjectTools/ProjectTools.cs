using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.ProjectTools
{
    internal static class ProjectToolSchemas
    {
        public static JObject Empty() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public static JObject SettingsChange() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["changes"] = new JObject
                {
                    ["type"] = "object", ["minProperties"] = 1,
                    ["properties"] = new JObject
                    {
                        ["companyName"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
                        ["productName"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
                        ["bundleVersion"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
                        ["defaultScreenWidth"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 16384 },
                        ["defaultScreenHeight"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 16384 },
                        ["runInBackground"] = new JObject { ["type"] = "boolean" },
                        ["colorSpace"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Gamma", "Linear" } },
                    },
                    ["additionalProperties"] = false,
                },
            },
            ["required"] = new JArray { "changes" },
            ["additionalProperties"] = false,
        };

        public static JObject EditorContext() => Object(new JObject
        {
            ["unityVersion"] = String(),
            ["platform"] = String(),
            ["projectRoot"] = String(),
            ["isPlaying"] = Boolean(),
            ["isPaused"] = Boolean(),
            ["isCompiling"] = Boolean(),
            ["isUpdating"] = Boolean(),
            ["activeScene"] = Object(new JObject
            {
                ["name"] = String(), ["path"] = String(), ["handle"] = Integer(), ["isDirty"] = Boolean(),
            }, "name", "path", "handle", "isDirty"),
            ["selection"] = NullableObject(),
            ["registryCount"] = Integer(),
            ["registryHash"] = NullableString(),
            ["requestId"] = NullableString(),
            ["protocolVersion"] = NullableString(),
            ["deadlineUtc"] = NullableString(),
        }, "unityVersion", "platform", "projectRoot", "isPlaying", "isPaused", "isCompiling", "isUpdating", "activeScene", "selection", "registryCount", "registryHash", "requestId", "protocolVersion", "deadlineUtc");

        public static JObject ProjectSettings() => Object(new JObject
        {
            ["companyName"] = String(), ["productName"] = String(), ["bundleVersion"] = String(),
            ["defaultScreenWidth"] = Integer(), ["defaultScreenHeight"] = Integer(), ["runInBackground"] = Boolean(),
            ["colorSpace"] = String(), ["desiredColorSpace"] = String(), ["activeColorSpace"] = String(),
            ["activeBuildTarget"] = String(), ["renderPipeline"] = String(),
            ["defaultRenderPipeline"] = NullableObject(), ["qualityRenderPipelineOverride"] = NullableObject(),
            ["activeRenderPipeline"] = NullableObject(), ["qualityLevel"] = Integer(), ["qualityName"] = NullableString(),
        }, "companyName", "productName", "bundleVersion", "defaultScreenWidth", "defaultScreenHeight", "runInBackground",
            "colorSpace", "desiredColorSpace", "activeColorSpace", "activeBuildTarget", "renderPipeline",
            "defaultRenderPipeline", "qualityRenderPipelineOverride", "activeRenderPipeline", "qualityLevel", "qualityName");

        public static JObject TagsLayers() => Object(new JObject
        {
            ["tags"] = Array(String()), ["layers"] = Array(Object(new JObject { ["index"] = Integer(), ["name"] = String() }, "index", "name")),
            ["sortingLayers"] = Array(Object(new JObject { ["id"] = Integer(), ["value"] = Integer(), ["name"] = String() }, "id", "value", "name")),
        }, "tags", "layers", "sortingLayers");

        public static JObject SettingsChangeResult() => Object(new JObject
        {
            ["before"] = ProjectSettings(), ["after"] = ProjectSettings(), ["current"] = ProjectSettings(),
            ["changedKeys"] = Array(String()), ["effectiveChangedKeys"] = Array(String()),
            ["warning"] = String(), ["colorSpaceMayReimport"] = Boolean(),
            ["applied"] = Boolean(), ["rolledBack"] = Boolean(),
        }, "before", "after", "changedKeys", "effectiveChangedKeys", "warning", "colorSpaceMayReimport");

        public static JObject QualityLevels() => Object(new JObject
        {
            ["activeIndex"] = Integer(), ["activeName"] = NullableString(),
            ["defaultRenderPipeline"] = NullableObject(), ["activeRenderPipeline"] = NullableObject(),
            ["levels"] = Array(Object(new JObject
            {
                ["index"] = Integer(), ["name"] = String(), ["active"] = Boolean(), ["renderPipelineOverride"] = NullableObject(),
            }, "index", "name", "active", "renderPipelineOverride")),
            ["vSyncCount"] = Integer(), ["antiAliasing"] = Integer(), ["shadowDistance"] = Number(),
        }, "activeIndex", "activeName", "defaultRenderPipeline", "activeRenderPipeline", "levels", "vSyncCount", "antiAliasing", "shadowDistance");

        public static JObject QualityChangeResult() => Object(new JObject
        {
            ["before"] = QualityState(), ["current"] = QualityState(),
            ["changedKeys"] = Array(String()), ["applied"] = Boolean(), ["rolledBack"] = Boolean(),
        }, "before", "current", "changedKeys", "applied", "rolledBack");

        private static JObject QualityState() => Object(new JObject
        {
            ["activeIndex"] = Integer(), ["activeName"] = NullableString(),
            ["defaultRenderPipeline"] = NullableObject(), ["renderPipelineOverride"] = NullableObject(), ["activeRenderPipeline"] = NullableObject(),
            ["vSyncCount"] = Integer(), ["antiAliasing"] = Integer(), ["shadowDistance"] = Number(),
        }, "activeIndex", "activeName", "defaultRenderPipeline", "renderPipelineOverride", "activeRenderPipeline", "vSyncCount", "antiAliasing", "shadowDistance");

        private static JObject Object(JObject properties, params string[] required) => new JObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JArray(required ?? System.Array.Empty<string>()), ["additionalProperties"] = false,
        };
        private static JObject NullableObject() => new JObject { ["type"] = new JArray { "object", "null" } };
        private static JObject String() => new JObject { ["type"] = "string" };
        private static JObject NullableString() => new JObject { ["type"] = new JArray { "string", "null" } };
        private static JObject Integer() => new JObject { ["type"] = "integer" };
        private static JObject Number() => new JObject { ["type"] = "number" };
        private static JObject Boolean() => new JObject { ["type"] = "boolean" };
        private static JObject Array(JObject items) => new JObject { ["type"] = "array", ["items"] = items };

        public static JObject CompilationStatus() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["isCompiling"] = new JObject { ["type"] = "boolean" },
                ["isUpdating"] = new JObject { ["type"] = "boolean" },
                ["isPlayingOrWillChangePlaymode"] = new JObject { ["type"] = "boolean" },
                ["bufferDomainId"] = new JObject { ["type"] = "string" },
                ["consoleSequence"] = new JObject { ["type"] = "integer" },
                ["bufferSize"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "isCompiling", "isUpdating", "isPlayingOrWillChangePlaymode", "bufferDomainId", "consoleSequence", "bufferSize" },
            ["additionalProperties"] = false,
        };

        public static JObject CompilationDiagnostic() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["source"] = new JObject { ["type"] = "string" },
                ["seq"] = new JObject { ["type"] = new JArray { "integer", "null" } },
                ["severity"] = new JObject { ["type"] = "string" },
                ["message"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["file"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["line"] = new JObject { ["type"] = "integer" },
                ["column"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "source", "seq", "severity", "message", "file", "line", "column" },
            ["additionalProperties"] = false,
        };

        public static JObject CompilationDiagnostics() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["isCompiling"] = new JObject { ["type"] = "boolean" },
                ["source"] = new JObject { ["type"] = "string" },
                ["anchorValid"] = new JObject { ["type"] = "boolean" },
                ["bufferDomainId"] = new JObject { ["type"] = "string" },
                ["nextSeq"] = new JObject { ["type"] = "integer" },
                ["count"] = new JObject { ["type"] = "integer" },
                ["truncated"] = new JObject { ["type"] = "boolean" },
                ["diagnostics"] = new JObject { ["type"] = "array", ["items"] = CompilationDiagnostic() },
            },
            ["required"] = new JArray { "isCompiling", "source", "anchorValid", "bufferDomainId", "nextSeq", "count", "truncated", "diagnostics" },
            ["additionalProperties"] = false,
        };

        public static JObject CompilationRequest() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["requested"] = new JObject { ["type"] = "boolean" },
                ["diagnosticsSinceSeq"] = new JObject { ["type"] = "integer" },
                ["bufferDomainId"] = new JObject { ["type"] = "string" },
                ["anchorSurvivesReload"] = new JObject { ["type"] = "boolean" },
                ["serverMayReload"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "requested", "diagnosticsSinceSeq", "bufferDomainId", "anchorSurvivesReload", "serverMayReload" },
            ["additionalProperties"] = false,
        };
    }

    internal static class ProjectSettingsService
    {
        private static readonly string[] WritableKeys =
        {
            "companyName", "productName", "bundleVersion", "defaultScreenWidth",
            "defaultScreenHeight", "runInBackground", "colorSpace",
        };

        public static JObject Snapshot()
        {
            var currentPipeline = GraphicsSettings.currentRenderPipeline;
            var qualityPipeline = QualitySettings.renderPipeline;
            var defaultPipeline = GraphicsSettings.defaultRenderPipeline;
            var qualityLevel = QualitySettings.GetQualityLevel();
            return new JObject
            {
                ["companyName"] = PlayerSettings.companyName,
                ["productName"] = PlayerSettings.productName,
                ["bundleVersion"] = PlayerSettings.bundleVersion,
                ["defaultScreenWidth"] = PlayerSettings.defaultScreenWidth,
                ["defaultScreenHeight"] = PlayerSettings.defaultScreenHeight,
                ["runInBackground"] = PlayerSettings.runInBackground,
                ["colorSpace"] = PlayerSettings.colorSpace.ToString(),
                ["desiredColorSpace"] = QualitySettings.desiredColorSpace.ToString(),
                ["activeColorSpace"] = QualitySettings.activeColorSpace.ToString(),
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["renderPipeline"] = currentPipeline != null ? currentPipeline.GetType().FullName : "BuiltIn",
                ["defaultRenderPipeline"] = PipelineJson(defaultPipeline),
                ["qualityRenderPipelineOverride"] = PipelineJson(qualityPipeline),
                ["activeRenderPipeline"] = PipelineJson(currentPipeline),
                ["qualityLevel"] = qualityLevel,
                ["qualityName"] = QualitySettings.names.ElementAtOrDefault(qualityLevel),
            };
        }

        public static bool ValidateChanges(JObject changes, out JObject normalized, out string error)
        {
            normalized = new JObject();
            if (changes == null || changes.Count == 0) { error = "NO_CHANGES: changes must contain at least one whitelisted setting"; return false; }
            foreach (var property in changes.Properties())
            {
                if (!WritableKeys.Contains(property.Name)) { error = $"BAD_ARGS: unsupported project setting '{property.Name}'"; return false; }
                var token = property.Value;
                switch (property.Name)
                {
                    case "companyName":
                    case "productName":
                    case "bundleVersion":
                        var text = token.Type == JTokenType.String ? (string)token : null;
                        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024)
                        { error = $"BAD_ARGS: {property.Name} must be non-empty and at most 1024 characters"; return false; }
                        normalized[property.Name] = text;
                        break;
                    case "defaultScreenWidth":
                    case "defaultScreenHeight":
                        if (token.Type != JTokenType.Integer || (long)token < 1 || (long)token > 16384)
                        { error = $"BAD_ARGS: {property.Name} must be an integer in 1..16384"; return false; }
                        normalized[property.Name] = (int)token;
                        break;
                    case "runInBackground":
                        if (token.Type != JTokenType.Boolean)
                        { error = "BAD_ARGS: runInBackground must be boolean"; return false; }
                        normalized[property.Name] = (bool)token;
                        break;
                    case "colorSpace":
                        if (token.Type != JTokenType.String
                            || !Enum.TryParse((string)token, true, out ColorSpace colorSpace)
                            || (colorSpace != ColorSpace.Gamma && colorSpace != ColorSpace.Linear))
                        { error = "BAD_ARGS: colorSpace must be Gamma or Linear"; return false; }
                        normalized[property.Name] = colorSpace.ToString();
                        break;
                }
            }
            error = null;
            return true;
        }

        public static JObject Preview(JObject normalized)
        {
            var before = Snapshot();
            var after = (JObject)before.DeepClone();
            foreach (var property in normalized.Properties()) after[property.Name] = property.Value.DeepClone();
            var effective = normalized.Properties()
                .Where(property => !JToken.DeepEquals(before[property.Name], property.Value))
                .Select(property => property.Name).ToArray();
            var colorSpaceMayReimport = effective.Contains("colorSpace");
            return new JObject
            {
                ["before"] = before,
                ["after"] = after,
                ["changedKeys"] = new JArray(normalized.Properties().Select(property => property.Name)),
                ["effectiveChangedKeys"] = new JArray(effective),
                ["warning"] = colorSpaceMayReimport
                    ? "PlayerSettings.colorSpace may trigger asset reimport. ProjectSettings APIs persist immediately; apply requires confirmation."
                    : "ProjectSettings APIs persist immediately. Apply requires ProjectSettings capability and confirmation.",
                ["colorSpaceMayReimport"] = colorSpaceMayReimport,
                ["applied"] = false,
                ["rolledBack"] = false,
            };
        }

        public static bool TryApply(JObject normalized, out JObject result, out string error)
        {
            result = Preview(normalized);
            var before = (JObject)result["before"];
            var effectiveKeys = ((JArray)result["effectiveChangedKeys"]).Values<string>().ToArray();
            var effectiveChanges = new JObject(normalized.Properties()
                .Where(property => effectiveKeys.Contains(property.Name))
                .Select(property => new JProperty(property.Name, property.Value.DeepClone())));
            if (effectiveChanges.Count == 0)
            {
                result["current"] = Snapshot();
                error = null;
                return true;
            }
            try
            {
                ApplyValues(effectiveChanges);
                result["applied"] = true;
                result["current"] = Snapshot();
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                try { Restore(before); result["rolledBack"] = true; }
                catch { result["rolledBack"] = false; }
                result["current"] = Snapshot();
                error = $"{exception.GetType().Name}: {exception.Message}";
                return false;
            }
        }

        public static JObject PipelineJson(RenderPipelineAsset pipeline)
        {
            if (pipeline == null) return null;
            var path = AssetDatabase.GetAssetPath(pipeline);
            return new JObject
            {
                ["name"] = pipeline.name,
                ["type"] = pipeline.GetType().FullName,
                ["assetPath"] = string.IsNullOrEmpty(path) ? null : path,
                ["assetGuid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
            };
        }

        private static void ApplyValues(JObject changes)
        {
            if (changes["companyName"] != null) PlayerSettings.companyName = (string)changes["companyName"];
            if (changes["productName"] != null) PlayerSettings.productName = (string)changes["productName"];
            if (changes["bundleVersion"] != null) PlayerSettings.bundleVersion = (string)changes["bundleVersion"];
            if (changes["defaultScreenWidth"] != null) PlayerSettings.defaultScreenWidth = (int)changes["defaultScreenWidth"];
            if (changes["defaultScreenHeight"] != null) PlayerSettings.defaultScreenHeight = (int)changes["defaultScreenHeight"];
            if (changes["runInBackground"] != null) PlayerSettings.runInBackground = (bool)changes["runInBackground"];
            if (changes["colorSpace"] != null) PlayerSettings.colorSpace = (ColorSpace)Enum.Parse(typeof(ColorSpace), (string)changes["colorSpace"]);
        }

        private static void Restore(JObject before)
        {
            PlayerSettings.companyName = (string)before["companyName"];
            PlayerSettings.productName = (string)before["productName"];
            PlayerSettings.bundleVersion = (string)before["bundleVersion"];
            PlayerSettings.defaultScreenWidth = (int)before["defaultScreenWidth"];
            PlayerSettings.defaultScreenHeight = (int)before["defaultScreenHeight"];
            PlayerSettings.runInBackground = (bool)before["runInBackground"];
            PlayerSettings.colorSpace = (ColorSpace)Enum.Parse(typeof(ColorSpace), (string)before["colorSpace"]);
        }
    }

    internal static class QualitySettingsService
    {
        public static JObject Snapshot()
        {
            var index = QualitySettings.GetQualityLevel();
            return new JObject
            {
                ["activeIndex"] = index,
                ["activeName"] = QualitySettings.names.ElementAtOrDefault(index),
                ["defaultRenderPipeline"] = ProjectSettingsService.PipelineJson(GraphicsSettings.defaultRenderPipeline),
                ["renderPipelineOverride"] = ProjectSettingsService.PipelineJson(QualitySettings.renderPipeline),
                ["activeRenderPipeline"] = ProjectSettingsService.PipelineJson(GraphicsSettings.currentRenderPipeline),
                ["vSyncCount"] = QualitySettings.vSyncCount,
                ["antiAliasing"] = QualitySettings.antiAliasing,
                ["shadowDistance"] = QualitySettings.shadowDistance,
            };
        }

        public static void RestoreQualitySettings(JObject before, int targetIndex, JObject targetBefore)
        {
            if (targetBefore != null)
            {
                QualitySettings.SetQualityLevel(targetIndex, true);
                RestoreProperties(targetBefore);
            }
            QualitySettings.SetQualityLevel((int)before["activeIndex"], true);
            RestoreProperties(before);
        }

        private static void RestoreProperties(JObject snapshot)
        {
            QualitySettings.vSyncCount = (int)snapshot["vSyncCount"];
            QualitySettings.antiAliasing = (int)snapshot["antiAliasing"];
            QualitySettings.shadowDistance = (float)snapshot["shadowDistance"];
        }
    }

    public sealed class GetEditorContextTool : IMcpTool
    {
        public string Name => "get_editor_context";
        public string Description => "Returns Unity editor, play mode, compilation, scene, selection, registry and MCP request context.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public JObject OutputSchema => ProjectToolSchemas.EditorContext();
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var context = ToolExecutionContext.Current;
            var selection = Selection.activeObject;
            return ToolResult.Ok("editor context captured", new JObject
            {
                ["unityVersion"] = Application.unityVersion,
                ["platform"] = Application.platform.ToString(),
                ["projectRoot"] = Bootstrap.UnityProjectRoot,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["activeScene"] = new JObject
                {
                    ["name"] = activeScene.name,
                    ["path"] = activeScene.path,
                    ["handle"] = (int)activeScene.handle,
                    ["isDirty"] = activeScene.isDirty,
                },
                ["selection"] = ToolTargetRef.Capture(selection)?.ToJson(),
                ["registryCount"] = Bootstrap.ToolRegistry?.Count ?? 0,
                ["registryHash"] = Bootstrap.ToolRegistry?.VersionHash,
                ["requestId"] = context?.RequestId,
                ["protocolVersion"] = context?.ProtocolVersion,
                ["deadlineUtc"] = context?.DeadlineUtc.ToString("O"),
            });
        }
    }

    public sealed class GetProjectSettingsTool : IMcpTool
    {
        public string Name => "get_project_settings";
        public string Description => "Reads a stable subset of Player, build target, render pipeline and quality settings.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public JObject OutputSchema => ProjectToolSchemas.ProjectSettings();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => ToolResult.Ok("project settings read", ProjectSettingsService.Snapshot());
    }

    public sealed class GetCompilationStatusTool : IMcpTool
    {
        public string Name => "get_compilation_status";
        public string Description => "Returns current compilation/update/play-transition state and a domain-bound console sequence anchor.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public JObject OutputSchema => ProjectToolSchemas.CompilationStatus();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => ToolResult.Ok(
            EditorApplication.isCompiling ? "compiling" : "not compiling",
            new JObject
            {
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode,
                ["bufferDomainId"] = ConsoleCapture.DomainId,
                ["consoleSequence"] = ConsoleCapture.CurrentSeq,
                ["bufferSize"] = ConsoleCapture.BufferSize,
            });
    }

    public sealed class GetCompilationDiagnosticsTool : IMcpTool
    {
        public string Name => "get_compilation_diagnostics";
        public string Description => "Returns domain-bound live compiler diagnostics or a persistent Unity Console fallback when no delta anchor is supplied.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sinceSeq"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["bufferDomainId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 64 },
                ["includeWarnings"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 200 },
            },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => ProjectToolSchemas.CompilationDiagnostics();
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var sinceSeq = (int?)args["sinceSeq"];
            var domainId = (string)args["bufferDomainId"];
            var includeWarnings = (bool?)args["includeWarnings"] ?? true;
            var limit = (int?)args["limit"] ?? 200;
            if (limit < 1 || limit > 1000)
                return ToolResult.Error("BAD_ARGS", "limit must be in 1..1000");
            if (sinceSeq.HasValue && string.IsNullOrWhiteSpace(domainId))
                return ToolResult.Error("BAD_ARGS", "bufferDomainId is required with sinceSeq because sequence numbers reset on domain reload");
            if (sinceSeq.HasValue && !ConsoleCapture.IsCurrentDomain(domainId))
                return ToolResult.Error("STALE_CONSOLE_ANCHOR", "bufferDomainId belongs to a previous domain reload; request a new compilation anchor");

            var diagnostics = new JArray();
            var source = "buffer";
            var candidateLimit = Math.Min(1000, limit + 1);
            if (sinceSeq.HasValue || !UnityConsoleReader.IsAvailable)
            {
                var entries = ConsoleCapture.SnapshotCompilerDiagnostics(
                    sinceSeq,
                    includeWarnings ? LogType.Warning : LogType.Error,
                    candidateLimit);
                foreach (var entry in entries)
                {
                    diagnostics.Add(new JObject
                    {
                        ["source"] = "buffer",
                        ["seq"] = entry.Seq,
                        ["severity"] = entry.Severity.ToString(),
                        ["message"] = entry.Message,
                        ["file"] = entry.SourceFile,
                        ["line"] = entry.SourceLine,
                        ["column"] = entry.SourceColumn,
                    });
                }
            }
            else
            {
                source = "unity";
                var severity = includeWarnings ? "warnings_and_errors" : "errors_only";
                foreach (var item in UnityConsoleReader.ReadCompilerDiagnostics(severity, candidateLimit))
                {
                    diagnostics.Add(new JObject
                    {
                        ["source"] = "unity",
                        ["seq"] = null,
                        ["severity"] = item.Severity,
                        ["message"] = item.Message,
                        ["file"] = item.File,
                        ["line"] = item.Line,
                        ["column"] = 0,
                    });
                }
            }

            var truncated = diagnostics.Count > limit;
            if (truncated)
            {
                while (diagnostics.Count > limit) diagnostics.RemoveAt(0);
            }
            return ToolResult.Ok($"{diagnostics.Count} compilation diagnostic(s)", new JObject
            {
                ["isCompiling"] = EditorApplication.isCompiling,
                ["source"] = source,
                ["anchorValid"] = !sinceSeq.HasValue || ConsoleCapture.IsCurrentDomain(domainId),
                ["bufferDomainId"] = ConsoleCapture.DomainId,
                ["nextSeq"] = ConsoleCapture.CurrentSeq,
                ["count"] = diagnostics.Count,
                ["truncated"] = truncated,
                ["diagnostics"] = diagnostics,
            });
        }
    }

    public sealed class RequestScriptCompilationTool : IMcpTool
    {
        public string Name => "request_script_compilation";
        public string Description => "Requests Unity script compilation from stable Edit Mode without waiting for completion or saving unrelated assets.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public JObject OutputSchema => ProjectToolSchemas.CompilationRequest();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (EditorApplication.isCompiling)
                return ToolResult.Error("EDITOR_BUSY", "Unity is already compiling scripts");
            if (EditorApplication.isUpdating)
                return ToolResult.Error("EDITOR_BUSY", "Unity is importing or updating assets");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying)
                return ToolResult.Error("EDITOR_BUSY", "script compilation must be requested from stable Edit Mode");

            var anchor = ConsoleCapture.CurrentSeq;
            var domainId = ConsoleCapture.DomainId;
            CompilationPipeline.RequestScriptCompilation();
            return ToolResult.Ok("script compilation requested", new JObject
            {
                ["requested"] = true,
                ["diagnosticsSinceSeq"] = anchor,
                ["bufferDomainId"] = domainId,
                ["anchorSurvivesReload"] = false,
                ["serverMayReload"] = true,
            });
        }
    }

    public sealed class ListTagsLayersTool : IMcpTool
    {
        public string Name => "list_tags_layers";
        public string Description => "Lists project tags, named layers and sorting layers.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public JObject OutputSchema => ProjectToolSchemas.TagsLayers();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var layers = new JArray();
            for (int index = 0; index < 32; index++)
            {
                var name = LayerMask.LayerToName(index);
                if (!string.IsNullOrEmpty(name)) layers.Add(new JObject { ["index"] = index, ["name"] = name });
            }
            var sortingLayers = new JArray(SortingLayer.layers.Select(layer => new JObject
            {
                ["id"] = layer.id,
                ["value"] = layer.value,
                ["name"] = layer.name,
            }));
            return ToolResult.Ok("tags and layers listed", new JObject
            {
                ["tags"] = new JArray(InternalEditorUtility.tags),
                ["layers"] = layers,
                ["sortingLayers"] = sortingLayers,
            });
        }
    }

    public sealed class PreviewProjectSettingsChangeTool : IMcpTool
    {
        public string Name => "preview_project_settings_change";
        public string Description => "Previews whitelisted PlayerSettings changes without mutating the project.";
        public JObject InputSchema => ProjectToolSchemas.SettingsChange();
        public JObject OutputSchema => ProjectToolSchemas.SettingsChangeResult();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            if (!ProjectSettingsService.ValidateChanges(args?["changes"] as JObject, out var changes, out var error))
                return ToolResult.Error(error.StartsWith("NO_CHANGES", StringComparison.Ordinal) ? "NO_CHANGES" : "BAD_ARGS", error);
            var preview = ProjectSettingsService.Preview(changes);
            return ToolResult.Ok($"would effectively change {preview["effectiveChangedKeys"].Count()} project setting(s)", preview);
        }
    }

    public sealed class ApplyProjectSettingsChangeTool : IMcpTool
    {
        public string Name => "apply_project_settings_change";
        public string Description => "Applies whitelisted PlayerSettings changes after capability authorization and confirmation.";
        public JObject InputSchema => ProjectToolSchemas.SettingsChange();
        public JObject OutputSchema => ProjectToolSchemas.SettingsChangeResult();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!ProjectSettingsService.ValidateChanges(args?["changes"] as JObject, out var changes, out var validationError))
                return ToolResult.Error(validationError.StartsWith("NO_CHANGES", StringComparison.Ordinal) ? "NO_CHANGES" : "BAD_ARGS", validationError);
            if (!ProjectSettingsService.TryApply(changes, out var result, out var applyError))
                return ToolResult.Error("PROJECT_SETTINGS_APPLY_FAILED", applyError, result);
            return ToolResult.Ok($"applied {result["effectiveChangedKeys"].Count()} effective project setting change(s)", result);
        }
    }

    public sealed class ListQualityLevelsTool : IMcpTool
    {
        public string Name => "list_quality_levels";
        public string Description => "Lists Unity quality levels and the active level.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public JObject OutputSchema => ProjectToolSchemas.QualityLevels();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var current = QualitySettings.GetQualityLevel();
            var levels = new JArray();
            for (int index = 0; index < QualitySettings.names.Length; index++)
                levels.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = QualitySettings.names[index],
                    ["active"] = index == current,
                    ["renderPipelineOverride"] = ProjectSettingsService.PipelineJson(QualitySettings.GetRenderPipelineAssetAt(index)),
                });
            return ToolResult.Ok($"{levels.Count} quality level(s)", new JObject
            {
                ["activeIndex"] = current,
                ["activeName"] = QualitySettings.names.ElementAtOrDefault(current),
                ["defaultRenderPipeline"] = ProjectSettingsService.PipelineJson(GraphicsSettings.defaultRenderPipeline),
                ["activeRenderPipeline"] = ProjectSettingsService.PipelineJson(GraphicsSettings.currentRenderPipeline),
                ["levels"] = levels,
                ["vSyncCount"] = QualitySettings.vSyncCount,
                ["antiAliasing"] = QualitySettings.antiAliasing,
                ["shadowDistance"] = QualitySettings.shadowDistance,
            });
        }
    }

    public sealed class SetQualitySettingsTool : IMcpTool
    {
        public string Name => "set_quality_settings";
        public string Description => "Sets the active quality level and selected global quality properties.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["levelIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["levelName"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
                ["applyExpensiveChanges"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["vSyncCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 4 },
                ["antiAliasing"] = new JObject { ["type"] = "integer", ["enum"] = new JArray { 0, 2, 4, 8 } },
                ["shadowDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
            },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => ProjectToolSchemas.QualityChangeResult();
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            if (args == null) return ToolResult.Error("QUALITY_CHANGE_REQUIRED", "provide a quality level or at least one quality property");
            var allowed = new[] { "levelIndex", "levelName", "applyExpensiveChanges", "vSyncCount", "antiAliasing", "shadowDistance" };
            var unknown = args.Properties().FirstOrDefault(property => !allowed.Contains(property.Name));
            if (unknown != null) return ToolResult.Error("BAD_ARGS", $"unsupported quality setting '{unknown.Name}'");
            var hasIndex = args["levelIndex"] != null;
            var hasName = args["levelName"] != null;
            if (hasIndex && hasName)
                return ToolResult.Error("AMBIGUOUS_QUALITY_LEVEL", "provide levelIndex or levelName, not both");
            if (!hasIndex && !hasName && args["vSyncCount"] == null && args["antiAliasing"] == null && args["shadowDistance"] == null)
                return ToolResult.Error("QUALITY_CHANGE_REQUIRED", "provide a quality level or at least one quality property");
            if (hasIndex && args["levelIndex"].Type != JTokenType.Integer)
                return ToolResult.Error("BAD_ARGS", "levelIndex must be an integer");
            if (hasName && (args["levelName"].Type != JTokenType.String
                || string.IsNullOrWhiteSpace((string)args["levelName"])
                || ((string)args["levelName"]).Length > 1024))
                return ToolResult.Error("BAD_ARGS", "levelName must be a non-empty string of at most 1024 characters");
            if (args["applyExpensiveChanges"] != null && args["applyExpensiveChanges"].Type != JTokenType.Boolean)
                return ToolResult.Error("BAD_ARGS", "applyExpensiveChanges must be boolean");

            var index = (int?)args["levelIndex"] ?? -1;
            var name = (string)args["levelName"];
            if (hasName)
                index = Array.FindIndex(QualitySettings.names, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            if ((hasIndex && (index < 0 || index >= QualitySettings.names.Length)) || (hasName && index < 0))
                return ToolResult.Error("BAD_ARGS", hasName ? $"quality level '{name}' was not found" : $"quality level index {index} is out of range");
            if (args["vSyncCount"] != null
                && (args["vSyncCount"].Type != JTokenType.Integer || (int)args["vSyncCount"] < 0 || (int)args["vSyncCount"] > 4))
                return ToolResult.Error("BAD_ARGS", "vSyncCount must be an integer in 0..4");
            if (args["antiAliasing"] != null)
            {
                if (args["antiAliasing"].Type != JTokenType.Integer)
                    return ToolResult.Error("BAD_ARGS", "antiAliasing must be one of 0, 2, 4 or 8");
                var antiAliasing = (int)args["antiAliasing"];
                if (antiAliasing != 0 && antiAliasing != 2 && antiAliasing != 4 && antiAliasing != 8)
                    return ToolResult.Error("BAD_ARGS", "antiAliasing must be one of 0, 2, 4 or 8");
            }
            if (args["shadowDistance"] != null)
            {
                if (args["shadowDistance"].Type != JTokenType.Integer && args["shadowDistance"].Type != JTokenType.Float)
                    return ToolResult.Error("BAD_ARGS", "shadowDistance must be a finite non-negative number");
                var distance = (float)args["shadowDistance"];
                if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
                    return ToolResult.Error("BAD_ARGS", "shadowDistance must be a finite non-negative number");
            }

            var before = QualitySettingsService.Snapshot();
            JObject targetBefore = null;
            var changedKeys = new JArray(args.Properties()
                .Where(property => property.Name != "applyExpensiveChanges")
                .Select(property => property.Name));
            var result = new JObject
            {
                ["before"] = before,
                ["changedKeys"] = changedKeys,
                ["applied"] = false,
                ["rolledBack"] = false,
            };
            try
            {
                if (index >= 0)
                {
                    QualitySettings.SetQualityLevel(index, (bool?)args["applyExpensiveChanges"] ?? true);
                    targetBefore = QualitySettingsService.Snapshot();
                }
                if (args["vSyncCount"] != null) QualitySettings.vSyncCount = (int)args["vSyncCount"];
                if (args["antiAliasing"] != null) QualitySettings.antiAliasing = (int)args["antiAliasing"];
                if (args["shadowDistance"] != null) QualitySettings.shadowDistance = (float)args["shadowDistance"];
                result["applied"] = true;
                result["current"] = QualitySettingsService.Snapshot();
                return ToolResult.Ok("quality settings applied", result);
            }
            catch (Exception exception)
            {
                try { QualitySettingsService.RestoreQualitySettings(before, index, targetBefore); result["rolledBack"] = true; }
                catch { result["rolledBack"] = false; }
                result["current"] = QualitySettingsService.Snapshot();
                return ToolResult.Error("QUALITY_SETTINGS_APPLY_FAILED", exception.Message, result);
            }
        }
    }
}
