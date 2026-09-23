using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class ToolJobSafetyTests
    {
        [Test]
        public async Task MutatingJob_HoldsResourceUntilTerminal_ButReadsAndCancelRemainAvailable()
        {
            var started = new TaskCompletionSource<bool>();
            var finish = new TaskCompletionSource<bool>();
            var job = ToolJobManager.Create("asset_reimport");
            ToolJobManager.Start(job, async (_, token) =>
            {
                started.SetResult(true);
                await finish.Task;
                return new JObject { ["completedItems"] = 1 };
            });
            using (var coordinator = new McpRequestCoordinator(4))
            {
                Task<McpRequestCoordinator.Lease> write = null;
                try
                {
                    Assert.That(await Task.WhenAny(started.Task, Task.Delay(2000)), Is.SameAs(started.Task));
                    using (await coordinator.BeginAsync("read", false, CancellationToken.None, TimeSpan.FromSeconds(2))) { }
                    write = coordinator.BeginAsync("write", true, CancellationToken.None, TimeSpan.FromSeconds(5));
                    await Task.Delay(50);
                    Assert.That(write.IsCompleted, Is.False);
                    Assert.That(ToolJobManager.IsControlTool("cancel_tool_job"), Is.True);
                }
                finally
                {
                    finish.TrySetResult(true);
                    if (write != null) (await write).Dispose();
                    await ToolJobManager.WaitAsync(job.Id, TimeSpan.FromSeconds(2), CancellationToken.None);
                }
            }
            Assert.That(job.Status, Is.EqualTo(ToolJobStatus.Completed));
        }

        [Test]
        public async Task QueuedJob_CanBeCancelledBeforeItsMutationResourceIsAvailable()
        {
            using (var coordinator = new McpRequestCoordinator(2))
            using (await coordinator.BeginAsync("blocking-write", true, CancellationToken.None, TimeSpan.FromSeconds(5)))
            {
                bool executed = false;
                var job = ToolJobManager.Create("package_add");
                ToolJobManager.Start(job, (_, __) => { executed = true; return Task.FromResult<JToken>(new JObject()); });
                Assert.That(ToolJobManager.Cancel(job.Id, out _), Is.True);
                var result = await ToolJobManager.WaitAsync(job.Id, TimeSpan.FromSeconds(2), CancellationToken.None);
                Assert.That(result.Status, Is.EqualTo(ToolJobStatus.Cancelled));
                Assert.That(executed, Is.False);
            }
        }

        [Test]
        public async Task LateCancellation_DoesNotEraseAnOperationResult()
        {
            var finish = new TaskCompletionSource<bool>();
            var job = ToolJobManager.Create("asset_reimport");
            ToolJobManager.Start(job, async (_, __) => { await finish.Task; return new JObject { ["applied"] = true }; });
            try { Assert.That(ToolJobManager.Cancel(job.Id, out _), Is.True); }
            finally { finish.TrySetResult(true); }
            var result = await ToolJobManager.WaitAsync(job.Id, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.That(result.Status, Is.EqualTo(ToolJobStatus.Completed));
            Assert.That((bool)result.Result["applied"], Is.True);
        }

        [Test]
        public async Task QueuedRequest_IsCancellableBeforeConcurrencyAdmission()
        {
            using (var coordinator = new McpRequestCoordinator(1))
            using (await coordinator.BeginAsync("occupied", false, CancellationToken.None, TimeSpan.FromSeconds(5)))
            {
                var queued = coordinator.BeginAsync("queued", true, CancellationToken.None, TimeSpan.FromSeconds(5));
                Assert.That(queued.IsCompleted, Is.False);
                Assert.That(coordinator.Cancel("queued"), Is.True);
                try
                {
                    using (await queued) { Assert.Fail("A cancelled queued request must not acquire execution access."); }
                }
                catch (OperationCanceledException) { }
            }
        }

        [Test]
        public async Task PriorityControl_DoesNotWaitBehindExhaustedNormalSlots()
        {
            using (var coordinator = new McpRequestCoordinator(1))
            using (await coordinator.BeginAsync("normal", false, CancellationToken.None, TimeSpan.FromSeconds(5)))
            using (await coordinator.BeginAsync("cancel", false, CancellationToken.None, TimeSpan.FromSeconds(1), priority: true)) { }
        }

        [Test]
        public async Task RequestCoordinators_ShareMutationGate_AndDisposalDoesNotDestroyIt()
        {
            using (var first = new McpRequestCoordinator(2))
            using (var second = new McpRequestCoordinator(2))
            {
                var owner = await first.BeginAsync("first", true, CancellationToken.None, TimeSpan.FromSeconds(5));
                var waiting = second.BeginAsync("second", true, CancellationToken.None, TimeSpan.FromSeconds(5));
                try { await Task.Delay(50); Assert.That(waiting.IsCompleted, Is.False); }
                finally { owner.Dispose(); (await waiting).Dispose(); }
                first.Dispose();
                using (await second.BeginAsync("after-dispose", true, CancellationToken.None, TimeSpan.FromSeconds(2))) { }
            }
        }

        [Test]
        public async Task ReimportReceipt_IsPersistedOutsideSessionState()
        {
            var job = ToolJobManager.Create("asset_reimport");
            ToolJobManager.Start(job, (_, __) => Task.FromResult<JToken>(new JObject { ["completedItems"] = 2 }));
            await ToolJobManager.WaitAsync(job.Id, TimeSpan.FromSeconds(2), CancellationToken.None);
            var journal = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "UnityMcp", "jobs.json");
            Assert.That(ToolJobManager.PersistenceError, Is.Null);
            var receipt = JArray.Parse(File.ReadAllText(journal)).OfType<JObject>().Single(item => (string)item["jobId"] == job.Id);
            Assert.That((string)receipt["status"], Is.EqualTo("Completed"));
            Assert.That((int)receipt["result"]["completedItems"], Is.EqualTo(2));
        }
    }
}
