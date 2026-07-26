using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.TestTools
{
    internal static class TestToolSupport
    {
        public const int MaxFailures = 100;
        public const int MaxTextLength = 32768;

        public static JObject ModeSchema(bool withFilter)
        {
            var properties = new JObject
            {
                ["testMode"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "EditMode", "PlayMode" },
                    ["default"] = "EditMode",
                },
            };
            if (withFilter)
            {
                foreach (var name in new[] { "testNames", "groupNames", "categoryNames", "assemblyNames" })
                {
                    properties[name] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
                        ["maxItems"] = 1000,
                    };
                }
            }
            else
            {
                properties["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 };
                properties["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 500 };
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
        }

        public static JObject TestSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["id"] = new JObject { ["type"] = "string" },
                ["uniqueName"] = new JObject { ["type"] = "string" },
                ["name"] = new JObject { ["type"] = "string" },
                ["fullName"] = new JObject { ["type"] = "string" },
                ["testMode"] = new JObject { ["type"] = "string" },
                ["runState"] = new JObject { ["type"] = "string" },
                ["categories"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                ["skipReason"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["parentFullName"] = new JObject { ["type"] = new JArray { "string", "null" } },
            },
            ["additionalProperties"] = false,
        };

        public static JObject ListOutputSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["tests"] = new JObject { ["type"] = "array", ["items"] = TestSchema() },
                ["offset"] = new JObject { ["type"] = "integer" },
                ["count"] = new JObject { ["type"] = "integer" },
                ["total"] = new JObject { ["type"] = "integer" },
                ["truncated"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "tests", "offset", "count", "total", "truncated" },
            ["additionalProperties"] = false,
        };

        public static JObject JobOutputSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["jobId"] = new JObject { ["type"] = "string" },
                ["kind"] = new JObject { ["type"] = "string" },
                ["status"] = new JObject { ["type"] = "string" },
                ["progress"] = new JObject { ["type"] = "number" },
                ["message"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["error"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["result"] = new JObject(),
                ["input"] = new JObject { ["type"] = "object" },
                ["createdUtc"] = new JObject { ["type"] = "string" },
                ["startedUtc"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["completedUtc"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["canCancel"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "jobId", "kind", "status", "progress", "createdUtc", "canCancel" },
            ["additionalProperties"] = false,
        };

        public static bool ValidateRunState(out string error)
        {
            if (EditorApplication.isCompiling) { error = "Unity is compiling scripts"; return false; }
            if (EditorApplication.isUpdating) { error = "Unity is importing or updating assets"; return false; }
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying)
            {
                error = "Unity must be in stable Edit Mode before a TestRunnerApi run";
                return false;
            }
            error = null;
            return true;
        }

        public static bool ValidateFilters(JObject args, out string error)
        {
            foreach (var name in new[] { "testNames", "groupNames", "categoryNames", "assemblyNames" })
            {
                if (!(args[name] is JArray array)) continue;
                if (array.Count > 1000) { error = $"{name} exceeds 1000 entries"; return false; }
                foreach (var value in array.Values<string>())
                {
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
                    {
                        error = $"{name} entries must be non-empty and at most 1024 characters";
                        return false;
                    }
                }
            }
            error = null;
            return true;
        }

        public static TestMode ParseMode(JObject args) =>
            string.Equals((string)args["testMode"], "PlayMode", StringComparison.OrdinalIgnoreCase)
                ? TestMode.PlayMode
                : TestMode.EditMode;

        public static Filter Filter(JObject args) => new Filter
        {
            testMode = ParseMode(args),
            testNames = Values(args["testNames"]),
            groupNames = Values(args["groupNames"]),
            categoryNames = Values(args["categoryNames"]),
            assemblyNames = Values(args["assemblyNames"]),
        };

        private static string[] Values(JToken token) =>
            token is JArray array && array.Count > 0 ? array.Values<string>().ToArray() : null;

        public static void FlattenTests(ITestAdaptor node, List<ITestAdaptor> output)
        {
            if (node == null) return;
            if (!node.IsSuite) output.Add(node);
            if (node.Children == null) return;
            foreach (var child in node.Children) FlattenTests(child, output);
        }

        public static JObject TestJson(ITestAdaptor test) => new JObject
        {
            ["id"] = test.Id,
            ["uniqueName"] = test.UniqueName,
            ["name"] = test.Name,
            ["fullName"] = test.FullName,
            ["testMode"] = test.TestMode.ToString(),
            ["runState"] = test.RunState.ToString(),
            ["categories"] = new JArray(test.Categories ?? Array.Empty<string>()),
            ["skipReason"] = Trim(test.SkipReason),
            ["parentFullName"] = Trim(test.ParentFullName),
        };

        public static JObject ResultJson(ITestResultAdaptor result, JArray failures, bool failuresTruncated) => new JObject
        {
            ["fullName"] = Trim(result.FullName),
            ["resultState"] = Trim(result.ResultState),
            ["status"] = result.TestStatus.ToString(),
            ["durationSeconds"] = result.Duration,
            ["passed"] = result.PassCount,
            ["failed"] = result.FailCount,
            ["skipped"] = result.SkipCount,
            ["inconclusive"] = result.InconclusiveCount,
            ["asserts"] = result.AssertCount,
            ["message"] = Trim(result.Message),
            ["stackTrace"] = Trim(result.StackTrace),
            ["output"] = Trim(result.Output),
            ["failures"] = failures,
            ["failuresTruncated"] = failuresTruncated,
        };

        public static string Trim(string value) => string.IsNullOrEmpty(value)
            ? value
            : value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength);
    }

    internal sealed class TestRunCallbacks : IErrorCallbacks
    {
        private readonly TaskCompletionSource<JToken> _completion =
            new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly JArray _failures = new JArray();
        private bool _failuresTruncated;

        public Task<JToken> Completion => _completion.Task;
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result)
        {
            if (result == null || result.Test == null || result.Test.IsSuite || result.FailCount == 0) return;
            if (_failures.Count >= TestToolSupport.MaxFailures) { _failuresTruncated = true; return; }
            _failures.Add(new JObject
            {
                ["fullName"] = TestToolSupport.Trim(result.FullName),
                ["resultState"] = TestToolSupport.Trim(result.ResultState),
                ["message"] = TestToolSupport.Trim(result.Message),
                ["stackTrace"] = TestToolSupport.Trim(result.StackTrace),
                ["output"] = TestToolSupport.Trim(result.Output),
                ["durationSeconds"] = result.Duration,
            });
        }
        public void RunFinished(ITestResultAdaptor result)
        {
            if (result == null) _completion.TrySetException(new ToolJobFailureException("Test Runner returned no result"));
            else _completion.TrySetResult(TestToolSupport.ResultJson(result, _failures, _failuresTruncated));
        }
        public void OnError(string message) =>
            _completion.TrySetException(new ToolJobFailureException("TEST_RUN_FAILED: " + TestToolSupport.Trim(message)));
    }

    public sealed class ListTestsTool : IMcpTool
    {
        public string Name => "list_tests";
        public string Description => "Lists EditMode or PlayMode tests through TestRunnerApi with stable UniqueName values and pagination.";
        public JObject InputSchema => TestToolSupport.ModeSchema(false);
        public JObject OutputSchema => TestToolSupport.ListOutputSchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => ToolResult.Error("ASYNC_REQUIRED", "list_tests must run through ExecuteAsync");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var offsetArg = (int?)args["offset"] ?? 0;
            var limit = (int?)args["limit"] ?? 500;
            if (offsetArg < 0 || limit < 1 || limit > 5000)
                return ToolResult.Error("BAD_ARGS", "offset must be non-negative and limit must be in 1..5000");
            if (!TestToolSupport.ValidateRunState(out var stateError))
                return ToolResult.Error("EDITOR_BUSY", stateError);

            var context = ToolExecutionContext.Current;
            var token = context != null ? context.CancellationToken : default;
            TestRunnerApi api = null;
            try
            {
                api = await MainThreadDispatcher.RunAsync(ScriptableObject.CreateInstance<TestRunnerApi>, token);
                var completion = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CS0618
                await MainThreadDispatcher.RunAsync(() => api.RetrieveTestList(
                    TestToolSupport.ParseMode(args), root => completion.TrySetResult(root)), token);
#pragma warning restore CS0618
                using (token.Register(() => completion.TrySetCanceled(token)))
                {
                    var root = await completion.Task;
                    var tests = new List<ITestAdaptor>();
                    TestToolSupport.FlattenTests(root, tests);
                    tests.Sort((left, right) => string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));
                    var offset = Math.Min(offsetArg, tests.Count);
                    var page = new JArray(tests.Skip(offset).Take(limit).Select(TestToolSupport.TestJson));
                    return ToolResult.Ok($"{tests.Count} {TestToolSupport.ParseMode(args)} test(s)", new JObject
                    {
                        ["tests"] = page,
                        ["offset"] = offset,
                        ["count"] = page.Count,
                        ["total"] = tests.Count,
                        ["truncated"] = offset + page.Count < tests.Count,
                    });
                }
            }
            catch (OperationCanceledException) { return ToolResult.Error("CANCELLED", "test listing cancelled"); }
            catch (Exception exception) { return ToolResult.Error("TEST_LIST_FAILED", exception.Message); }
            finally
            {
                if (api != null)
                {
                    try { await MainThreadDispatcher.RunAsync(() => UnityEngine.Object.DestroyImmediate(api)); }
                    catch { }
                }
            }
        }
    }

    public sealed class RunTestsTool : IMcpTool
    {
        public string Name => "run_tests";
        public string Description => "Starts one serialized Unity Test Framework job and returns immediately with a reload-sensitive jobId.";
        public JObject InputSchema => TestToolSupport.ModeSchema(true);
        public JObject OutputSchema => TestToolSupport.JobOutputSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!TestToolSupport.ValidateRunState(out var stateError))
                return ToolResult.Error("EDITOR_BUSY", stateError);
            if (!TestToolSupport.ValidateFilters(args, out var filterError))
                return ToolResult.Error("BAD_ARGS", filterError);
            if (!ToolJobManager.TryCreateExclusive("test", args, canCancel: true, out var job))
                return ToolResult.Error("TEST_RUN_ACTIVE", "another Unity test job is already active");

            EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
            {
                TestRunnerApi api = null;
                var callbacks = new TestRunCallbacks();
                string runGuid = null;
                try
                {
                    api = await MainThreadDispatcher.RunAsync(ScriptableObject.CreateInstance<TestRunnerApi>, token);
                    await MainThreadDispatcher.RunAsync(() => api.RegisterCallbacks(callbacks), token);
                    runGuid = await MainThreadDispatcher.RunAsync(() =>
                        api.Execute(new ExecutionSettings(TestToolSupport.Filter(args))), token);
                    ToolJobManager.Report(record, 0.1, $"test run {runGuid} started");
                    using (token.Register(() =>
                    {
                        if (!string.IsNullOrEmpty(runGuid))
                            _ = MainThreadDispatcher.RunAsync(() => TestRunnerApi.CancelTestRun(runGuid));
                    }))
                    {
                        return await callbacks.Completion;
                    }
                }
                finally
                {
                    if (api != null)
                    {
                        try
                        {
                            await MainThreadDispatcher.RunAsync(() =>
                            {
                                api.UnregisterCallbacks(callbacks);
                                UnityEngine.Object.DestroyImmediate(api);
                            });
                        }
                        catch { }
                    }
                }
            });
            return ToolResult.Ok($"test job {job.Id} queued", job.ToJson());
        }
    }

    public sealed class GetTestJobTool : IMcpTool
    {
        public string Name => "get_test_job";
        public string Description => "Returns one Unity test job state and aggregated result.";
        public JObject InputSchema => JobSchema();
        public JObject OutputSchema => TestToolSupport.JobOutputSchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["jobId"];
            if (string.IsNullOrWhiteSpace(id)) return ToolResult.Error("BAD_ARGS", "jobId is required");
            if (!ToolJobManager.TryGet(id, out var job) || job.Kind != "test")
                return ToolResult.Error("JOB_NOT_FOUND", "test job not found");
            return ToolResult.Ok($"test job {job.Status}", job.ToJson());
        }

        internal static JObject JobSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["jobId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 } },
            ["required"] = new JArray { "jobId" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class CancelTestJobTool : IMcpTool
    {
        public string Name => "cancel_test_job";
        public string Description => "Requests TestRunnerApi cancellation; cleanup can take multiple frames before the job becomes terminal.";
        public JObject InputSchema => GetTestJobTool.JobSchema();
        public JObject OutputSchema => TestToolSupport.JobOutputSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["jobId"];
            if (string.IsNullOrWhiteSpace(id)) return ToolResult.Error("BAD_ARGS", "jobId is required");
            if (!ToolJobManager.TryGet(id, out var job) || job.Kind != "test")
                return ToolResult.Error("JOB_NOT_FOUND", "test job not found");
            if (!ToolJobManager.Cancel(id, out job))
                return ToolResult.Error("TEST_NOT_CANCELLABLE", "test job is terminal or already cancelling", job.ToJson());
            return ToolResult.Ok("test cancellation requested; poll until terminal", job.ToJson());
        }
    }
}
