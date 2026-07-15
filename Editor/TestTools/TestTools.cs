using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.TestTools
{
    internal static class TestToolSupport
    {
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
                properties["testNames"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["maxItems"] = 1000,
                };
                properties["groupNames"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["maxItems"] = 1000,
                };
                properties["categoryNames"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["maxItems"] = 1000,
                };
                properties["assemblyNames"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["maxItems"] = 1000,
                };
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

        public static TestMode ParseMode(JObject args) =>
            string.Equals((string)args["testMode"], "PlayMode", StringComparison.OrdinalIgnoreCase)
                ? TestMode.PlayMode
                : TestMode.EditMode;

        public static Filter Filter(JObject args)
        {
            return new Filter
            {
                testMode = ParseMode(args),
                testNames = Values(args["testNames"]),
                groupNames = Values(args["groupNames"]),
                categoryNames = Values(args["categoryNames"]),
                assemblyNames = Values(args["assemblyNames"]),
            };
        }

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
            ["skipReason"] = test.SkipReason,
            ["parentFullName"] = test.ParentFullName,
        };

        public static JObject ResultJson(ITestResultAdaptor result, JArray failures)
        {
            return new JObject
            {
                ["fullName"] = result.FullName,
                ["resultState"] = result.ResultState,
                ["status"] = result.TestStatus.ToString(),
                ["durationSeconds"] = result.Duration,
                ["passed"] = result.PassCount,
                ["failed"] = result.FailCount,
                ["skipped"] = result.SkipCount,
                ["inconclusive"] = result.InconclusiveCount,
                ["asserts"] = result.AssertCount,
                ["message"] = result.Message,
                ["stackTrace"] = result.StackTrace,
                ["output"] = result.Output,
                ["failures"] = failures,
            };
        }
    }

    internal sealed class TestRunCallbacks : ICallbacks
    {
        private readonly TaskCompletionSource<JToken> _completion =
            new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly JArray _failures = new JArray();

        public Task<JToken> Completion => _completion.Task;
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result)
        {
            if (result == null || result.Test == null || result.Test.IsSuite || result.FailCount == 0) return;
            _failures.Add(new JObject
            {
                ["fullName"] = result.FullName,
                ["resultState"] = result.ResultState,
                ["message"] = result.Message,
                ["stackTrace"] = result.StackTrace,
                ["output"] = result.Output,
                ["durationSeconds"] = result.Duration,
            });
        }
        public void RunFinished(ITestResultAdaptor result)
        {
            if (result == null) _completion.TrySetException(new InvalidOperationException("Test Runner returned no result"));
            else _completion.TrySetResult(TestToolSupport.ResultJson(result, _failures));
        }
    }

    public sealed class ListTestsTool : IMcpTool
    {
        public string Name => "list_tests";
        public string Description => "Lists EditMode or PlayMode tests through TestRunnerApi with stable UniqueName values and pagination.";
        public JObject InputSchema => TestToolSupport.ModeSchema(false);
        public bool IsWrite => false;
        public ToolResult Execute(JObject args) => ToolResult.Error("ASYNC_REQUIRED", "list_tests must run through ExecuteAsync");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var context = ToolExecutionContext.Current;
            var token = context != null ? context.CancellationToken : default;
            TestRunnerApi api = null;
            try
            {
                api = await MainThreadDispatcher.RunAsync(ScriptableObject.CreateInstance<TestRunnerApi>, token);
                var completion = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
                await MainThreadDispatcher.RunAsync(() => api.RetrieveTestList(
                    TestToolSupport.ParseMode(args), root => completion.TrySetResult(root)), token);
                using (token.Register(() => completion.TrySetCanceled(token)))
                {
                    var root = await completion.Task;
                    var tests = new List<ITestAdaptor>();
                    TestToolSupport.FlattenTests(root, tests);
                    tests.Sort((left, right) => string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));
                    int offset = Math.Min((int?)args["offset"] ?? 0, tests.Count);
                    int limit = (int?)args["limit"] ?? 500;
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
        public string Description => "Starts a serialized Unity Test Framework job and returns immediately with a jobId.";
        public JObject InputSchema => TestToolSupport.ModeSchema(true);
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var active = ToolJobManager.List("test", 20)
                .OfType<JObject>()
                .Any(item => !new[] { "Completed", "Failed", "Cancelled" }.Contains((string)item["status"]));
            if (active) return ToolResult.Error("TEST_RUN_ACTIVE", "another Unity test job is already active");

            var job = ToolJobManager.Create("test", args, canCancel: true);
            UnityEditor.EditorApplication.delayCall += () => ToolJobManager.Start(job, async (record, token) =>
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
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            if (!ToolJobManager.TryGet((string)args["jobId"], out var job) || job.Kind != "test")
                return ToolResult.Error("JOB_NOT_FOUND", "test job not found");
            return ToolResult.Ok($"test job {job.Status}", job.ToJson());
        }

        internal static JObject JobSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["jobId"] = new JObject { ["type"] = "string" } },
            ["required"] = new JArray { "jobId" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class CancelTestJobTool : IMcpTool
    {
        public string Name => "cancel_test_job";
        public string Description => "Requests cancellation of a running Unity Test Framework job.";
        public JObject InputSchema => GetTestJobTool.JobSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var id = (string)args["jobId"];
            if (!ToolJobManager.TryGet(id, out var job) || job.Kind != "test")
                return ToolResult.Error("JOB_NOT_FOUND", "test job not found");
            if (!ToolJobManager.Cancel(id, out job))
                return ToolResult.Error("TEST_NOT_CANCELLABLE", "test job is terminal or already cancelling", job.ToJson());
            return ToolResult.Ok("test cancellation requested", job.ToJson());
        }
    }
}
