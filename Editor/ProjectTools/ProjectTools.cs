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
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["companyName"] = new JObject { ["type"] = "string" },
                        ["productName"] = new JObject { ["type"] = "string" },
                        ["bundleVersion"] = new JObject { ["type"] = "string" },
                        ["defaultScreenWidth"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                        ["defaultScreenHeight"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                        ["runInBackground"] = new JObject { ["type"] = "boolean" },
                        ["colorSpace"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Gamma", "Linear" } },
                    },
                    ["additionalProperties"] = false,
                },
            },
            ["required"] = new JArray { "changes" },
            ["additionalProperties"] = false,
        };
    }

    internal static class ProjectSettingsService
    {
        public static JObject Snapshot()
        {
            var currentPipeline = GraphicsSettings.currentRenderPipeline;
            var qualityPipeline = QualitySettings.renderPipeline;
            return new JObject
            {
                ["companyName"] = PlayerSettings.companyName,
                ["productName"] = PlayerSettings.productName,
                ["bundleVersion"] = PlayerSettings.bundleVersion,
                ["defaultScreenWidth"] = PlayerSettings.defaultScreenWidth,
                ["defaultScreenHeight"] = PlayerSettings.defaultScreenHeight,
                ["runInBackground"] = PlayerSettings.runInBackground,
                ["colorSpace"] = PlayerSettings.colorSpace.ToString(),
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["renderPipeline"] = currentPipeline != null
                    ? currentPipeline.GetType().FullName
                    : qualityPipeline != null ? qualityPipeline.GetType().FullName : "BuiltIn",
                ["qualityLevel"] = QualitySettings.GetQualityLevel(),
                ["qualityName"] = QualitySettings.names.ElementAtOrDefault(QualitySettings.GetQualityLevel()),
            };
        }

        public static JObject Preview(JObject changes)
        {
            var before = Snapshot();
            var after = (JObject)before.DeepClone();
            foreach (var property in changes.Properties()) after[property.Name] = property.Value.DeepClone();
            return new JObject
            {
                ["before"] = before,
                ["after"] = after,
                ["changedKeys"] = new JArray(changes.Properties().Select(property => property.Name)),
                ["warning"] = "ProjectSettings APIs persist immediately. Apply requires ProjectSettings capability and confirmation.",
            };
        }

        public static JObject Apply(JObject changes)
        {
            var preview = Preview(changes);
            if (changes["companyName"] != null) PlayerSettings.companyName = (string)changes["companyName"];
            if (changes["productName"] != null) PlayerSettings.productName = (string)changes["productName"];
            if (changes["bundleVersion"] != null) PlayerSettings.bundleVersion = (string)changes["bundleVersion"];
            if (changes["defaultScreenWidth"] != null) PlayerSettings.defaultScreenWidth = (int)changes["defaultScreenWidth"];
            if (changes["defaultScreenHeight"] != null) PlayerSettings.defaultScreenHeight = (int)changes["defaultScreenHeight"];
            if (changes["runInBackground"] != null) PlayerSettings.runInBackground = (bool)changes["runInBackground"];
            if (changes["colorSpace"] != null)
                PlayerSettings.colorSpace = (ColorSpace)Enum.Parse(typeof(ColorSpace), (string)changes["colorSpace"], true);
            preview["applied"] = true;
            preview["current"] = Snapshot();
            return preview;
        }
    }

    public sealed class GetEditorContextTool : IMcpTool
    {
        public string Name => "get_editor_context";
        public string Description => "Returns Unity editor, play mode, compilation, scene, selection, registry and MCP request context.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
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
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => ToolResult.Ok("project settings read", ProjectSettingsService.Snapshot());
    }

    public sealed class GetCompilationStatusTool : IMcpTool
    {
        public string Name => "get_compilation_status";
        public string Description => "Returns current compilation/update state and the console sequence anchor.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => ToolResult.Ok(
            EditorApplication.isCompiling ? "compiling" : "not compiling",
            new JObject
            {
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["consoleSequence"] = ConsoleCapture.CurrentSeq,
                ["bufferSize"] = ConsoleCapture.BufferSize,
            });
    }

    public sealed class GetCompilationDiagnosticsTool : IMcpTool
    {
        public string Name => "get_compilation_diagnostics";
        public string Description => "Returns captured compiler errors and warnings with source locations.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sinceSeq"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["includeWarnings"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 200 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var entries = ConsoleCapture.Snapshot(
                (int?)args["sinceSeq"],
                ((bool?)args["includeWarnings"] ?? true) ? LogType.Warning : LogType.Error,
                null,
                (int?)args["limit"] ?? 200).Where(entry => entry.IsCompile).ToList();
            var diagnostics = new JArray(entries.Select(entry => new JObject
            {
                ["seq"] = entry.Seq,
                ["severity"] = entry.Severity.ToString(),
                ["message"] = entry.Message,
                ["file"] = entry.SourceFile,
                ["line"] = entry.SourceLine,
                ["column"] = entry.SourceColumn,
            }));
            return ToolResult.Ok($"{entries.Count} compilation diagnostic(s)", new JObject
            {
                ["isCompiling"] = EditorApplication.isCompiling,
                ["diagnostics"] = diagnostics,
                ["nextSeq"] = ConsoleCapture.CurrentSeq,
            });
        }
    }

    public sealed class RequestScriptCompilationTool : IMcpTool
    {
        public string Name => "request_script_compilation";
        public string Description => "Requests Unity script compilation without waiting for or saving unrelated assets.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var anchor = ConsoleCapture.CurrentSeq;
            CompilationPipeline.RequestScriptCompilation();
            return ToolResult.Ok("script compilation requested", new JObject
            {
                ["requested"] = true,
                ["diagnosticsSinceSeq"] = anchor,
                ["serverMayReload"] = true,
            });
        }
    }

    public sealed class ListTagsLayersTool : IMcpTool
    {
        public string Name => "list_tags_layers";
        public string Description => "Lists project tags, named layers and sorting layers.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
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
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var changes = args["changes"] as JObject ?? new JObject();
            return ToolResult.Ok($"would change {changes.Count} project setting(s)", ProjectSettingsService.Preview(changes));
        }
    }

    public sealed class ApplyProjectSettingsChangeTool : IMcpTool
    {
        public string Name => "apply_project_settings_change";
        public string Description => "Applies whitelisted PlayerSettings changes after capability authorization and confirmation.";
        public JObject InputSchema => ProjectToolSchemas.SettingsChange();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var changes = args["changes"] as JObject ?? new JObject();
            return ToolResult.Ok($"applied {changes.Count} project setting(s)", ProjectSettingsService.Apply(changes));
        }
    }

    public sealed class ListQualityLevelsTool : IMcpTool
    {
        public string Name => "list_quality_levels";
        public string Description => "Lists Unity quality levels and the active level.";
        public JObject InputSchema => ProjectToolSchemas.Empty();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var current = QualitySettings.GetQualityLevel();
            var levels = new JArray();
            for (int index = 0; index < QualitySettings.names.Length; index++)
                levels.Add(new JObject { ["index"] = index, ["name"] = QualitySettings.names[index], ["active"] = index == current });
            return ToolResult.Ok($"{levels.Count} quality level(s)", new JObject
            {
                ["activeIndex"] = current,
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
                ["levelName"] = new JObject { ["type"] = "string" },
                ["applyExpensiveChanges"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["vSyncCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 4 },
                ["antiAliasing"] = new JObject { ["type"] = "integer", ["enum"] = new JArray { 0, 2, 4, 8 } },
                ["shadowDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var index = (int?)args["levelIndex"] ?? -1;
            var name = (string)args["levelName"];
            if (index < 0 && !string.IsNullOrEmpty(name))
                index = Array.FindIndex(QualitySettings.names, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            if (args["levelIndex"] != null && (index < 0 || index >= QualitySettings.names.Length))
                return ToolResult.Error("BAD_ARGS", $"quality level index {index} is out of range");
            if (!string.IsNullOrEmpty(name) && index < 0)
                return ToolResult.Error("BAD_ARGS", $"quality level '{name}' was not found");
            if (index >= 0) QualitySettings.SetQualityLevel(index, (bool?)args["applyExpensiveChanges"] ?? true);
            if (args["vSyncCount"] != null) QualitySettings.vSyncCount = (int)args["vSyncCount"];
            if (args["antiAliasing"] != null) QualitySettings.antiAliasing = (int)args["antiAliasing"];
            if (args["shadowDistance"] != null) QualitySettings.shadowDistance = (float)args["shadowDistance"];
            return ToolResult.Ok("quality settings applied", new JObject
            {
                ["activeIndex"] = QualitySettings.GetQualityLevel(),
                ["activeName"] = QualitySettings.names.ElementAtOrDefault(QualitySettings.GetQualityLevel()),
                ["vSyncCount"] = QualitySettings.vSyncCount,
                ["antiAliasing"] = QualitySettings.antiAliasing,
                ["shadowDistance"] = QualitySettings.shadowDistance,
            });
        }
    }
}
