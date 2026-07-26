using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Maicon.SceneAnalyzer.Editor.BuildTools
{
    internal static class BuildToolSupport
    {
        public static JObject OutputSchema(JObject properties) => new JObject
        {
            ["type"] = "object",
            ["properties"] = properties ?? new JObject(),
            ["additionalProperties"] = true,
        };

        public static JObject JobOutput => OutputSchema(new JObject
        {
            ["jobId"] = new JObject { ["type"] = "string" },
            ["kind"] = new JObject { ["type"] = "string" },
            ["status"] = new JObject { ["type"] = "string" },
            ["progress"] = new JObject { ["type"] = "number" },
            ["message"] = new JObject { ["type"] = new JArray { "string", "null" } },
            ["error"] = new JObject { ["type"] = new JArray { "string", "null" } },
            ["result"] = new JObject(),
            ["canCancel"] = new JObject { ["type"] = "boolean" },
        });

        public static JObject BuildSchema(bool requireOutput) => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["locationPathName"] = new JObject { ["type"] = "string" },
                ["target"] = new JObject { ["type"] = "string", ["default"] = EditorUserBuildSettings.activeBuildTarget.ToString() },
                ["scenes"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                    ["maxItems"] = 1000,
                },
                ["development"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["detailedReport"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["cleanBuildCache"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = requireOutput ? new JArray { "locationPathName" } : new JArray(),
            ["additionalProperties"] = false,
        };

        public static string[] ResolveScenes(JObject args, out string error)
        {
            error = null;
            string[] scenes;
            if (args["scenes"] is JArray supplied && supplied.Count > 0)
            {
                if (supplied.Count > 1000) { error = "scenes supports at most 1000 entries"; return null; }
                scenes = supplied.Values<string>().Select(path => path?.Trim()).ToArray();
                if (scenes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scenes.Length)
                { error = "scenes contains duplicate paths"; return null; }
            }
            else
                scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (scenes.Length == 0) { error = "no scenes are configured for the build"; return null; }
            foreach (var path in scenes)
            {
                if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                {
                    error = $"build scene not found: {path}";
                    return null;
                }
            }
            return scenes;
        }

        public static bool TryBuildTarget(JObject args, out BuildTarget target, out string error)
        {
            error = null;
            var text = (string)args["target"] ?? EditorUserBuildSettings.activeBuildTarget.ToString();
            if (!Enum.TryParse(text, true, out target) || !Enum.IsDefined(typeof(BuildTarget), target)
                || target == BuildTarget.NoTarget)
            {
                error = $"unknown BuildTarget '{text}'";
                return false;
            }
            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                error = $"BuildTarget '{target}' is not installed/supported in this Editor";
                return false;
            }
            return true;
        }

        public static bool TryOutputPath(string value, out string relative, out string absolute, out string error)
        {
            relative = (value ?? string.Empty).Replace('\\', '/').Trim();
            absolute = null;
            error = null;
            if (string.IsNullOrEmpty(relative)) { error = "locationPathName is required"; return false; }
            if (Path.IsPathRooted(relative)) { error = "locationPathName must be project-relative"; return false; }
            if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("../") || relative.EndsWith("/..", StringComparison.Ordinal))
            {
                error = "build output must stay inside the project and outside Assets/Packages";
                return false;
            }
            var root = Path.GetFullPath(Bootstrap.UnityProjectRoot);
            absolute = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "build output escapes the project root";
                return false;
            }
            return true;
        }

        public static BuildOptions Options(JObject args)
        {
            var options = BuildOptions.None;
            if ((bool?)args["development"] ?? false) options |= BuildOptions.Development;
            if ((bool?)args["detailedReport"] ?? true) options |= BuildOptions.DetailedBuildReport;
            if ((bool?)args["cleanBuildCache"] ?? false) options |= BuildOptions.CleanBuildCache;
            return options;
        }

        public static JObject Validate(JObject args, bool requireOutput)
        {
            var issues = new JArray();
            var scenes = ResolveScenes(args, out var sceneError);
            if (sceneError != null) issues.Add(new JObject { ["severity"] = "error", ["code"] = "SCENES_INVALID", ["message"] = sceneError });
            var targetValid = TryBuildTarget(args, out var target, out var targetError);
            if (!targetValid)
                issues.Add(new JObject { ["severity"] = "error", ["code"] = "TARGET_INVALID", ["message"] = targetError });
            else if (target != EditorUserBuildSettings.activeBuildTarget)
                issues.Add(new JObject
                {
                    ["severity"] = "error", ["code"] = "TARGET_NOT_ACTIVE",
                    ["message"] = $"requested target '{target}' differs from active target '{EditorUserBuildSettings.activeBuildTarget}'; switch target and wait for domain reload before building"
                });
            if (EditorApplication.isCompiling)
                issues.Add(new JObject { ["severity"] = "error", ["code"] = "COMPILING", ["message"] = "Editor is compiling" });
            if (EditorApplication.isUpdating)
                issues.Add(new JObject { ["severity"] = "error", ["code"] = "ASSET_UPDATE_IN_PROGRESS", ["message"] = "AssetDatabase/package import is updating" });
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                issues.Add(new JObject { ["severity"] = "error", ["code"] = "EDIT_MODE_REQUIRED", ["message"] = "Player builds must be started from stable Edit Mode" });
            if (BuildPipeline.isBuildingPlayer)
                issues.Add(new JObject { ["severity"] = "error", ["code"] = "BUILD_IN_PROGRESS", ["message"] = "Unity is already building" });

            string relative = null;
            string absolute = null;
            if (requireOutput || args["locationPathName"] != null)
            {
                if (!TryOutputPath((string)args["locationPathName"], out relative, out absolute, out var outputError))
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "OUTPUT_INVALID", ["message"] = outputError });
            }

            return new JObject
            {
                ["valid"] = !issues.Any(token => (string)token["severity"] == "error"),
                ["issues"] = issues,
                ["target"] = targetValid ? (JToken)target.ToString() : JValue.CreateNull(),
                ["targetGroup"] = targetValid ? (JToken)BuildPipeline.GetBuildTargetGroup(target).ToString() : JValue.CreateNull(),
                ["scenes"] = scenes != null ? new JArray(scenes) : new JArray(),
                ["locationPathName"] = relative,
                ["absoluteOutputPath"] = absolute,
                ["outputExists"] = absolute != null && (File.Exists(absolute) || Directory.Exists(absolute)),
                ["options"] = Options(args).ToString(),
            };
        }

        public static JObject ReportJson(BuildReport report, bool includeSteps, int maxSteps = 100, int maxMessagesPerStep = 100)
        {
            if (report == null) return new JObject { ["available"] = false };
            var summary = report.summary;
            var json = new JObject
            {
                ["available"] = true,
                ["result"] = summary.result.ToString(),
                ["platform"] = summary.platform.ToString(),
                ["platformGroup"] = summary.platformGroup.ToString(),
                ["outputPath"] = summary.outputPath,
                ["totalSize"] = summary.totalSize,
                ["totalTimeSeconds"] = summary.totalTime.TotalSeconds,
                ["totalErrors"] = summary.totalErrors,
                ["totalWarnings"] = summary.totalWarnings,
                ["guid"] = summary.guid.ToString(),
                ["errors"] = report.SummarizeErrors(),
            };
            if (includeSteps)
            {
                maxSteps = Math.Max(1, Math.Min(1000, maxSteps));
                maxMessagesPerStep = Math.Max(1, Math.Min(1000, maxMessagesPerStep));
                var allSteps = report.steps;
                var steps = new JArray();
                foreach (var step in allSteps.Take(maxSteps))
                {
                    var allMessages = step.messages ?? Array.Empty<BuildStepMessage>();
                    var messages = new JArray(allMessages.Take(maxMessagesPerStep).Select(message => new JObject
                    {
                        ["type"] = message.type.ToString(),
                        ["content"] = message.content,
                    }));
                    steps.Add(new JObject
                    {
                        ["name"] = step.name,
                        ["depth"] = step.depth,
                        ["durationSeconds"] = step.duration.TotalSeconds,
                        ["messages"] = messages,
                        ["messageCount"] = allMessages.Length,
                        ["messagesTruncated"] = allMessages.Length > maxMessagesPerStep,
                    });
                }
                json["steps"] = steps;
                json["stepCount"] = allSteps.Length;
                json["stepsTruncated"] = allSteps.Length > maxSteps;
            }
            return json;
        }
    }

    public sealed class ListBuildProfilesTool : IMcpTool
    {
        public string Name => "list_build_profiles";
        public string Description => "Lists active build target, EditorBuildSettings scenes and BuildProfile assets without requiring direct BuildProfile API references.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false };
        public bool IsWrite => false;
        public JObject OutputSchema => BuildToolSupport.OutputSchema(new JObject
        {
            ["activeBuildTarget"] = new JObject { ["type"] = "string" },
            ["profiles"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["buildSettingsScenes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
        });
        public ToolResult Execute(JObject args)
        {
            var profiles = new JArray(AssetDatabase.FindAssets("t:BuildProfile").OrderBy(guid => guid, StringComparer.Ordinal).Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                return new JObject
                {
                    ["guid"] = guid,
                    ["path"] = path,
                    ["name"] = asset != null ? asset.name : null,
                    ["type"] = asset != null ? asset.GetType().FullName : null,
                };
            }));
            var scenes = new JArray(EditorBuildSettings.scenes.Select((scene, index) => new JObject
            {
                ["index"] = index,
                ["path"] = scene.path,
                ["guid"] = AssetDatabase.AssetPathToGUID(scene.path),
                ["enabled"] = scene.enabled,
                ["exists"] = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) != null,
            }));
            return ToolResult.Ok($"{profiles.Count} BuildProfile asset(s)", new JObject
            {
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["profiles"] = profiles,
                ["buildSettingsScenes"] = scenes,
            });
        }
    }

    public sealed class ValidateBuildTool : IMcpTool
    {
        public string Name => "validate_build";
        public string Description => "Validates target support, scene list, compilation state and optional output path without building.";
        public JObject InputSchema => BuildToolSupport.BuildSchema(false);
        public bool IsWrite => false;
        public JObject OutputSchema => BuildToolSupport.OutputSchema(new JObject
        {
            ["valid"] = new JObject { ["type"] = "boolean" },
            ["issues"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["target"] = new JObject { ["type"] = new JArray { "string", "null" } },
            ["scenes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
        });
        public ToolResult Execute(JObject args)
        {
            var validation = BuildToolSupport.Validate(args, false);
            return (bool)validation["valid"]
                ? ToolResult.Ok("build configuration is valid", validation)
                : ToolResult.Error("BUILD_INVALID", "build configuration has errors", validation);
        }
    }

    public sealed class PreviewBuildTool : IMcpTool
    {
        public string Name => "preview_build";
        public string Description => "Previews the exact BuildPlayerOptions and requires confirmation before start_build.";
        public JObject InputSchema => BuildToolSupport.BuildSchema(true);
        public bool IsWrite => false;
        public JObject OutputSchema => BuildToolSupport.OutputSchema(new JObject
        {
            ["valid"] = new JObject { ["type"] = "boolean" },
            ["issues"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["requiresConfirmation"] = new JObject { ["type"] = "boolean" },
            ["nextTool"] = new JObject { ["type"] = "string" },
        });
        public ToolResult Execute(JObject args)
        {
            var preview = BuildToolSupport.Validate(args, true);
            preview["requiresConfirmation"] = true;
            preview["nextTool"] = "start_build";
            preview["warning"] = "BuildPipeline.BuildPlayer is synchronous on the Unity main thread and cannot be cancelled after it starts.";
            return (bool)preview["valid"]
                ? ToolResult.Ok("build preview ready", preview)
                : ToolResult.Error("BUILD_INVALID", "build preview has errors", preview);
        }
    }

    public sealed class StartBuildTool : IMcpTool
    {
        public string Name => "start_build";
        public string Description => "Starts a confirmed player build job. The request returns immediately; Unity may become busy while BuildPipeline runs.";
        public JObject InputSchema => BuildToolSupport.BuildSchema(true);
        public bool IsWrite => true;
        public JObject OutputSchema => BuildToolSupport.JobOutput;
        public ToolResult Execute(JObject args)
        {
            var validation = BuildToolSupport.Validate(args, true);
            if (!(bool)validation["valid"])
                return ToolResult.Error("BUILD_INVALID", "build configuration has errors", validation);
            if (!ToolJobManager.TryCreateExclusive("build", args, canCancel: true, out var job))
                return ToolResult.Error("BUILD_JOB_ACTIVE", "another build job is already queued or running");
            EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
            {
                token.ThrowIfCancellationRequested();
                ToolJobManager.Report(record, 0.05, "waiting for Unity main thread");
                var report = await MainThreadDispatcher.RunAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var currentValidation = BuildToolSupport.Validate(args, true);
                    if (!(bool)currentValidation["valid"])
                        throw new InvalidOperationException("build configuration became invalid before execution: " + currentValidation["issues"]);
                    lock (record.Sync)
                    {
                        record.CanCancel = false;
                        record.Message = "BuildPipeline is running; cancellation is no longer supported";
                    }
                    var scenes = BuildToolSupport.ResolveScenes(args, out var error);
                    if (scenes == null) throw new InvalidOperationException(error);
                    if (!BuildToolSupport.TryBuildTarget(args, out var target, out error)) throw new InvalidOperationException(error);
                    if (!BuildToolSupport.TryOutputPath((string)args["locationPathName"], out _, out var output, out error))
                        throw new InvalidOperationException(error);
                    var directory = Path.GetDirectoryName(output);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    return BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = scenes,
                        locationPathName = output,
                        target = target,
                        options = BuildToolSupport.Options(args),
                    });
                }, token);
                var result = BuildToolSupport.ReportJson(report, includeSteps: false);
                if (report == null || report.summary.result != BuildResult.Succeeded)
                    throw new ToolJobFailureException(
                        $"BuildPlayer returned {report?.summary.result.ToString() ?? "no report"}", result);
                ToolJobManager.Report(record, 1, "build succeeded");
                return result;
            });
            return ToolResult.Ok($"build job {job.Id} queued", job.ToJson());
        }
    }

    public sealed class GetBuildJobTool : IMcpTool
    {
        public string Name => "get_build_job";
        public string Description => "Returns one build job state and summary when complete.";
        public JObject InputSchema => JobSchema();
        public bool IsWrite => false;
        public JObject OutputSchema => BuildToolSupport.JobOutput;
        public ToolResult Execute(JObject args)
        {
            if (!ToolJobManager.TryGet((string)args["jobId"], out var job) || job.Kind != "build")
                return ToolResult.Error("JOB_NOT_FOUND", "build job not found");
            return ToolResult.Ok($"build job {job.Status}", job.ToJson());
        }

        internal static JObject JobSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["jobId"] = new JObject { ["type"] = "string" } },
            ["required"] = new JArray { "jobId" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class CancelBuildJobTool : IMcpTool
    {
        public string Name => "cancel_build_job";
        public string Description => "Cancels a queued build job before BuildPipeline starts; Unity exposes no safe cancellation once a player build is running.";
        public JObject InputSchema => GetBuildJobTool.JobSchema();
        public bool IsWrite => true;
        public JObject OutputSchema => BuildToolSupport.JobOutput;
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["jobId"];
            if (!ToolJobManager.TryGet(id, out var job) || job.Kind != "build")
                return ToolResult.Error("JOB_NOT_FOUND", "build job not found");
            if (!ToolJobManager.Cancel(id, out job))
                return ToolResult.Error("BUILD_NOT_CANCELLABLE", "build is terminal or BuildPipeline already started", job.ToJson());
            return ToolResult.Ok("build cancellation requested", job.ToJson());
        }
    }

    public sealed class ReadBuildReportTool : IMcpTool
    {
        public string Name => "read_build_report";
        public string Description => "Reads the latest Unity BuildReport, including bounded step messages.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["includeSteps"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["maxSteps"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 100 },
                ["maxMessagesPerStep"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 100 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => BuildToolSupport.OutputSchema(new JObject
        {
            ["available"] = new JObject { ["type"] = "boolean" },
            ["result"] = new JObject { ["type"] = "string" },
            ["steps"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["stepsTruncated"] = new JObject { ["type"] = "boolean" },
        });
        public ToolResult Execute(JObject args)
        {
            var maxSteps = (int?)args["maxSteps"] ?? 100;
            var maxMessages = (int?)args["maxMessagesPerStep"] ?? 100;
            if (maxSteps < 1 || maxSteps > 1000 || maxMessages < 1 || maxMessages > 1000)
                return ToolResult.Error("BAD_LIMIT", "maxSteps and maxMessagesPerStep must be between 1 and 1000");
            var report = BuildReport.GetLatestReport();
            if (report == null) return ToolResult.Error("BUILD_REPORT_NOT_FOUND", "Unity has no latest BuildReport in this editor session");
            var json = BuildToolSupport.ReportJson(
                report, (bool?)args["includeSteps"] ?? true, maxSteps, maxMessages);
            return ToolResult.Ok($"latest build: {report.summary.result}", json);
        }
    }
}
