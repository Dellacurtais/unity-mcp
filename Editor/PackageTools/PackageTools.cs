using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Maicon.SceneAnalyzer.Editor.PackageTools
{
    internal static class PackageToolSupport
    {
        private static readonly SemaphoreSlim ClientGate = new SemaphoreSlim(1, 1);

        public static JObject OutputSchema(JObject properties) => new JObject
        {
            ["type"] = "object",
            ["properties"] = properties ?? new JObject(),
            ["additionalProperties"] = true,
        };

        public static JObject PackageOutput => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string" },
                ["displayName"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["version"] = new JObject { ["type"] = "string" },
                ["source"] = new JObject { ["type"] = "string" },
                ["isDirectDependency"] = new JObject { ["type"] = "boolean" },
                ["dependencies"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
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

        public static JObject PackageJson(PackageInfo package)
        {
            if (package == null) return null;
            var dependencies = new JArray();
            if (package.dependencies != null)
            {
                foreach (var dependency in package.dependencies)
                    dependencies.Add(new JObject { ["name"] = dependency.name, ["version"] = dependency.version });
            }
            return new JObject
            {
                ["name"] = package.name,
                ["displayName"] = package.displayName,
                ["version"] = package.version,
                ["source"] = package.source.ToString(),
                ["isDirectDependency"] = package.isDirectDependency,
                ["resolvedPath"] = package.resolvedPath,
                ["assetPath"] = package.assetPath,
                ["dependencies"] = dependencies,
            };
        }

        public static async Task<T> AwaitRequest<T>(Request<T> request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.CallbackFunction update = null;
            update = () =>
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= update;
                completion.TrySetResult(true);
            };
            await MainThreadDispatcher.RunAsync(() =>
            {
                if (request.IsCompleted) completion.TrySetResult(true);
                else EditorApplication.update += update;
            });
            try { await completion.Task; }
            finally
            {
                try { await MainThreadDispatcher.RunAsync(() => EditorApplication.update -= update); }
                catch { }
            }
            if (request.Status != StatusCode.Success)
                throw new InvalidOperationException(
                    $"Package Manager request ended with {request.Status}: {request.Error?.message ?? "no error details"}");
            cancellationToken.ThrowIfCancellationRequested();
            return request.Result;
        }

        public static async Task AwaitRequest(Request request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.CallbackFunction update = null;
            update = () =>
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= update;
                completion.TrySetResult(true);
            };
            await MainThreadDispatcher.RunAsync(() =>
            {
                if (request.IsCompleted) completion.TrySetResult(true);
                else EditorApplication.update += update;
            });
            try { await completion.Task; }
            finally
            {
                try { await MainThreadDispatcher.RunAsync(() => EditorApplication.update -= update); }
                catch { }
            }
            if (request.Status != StatusCode.Success)
                throw new InvalidOperationException(
                    $"Package Manager request ended with {request.Status}: {request.Error?.message ?? "no error details"}");
            cancellationToken.ThrowIfCancellationRequested();
        }

        public static async Task<PackageCollection> ListAsync(bool offline, bool indirect, CancellationToken token)
        {
            await ClientGate.WaitAsync(token);
            try
            {
                var request = await MainThreadDispatcher.RunAsync(() => Client.List(offline, indirect), token);
                return await AwaitRequest(request, token);
            }
            finally { ClientGate.Release(); }
        }

        public static async Task<PackageInfo> AddAsync(string spec, CancellationToken token)
        {
            await ClientGate.WaitAsync(token);
            try
            {
                var request = await MainThreadDispatcher.RunAsync(() => Client.Add(spec), token);
                return await AwaitRequest(request, token);
            }
            finally { ClientGate.Release(); }
        }

        public static async Task RemoveDirectAsync(string packageId, CancellationToken token)
        {
            await ClientGate.WaitAsync(token);
            try
            {
                var list = await MainThreadDispatcher.RunAsync(() => Client.List(true, true), token);
                var collection = await AwaitRequest(list, token);
                var installed = collection.FirstOrDefault(package =>
                    string.Equals(package.name, packageId, StringComparison.Ordinal));
                if (installed == null) throw new InvalidOperationException($"package '{packageId}' is not installed");
                if (!installed.isDirectDependency)
                    throw new InvalidOperationException($"package '{packageId}' is indirect and cannot be removed directly");
                var request = await MainThreadDispatcher.RunAsync(() => Client.Remove(packageId), token);
                await AwaitRequest(request, token);
            }
            finally { ClientGate.Release(); }
        }

        public static bool TryPackageSpec(JObject args, bool includeVersion, out string packageId, out string version, out string spec, out string error)
        {
            packageId = ((string)args["packageId"])?.Trim();
            version = includeVersion ? ((string)args["version"])?.Trim() : null;
            spec = null;
            error = null;
            if (string.IsNullOrEmpty(packageId) || packageId.Length > 1024 || packageId.Any(char.IsWhiteSpace))
            { error = "packageId must be non-empty, at most 1024 characters and contain no whitespace"; return false; }
            if (includeVersion && !string.IsNullOrEmpty(version))
            {
                if (version.Length > 256 || version.Any(char.IsWhiteSpace))
                { error = "version must be at most 256 characters and contain no whitespace"; return false; }
                if (packageId.Contains("@"))
                { error = "do not provide version when packageId already contains a version/specifier"; return false; }
            }
            spec = string.IsNullOrEmpty(version) ? packageId : packageId + "@" + version;
            return true;
        }

        public static JObject PackageIdSchema(bool includeVersion = false)
        {
            var properties = new JObject
            {
                ["packageId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024, ["pattern"] = "^\\S+$" },
            };
            if (includeVersion) properties["version"] = new JObject { ["type"] = "string", ["maxLength"] = 256, ["pattern"] = "^\\S*$" };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray { "packageId" },
                ["additionalProperties"] = false,
            };
        }
    }

    public sealed class ListPackagesTool : IMcpTool
    {
        public string Name => "list_packages";
        public string Description => "Lists installed UPM packages asynchronously, optionally including indirect dependencies.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["offline"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["includeIndirect"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 1000 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => PackageToolSupport.OutputSchema(new JObject
        {
            ["packages"] = new JObject { ["type"] = "array", ["items"] = PackageToolSupport.PackageOutput },
            ["total"] = new JObject { ["type"] = "integer" },
            ["offset"] = new JObject { ["type"] = "integer" },
            ["limit"] = new JObject { ["type"] = "integer" },
            ["truncated"] = new JObject { ["type"] = "boolean" },
        });
        public ToolResult Execute(JObject args) => ToolResult.Error("ASYNC_REQUIRED", "list_packages must run through ExecuteAsync");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var context = ToolExecutionContext.Current;
            var token = context != null ? context.CancellationToken : default;
            var offset = (int?)args["offset"] ?? 0;
            var limit = (int?)args["limit"] ?? 1000;
            if (offset < 0 || limit < 1 || limit > 1000)
                return ToolResult.Error("BAD_PAGINATION", "offset must be non-negative and limit must be between 1 and 1000");
            try
            {
                var collection = await PackageToolSupport.ListAsync(
                    (bool?)args["offline"] ?? true, (bool?)args["includeIndirect"] ?? true, token);
                var ordered = collection.OrderBy(package => package.name, StringComparer.Ordinal).ToArray();
                var packages = new JArray(ordered.Skip(offset).Take(limit).Select(PackageToolSupport.PackageJson));
                return ToolResult.Ok($"{packages.Count} of {ordered.Length} installed package(s)", new JObject
                {
                    ["packages"] = packages, ["total"] = ordered.Length,
                    ["offset"] = offset, ["limit"] = limit,
                    ["truncated"] = offset + packages.Count < ordered.Length,
                });
            }
            catch (OperationCanceledException) { return ToolResult.Error("CANCELLED", "package list cancelled"); }
            catch (Exception exception) { return ToolResult.Error("PACKAGE_LIST_FAILED", exception.Message); }
        }
    }

    public sealed class SearchPackagesTool : IMcpTool
    {
        public string Name => "search_packages";
        public string Description => "Searches the installed package set by ID or display name; this does not query the remote registry.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["query"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 100 },
            },
            ["required"] = new JArray { "query" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => PackageToolSupport.OutputSchema(new JObject
        {
            ["query"] = new JObject { ["type"] = "string" },
            ["scope"] = new JObject { ["type"] = "string" },
            ["packages"] = new JObject { ["type"] = "array", ["items"] = PackageToolSupport.PackageOutput },
            ["total"] = new JObject { ["type"] = "integer" },
            ["offset"] = new JObject { ["type"] = "integer" },
            ["limit"] = new JObject { ["type"] = "integer" },
            ["truncated"] = new JObject { ["type"] = "boolean" },
        });
        public ToolResult Execute(JObject args) => ToolResult.Error("ASYNC_REQUIRED", "search_packages must run through ExecuteAsync");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var context = ToolExecutionContext.Current;
            var token = context != null ? context.CancellationToken : default;
            var query = ((string)args["query"])?.Trim();
            var offset = (int?)args["offset"] ?? 0;
            var limit = (int?)args["limit"] ?? 100;
            if (string.IsNullOrEmpty(query) || query.Length > 256)
                return ToolResult.Error("BAD_QUERY", "query must contain 1 to 256 non-whitespace characters");
            if (offset < 0 || limit < 1 || limit > 1000)
                return ToolResult.Error("BAD_PAGINATION", "offset must be non-negative and limit must be between 1 and 1000");
            try
            {
                var collection = await PackageToolSupport.ListAsync(true, true, token);
                var matches = collection.Where(package =>
                        package.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                        || (package.displayName ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(package => package.name, StringComparer.Ordinal).ToArray();
                var packages = new JArray(matches.Skip(offset).Take(limit).Select(PackageToolSupport.PackageJson));
                return ToolResult.Ok($"{packages.Count} of {matches.Length} installed package match(es)", new JObject
                {
                    ["query"] = query, ["packages"] = packages, ["scope"] = "installed",
                    ["total"] = matches.Length, ["offset"] = offset, ["limit"] = limit,
                    ["truncated"] = offset + packages.Count < matches.Length,
                });
            }
            catch (OperationCanceledException) { return ToolResult.Error("CANCELLED", "package search cancelled"); }
            catch (Exception exception) { return ToolResult.Error("PACKAGE_SEARCH_FAILED", exception.Message); }
        }
    }

    public sealed class PreviewAddPackageTool : IMcpTool
    {
        public string Name => "preview_add_package";
        public string Description => "Previews an exact UPM package add/update operation without changing the manifest.";
        public JObject InputSchema => PackageToolSupport.PackageIdSchema(true);
        public bool IsWrite => false;
        public JObject OutputSchema => PackageToolSupport.OutputSchema(new JObject
        {
            ["packageSpec"] = new JObject { ["type"] = "string" },
            ["operation"] = new JObject { ["type"] = "string" },
            ["requiresConfirmation"] = new JObject { ["type"] = "boolean" },
            ["nextTool"] = new JObject { ["type"] = "string" },
        });
        public ToolResult Execute(JObject args)
        {
            if (!PackageToolSupport.TryPackageSpec(args, true, out _, out _, out var spec, out var error))
                return ToolResult.Error("BAD_PACKAGE_SPEC", error);
            return ToolResult.Ok($"would add package '{spec}'", new JObject
            {
                ["packageSpec"] = spec,
                ["operation"] = "add_or_update",
                ["requiresConfirmation"] = true,
                ["nextTool"] = "confirm_add_package",
                ["warning"] = "Package changes can trigger import and domain reload; the MCP server may restart.",
            });
        }
    }

    public sealed class ConfirmAddPackageTool : IMcpTool
    {
        public string Name => "confirm_add_package";
        public string Description => "Starts a confirmed UPM add/update job. Package Manager operations cannot be safely cancelled after submission.";
        public JObject InputSchema => PackageToolSupport.PackageIdSchema(true);
        public bool IsWrite => true;
        public JObject OutputSchema => PackageToolSupport.JobOutput;
        public ToolResult Execute(JObject args)
        {
            if (!PackageToolSupport.TryPackageSpec(args, true, out _, out _, out var spec, out var error))
                return ToolResult.Error("BAD_PACKAGE_SPEC", error);
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("EDITOR_BUSY", "package changes require stable Edit Mode with no compile/import in progress");
            var job = ToolJobManager.Create("package_add", args, canCancel: false);
            EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
            {
                ToolJobManager.Report(record, 0.1, $"submitting {spec}");
                var package = await PackageToolSupport.AddAsync(spec, token);
                ToolJobManager.Report(record, 1, "package added");
                return new JObject
                {
                    ["package"] = PackageToolSupport.PackageJson(package),
                    ["serverMayReload"] = true,
                };
            });
            return ToolResult.Ok($"package add job {job.Id} queued", job.ToJson());
        }
    }

    public sealed class PreviewRemovePackageTool : IMcpTool
    {
        public string Name => "preview_remove_package";
        public string Description => "Previews removal of one direct UPM package without changing the manifest.";
        public JObject InputSchema => PackageToolSupport.PackageIdSchema();
        public bool IsWrite => false;
        public JObject OutputSchema => PackageToolSupport.OutputSchema(new JObject
        {
            ["packageId"] = new JObject { ["type"] = "string" },
            ["installedPackage"] = PackageToolSupport.PackageOutput,
            ["operation"] = new JObject { ["type"] = "string" },
            ["requiresConfirmation"] = new JObject { ["type"] = "boolean" },
            ["nextTool"] = new JObject { ["type"] = "string" },
        });
        public ToolResult Execute(JObject args) => ToolResult.Error("ASYNC_REQUIRED", "preview_remove_package must run through ExecuteAsync");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            if (!PackageToolSupport.TryPackageSpec(args, false, out var id, out _, out _, out var error))
                return ToolResult.Error("BAD_PACKAGE_SPEC", error);
            var context = ToolExecutionContext.Current;
            var token = context != null ? context.CancellationToken : default;
            PackageInfo installed;
            try
            {
                var collection = await PackageToolSupport.ListAsync(true, true, token);
                installed = collection.FirstOrDefault(package => string.Equals(package.name, id, StringComparison.Ordinal));
            }
            catch (OperationCanceledException) { return ToolResult.Error("CANCELLED", "package remove preview cancelled"); }
            catch (Exception exception) { return ToolResult.Error("PACKAGE_LIST_FAILED", exception.Message); }
            if (installed == null) return ToolResult.Error("PACKAGE_NOT_INSTALLED", $"package '{id}' is not installed");
            if (!installed.isDirectDependency)
                return ToolResult.Error("PACKAGE_INDIRECT", $"package '{id}' is indirect and cannot be removed directly");
            return ToolResult.Ok($"would remove direct package '{id}'", new JObject
            {
                ["packageId"] = id,
                ["installedPackage"] = PackageToolSupport.PackageJson(installed),
                ["operation"] = "remove",
                ["requiresConfirmation"] = true,
                ["nextTool"] = "confirm_remove_package",
                ["warning"] = "Removing a package can break scripts and trigger domain reload.",
            });
        }
    }

    public sealed class ConfirmRemovePackageTool : IMcpTool
    {
        public string Name => "confirm_remove_package";
        public string Description => "Starts a confirmed UPM remove job. Package Manager operations cannot be safely cancelled after submission.";
        public JObject InputSchema => PackageToolSupport.PackageIdSchema();
        public bool IsWrite => true;
        public JObject OutputSchema => PackageToolSupport.JobOutput;
        public ToolResult Execute(JObject args)
        {
            if (!PackageToolSupport.TryPackageSpec(args, false, out var id, out _, out _, out var error))
                return ToolResult.Error("BAD_PACKAGE_SPEC", error);
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("EDITOR_BUSY", "package changes require stable Edit Mode with no compile/import in progress");
            var job = ToolJobManager.Create("package_remove", args, canCancel: false);
            EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
            {
                ToolJobManager.Report(record, 0.1, $"submitting removal of {id}");
                await PackageToolSupport.RemoveDirectAsync(id, token);
                ToolJobManager.Report(record, 1, "package removed");
                return new JObject { ["removedPackageId"] = id, ["serverMayReload"] = true };
            });
            return ToolResult.Ok($"package remove job {job.Id} queued", job.ToJson());
        }
    }

    public sealed class GetPackageJobTool : IMcpTool
    {
        public string Name => "get_package_job";
        public string Description => "Returns a package add/remove job state.";
        public JObject InputSchema => JobSchema();
        public bool IsWrite => false;
        public JObject OutputSchema => PackageToolSupport.JobOutput;
        public ToolResult Execute(JObject args)
        {
            if (!ToolJobManager.TryGet((string)args["jobId"], out var job)
                || !job.Kind.StartsWith("package_", StringComparison.Ordinal))
                return ToolResult.Error("JOB_NOT_FOUND", "package job not found");
            return ToolResult.Ok($"package job {job.Status}", job.ToJson());
        }

        internal static JObject JobSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["jobId"] = new JObject { ["type"] = "string" } },
            ["required"] = new JArray { "jobId" },
            ["additionalProperties"] = false,
        };
    }
}
