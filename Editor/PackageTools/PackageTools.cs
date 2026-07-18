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
            }, cancellationToken);
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                try { await completion.Task; }
                finally
                {
                    try { await MainThreadDispatcher.RunAsync(() => EditorApplication.update -= update); }
                    catch { }
                }
            }
            if (request.Status == StatusCode.Failure)
                throw new InvalidOperationException(request.Error?.message ?? "Package Manager request failed");
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
            }, cancellationToken);
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                try { await completion.Task; }
                finally
                {
                    try { await MainThreadDispatcher.RunAsync(() => EditorApplication.update -= update); }
                    catch { }
                }
            }
            if (request.Status == StatusCode.Failure)
                throw new InvalidOperationException(request.Error?.message ?? "Package Manager request failed");
        }

        public static async Task<PackageCollection> ListAsync(bool offline, bool indirect, CancellationToken token)
        {
            var request = await MainThreadDispatcher.RunAsync(() => Client.List(offline, indirect), token);
            return await AwaitRequest(request, token);
        }

        public static JObject PackageIdSchema(bool includeVersion = false)
        {
            var properties = new JObject
            {
                ["packageId"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            };
            if (includeVersion) properties["version"] = new JObject { ["type"] = "string" };
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
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => ToolResult.Error("ASYNC_REQUIRED", "list_packages must run through ExecuteAsync");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var context = ToolExecutionContext.Current;
            var token = context != null ? context.CancellationToken : default;
            try
            {
                var collection = await PackageToolSupport.ListAsync(
                    (bool?)args["offline"] ?? true, (bool?)args["includeIndirect"] ?? true, token);
                var packages = new JArray(collection.OrderBy(package => package.name, StringComparer.Ordinal)
                    .Select(PackageToolSupport.PackageJson));
                return ToolResult.Ok($"{packages.Count} installed package(s)", new JObject { ["packages"] = packages });
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
                ["query"] = new JObject { ["type"] = "string" },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 100 },
            },
            ["required"] = new JArray { "query" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => ToolResult.Error("ASYNC_REQUIRED", "search_packages must run through ExecuteAsync");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var context = ToolExecutionContext.Current;
            var token = context != null ? context.CancellationToken : default;
            var query = (string)args["query"] ?? string.Empty;
            try
            {
                var collection = await PackageToolSupport.ListAsync(true, true, token);
                var matches = collection.Where(package =>
                        package.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                        || (package.displayName ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(package => package.name, StringComparer.Ordinal)
                    .Take((int?)args["limit"] ?? 100);
                var packages = new JArray(matches.Select(PackageToolSupport.PackageJson));
                return ToolResult.Ok($"{packages.Count} installed package match(es)", new JObject
                {
                    ["query"] = query,
                    ["packages"] = packages,
                    ["scope"] = "installed",
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
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["packageId"];
            var version = (string)args["version"];
            var spec = string.IsNullOrEmpty(version) ? id : id + "@" + version;
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
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["packageId"];
            var version = (string)args["version"];
            var spec = string.IsNullOrEmpty(version) ? id : id + "@" + version;
            var job = ToolJobManager.Create("package_add", args, canCancel: false);
            EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
            {
                ToolJobManager.Report(record, 0.1, $"submitting {spec}");
                var request = await MainThreadDispatcher.RunAsync(() => Client.Add(spec), token);
                var package = await PackageToolSupport.AwaitRequest(request, token);
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
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["packageId"];
            return ToolResult.Ok($"would remove package '{id}'", new JObject
            {
                ["packageId"] = id,
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
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["packageId"];
            var job = ToolJobManager.Create("package_remove", args, canCancel: false);
            EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
            {
                ToolJobManager.Report(record, 0.1, $"submitting removal of {id}");
                var request = await MainThreadDispatcher.RunAsync(() => Client.Remove(id), token);
                await PackageToolSupport.AwaitRequest(request, token);
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
