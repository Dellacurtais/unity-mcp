using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class McpServerInstanceCoordinatorTests
    {
        private sealed class TestClock
        {
            public DateTimeOffset UtcNow = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

            public void Advance(TimeSpan duration) => UtcNow += duration;
        }

        private readonly List<string> _roots = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var root in _roots)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
            _roots.Clear();
        }

        [Test]
        public void Lease_AllowsOnlyOneOwnerAndExposesRemoteProjectAndPort()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 101, 202 };
            var root = NewRoot();
            using var first = NewCoordinator(root, "first", 101, "ProjectA", clock, alive);
            using var second = NewCoordinator(root, "second", 202, "ProjectB", clock, alive);

            var acquired = first.TryAcquire(19451, "shared-token");
            Assert.That(acquired.Acquired, Is.True);
            first.MarkRunning(acquired.LeaseId);
            second.PollNow();

            Assert.That(second.Snapshot.HasRemoteOwner, Is.True);
            Assert.That(second.Snapshot.CanBringHere, Is.True);
            Assert.That(second.Snapshot.OwnerProjectName, Is.EqualTo("ProjectA"));
            Assert.That(second.Snapshot.Port, Is.EqualTo(19451));

            var rejected = second.TryAcquire(20562, "shared-token");
            Assert.That(rejected.Acquired, Is.False);
            Assert.That(rejected.Port, Is.EqualTo(19451));
            Assert.That(rejected.Message, Does.Contain("ProjectA"));
        }

        [Test]
        public void Lease_ConcurrentAcquisitionProducesExactlyOneOwner()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 303, 404 };
            var root = NewRoot();
            using var first = NewCoordinator(root, "first", 303, "ProjectA", clock, alive);
            using var second = NewCoordinator(root, "second", 404, "ProjectB", clock, alive);
            using var gate = new ManualResetEventSlim(false);

            var firstTask = Task.Run(() =>
            {
                gate.Wait();
                return first.TryAcquire(21673, "shared-token");
            });
            var secondTask = Task.Run(() =>
            {
                gate.Wait();
                return second.TryAcquire(21673, "shared-token");
            });

            gate.Set();
            Assert.That(Task.WaitAll(new Task[] { firstTask, secondTask }, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(new[] { firstTask.Result, secondTask.Result }.Count(result => result.Acquired), Is.EqualTo(1));

            var winner = firstTask.Result.Acquired ? first : second;
            var loser = firstTask.Result.Acquired ? second : first;
            var lease = firstTask.Result.Acquired ? firstTask.Result : secondTask.Result;
            winner.MarkRunning(lease.LeaseId);
            loser.PollNow();

            Assert.That(winner.Snapshot.IsLocalOwner, Is.True);
            Assert.That(loser.Snapshot.HasRemoteOwner, Is.True);
            Assert.That(loser.Snapshot.Port, Is.EqualTo(21673));
        }

        [UnityTest]
        public IEnumerator Handover_RebindsSameHttpEndpointWithSameTokenToDestination()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 505, 606 };
            var root = NewRoot();
            using var first = NewCoordinator(root, "first", 505, "ProjectA", clock, alive);
            using var second = NewCoordinator(root, "second", 606, "ProjectB", clock, alive);
            var port = FindFreePort();
            const string token = "shared-handover-token";
            var firstServer = new HttpMcpServer(new McpToolRegistry());
            var secondServer = new HttpMcpServer(new McpToolRegistry());

            try
            {
                var firstLease = first.TryAcquire(port, token);
                Assert.That(firstLease.Acquired, Is.True);
                firstServer.Start(port, token);
                first.MarkRunning(firstLease.LeaseId);

                var requestId = second.RequestHandover();
                McpServerCoordinationCommand releaseCommand = null;
                first.CommandRequested += command => releaseCommand = command;
                first.PollNow();
                Assert.That(releaseCommand?.Kind, Is.EqualTo(McpServerCoordinationCommandKind.ReleaseForHandover));

                firstServer.Stop("test handover");
                first.ReleaseForHandover(requestId);
                second.PollNow();

                var secondLease = second.TryAcquireHandover(requestId, token);
                Assert.That(secondLease.Acquired, Is.True);
                Assert.That(secondLease.Port, Is.EqualTo(port));
                secondServer.Start(secondLease.Port, token);
                second.MarkRunning(secondLease.LeaseId, requestId);

                var pingTask = PostMcpAsync(port, token, 1, "ping", new JObject());
                while (!pingTask.IsCompleted) yield return null;
                Assert.That(pingTask.IsFaulted, Is.False, pingTask.Exception?.ToString());
                Assert.That(pingTask.Result["error"], Is.Null, pingTask.Result.ToString());

                first.PollNow();
                Assert.That(second.Snapshot.IsLocalOwner, Is.True);
                Assert.That(second.Snapshot.Port, Is.EqualTo(port));
                Assert.That(second.Snapshot.HandoverStatus, Is.EqualTo("completed"));
                Assert.That(first.Snapshot.HasRemoteOwner, Is.True);
                Assert.That(first.Snapshot.OwnerProjectName, Is.EqualTo("ProjectB"));

                var oldOwnerRestart = first.TryAcquire(port, token);
                Assert.That(oldOwnerRestart.Acquired, Is.False);
                Assert.That(oldOwnerRestart.Message, Does.Contain("ProjectB"));
            }
            finally
            {
                firstServer.Dispose();
                secondServer.Dispose();
                second.ReleaseExplicit();
            }
        }

        [Test]
        public void Handover_DestinationWithoutFreshPresenceDoesNotStopOwner()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 607, 708 };
            var root = NewRoot();
            using var first = NewCoordinator(root, "first", 607, "ProjectA", clock, alive);
            var second = NewCoordinator(root, "second", 708, "ProjectB", clock, alive);

            var firstLease = first.TryAcquire(22118, "shared-token");
            first.MarkRunning(firstLease.LeaseId);
            second.RequestHandover();
            second.Dispose();
            alive.Remove(708);

            McpServerCoordinationCommand command = null;
            first.CommandRequested += value => command = value;
            first.PollNow();

            Assert.That(command, Is.Null);
            Assert.That(first.Snapshot.IsLocalOwner, Is.True);
            Assert.That(first.Snapshot.IsTransitioning, Is.False);
            Assert.That(first.Snapshot.HandoverStatus, Is.EqualTo("failed"));
            Assert.That(first.Snapshot.HandoverMessage, Does.Contain("no longer available"));
        }

        [Test]
        public void Reload_OnlyCurrentOwnerCanReacquireStickyEndpoint()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 709, 810 };
            var root = NewRoot();
            var first = NewCoordinator(root, "first", 709, "ProjectA", clock, alive);
            using var second = NewCoordinator(root, "second", 810, "ProjectB", clock, alive);

            var lease = first.TryAcquire(22229, "shared-token");
            first.MarkRunning(lease.LeaseId);
            first.PrepareForReload();
            first.Dispose();

            using var reloaded = NewCoordinator(root, "first", 709, "ProjectA", clock, alive);
            var rejected = second.TryAcquire(22229, "shared-token");
            var reacquired = reloaded.TryAcquire(22229, "shared-token");

            Assert.That(rejected.Acquired, Is.False);
            Assert.That(reacquired.Acquired, Is.True);
            reloaded.MarkRunning(reacquired.LeaseId);
            Assert.That(reloaded.Snapshot.IsLocalOwner, Is.True);
        }

        [Test]
        public void Handover_ChangedTokenFailsAndLetsOriginalOwnerRestore()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 707, 808 };
            var root = NewRoot();
            using var first = NewCoordinator(root, "first", 707, "ProjectA", clock, alive);
            using var second = NewCoordinator(root, "second", 808, "ProjectB", clock, alive);

            var firstLease = first.TryAcquire(22784, "original-token");
            first.MarkRunning(firstLease.LeaseId);
            var requestId = second.RequestHandover();
            first.ReleaseForHandover(requestId);

            var rejected = second.TryAcquireHandover(requestId, "changed-token");
            Assert.That(rejected.Acquired, Is.False);
            Assert.That(second.Snapshot.HandoverStatus, Is.EqualTo("failed"));

            McpServerCoordinationCommand restoreCommand = null;
            first.CommandRequested += command => restoreCommand = command;
            first.PollNow();
            Assert.That(restoreCommand?.Kind, Is.EqualTo(McpServerCoordinationCommandKind.RestoreAfterFailedHandover));

            var restored = first.TryAcquire(22784, "original-token");
            Assert.That(restored.Acquired, Is.True);
            first.MarkRunning(restored.LeaseId);
            Assert.That(first.Snapshot.IsLocalOwner, Is.True);
        }

        [Test]
        public void Handover_LiveUnresponsiveOwnerTimesOutWithoutBeingStolen()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 909, 1001 };
            var root = NewRoot();
            using var first = NewCoordinator(root, "first", 909, "ProjectA", clock, alive);
            using var second = NewCoordinator(root, "second", 1001, "ProjectB", clock, alive);

            var firstLease = first.TryAcquire(23895, "shared-token");
            first.MarkRunning(firstLease.LeaseId);
            second.RequestHandover();
            clock.Advance(TimeSpan.FromSeconds(5));
            second.PollNow();

            Assert.That(second.Snapshot.HandoverStatus, Is.EqualTo("failed"));
            Assert.That(second.Snapshot.HasRemoteOwner, Is.True);
            Assert.That(second.Snapshot.OwnerProjectName, Is.EqualTo("ProjectA"));
            Assert.That(second.Snapshot.HandoverMessage, Does.Contain("did not answer"));

            var rejected = second.TryAcquire(23895, "shared-token");
            Assert.That(rejected.Acquired, Is.False);
        }

        [Test]
        public void Lease_DeadStaleOwnerCanBeRecovered()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 1102, 1203 };
            var root = NewRoot();
            var first = NewCoordinator(root, "first", 1102, "ProjectA", clock, alive);
            using var second = NewCoordinator(root, "second", 1203, "ProjectB", clock, alive);

            var firstLease = first.TryAcquire(24906, "shared-token");
            first.MarkRunning(firstLease.LeaseId);
            first.Dispose();
            alive.Remove(1102);
            clock.Advance(TimeSpan.FromSeconds(10));
            second.PollNow();

            var recovered = second.TryAcquire(24906, "shared-token");
            Assert.That(recovered.Acquired, Is.True);
            Assert.That(recovered.Port, Is.EqualTo(24906));
            second.MarkRunning(recovered.LeaseId);
            Assert.That(second.Snapshot.IsLocalOwner, Is.True);
        }

        [Test]
        public void State_FutureVersionFailsClosedWithoutReplacingLeaseFile()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 1303 };
            var root = NewRoot();
            var statePath = Path.Combine(root, "state.json");
            File.WriteAllText(statePath, "{\"Version\":2}", Encoding.UTF8);

            using var coordinator = NewCoordinator(root, "only", 1303, "ProjectA", clock, alive);
            var exception = Assert.Throws<InvalidDataException>(
                () => coordinator.TryAcquire(25016, "shared-token"));

            Assert.That(exception.Message, Does.Contain("version 2"));
            Assert.That(File.ReadAllText(statePath, Encoding.UTF8), Is.EqualTo("{\"Version\":2}"));
            Assert.That(Directory.GetFiles(root, "state.json.corrupt-*"), Is.Empty);
        }

        [Test]
        public void State_CorruptJsonIsQuarantinedAndDoesNotBlockStartup()
        {
            var clock = new TestClock();
            var alive = new HashSet<int> { 1304 };
            var root = NewRoot();
            File.WriteAllText(Path.Combine(root, "state.json"), "{ definitely-not-json", Encoding.UTF8);

            using var coordinator = NewCoordinator(root, "only", 1304, "ProjectA", clock, alive);
            var acquired = coordinator.TryAcquire(25017, "shared-token");

            Assert.That(acquired.Acquired, Is.True);
            Assert.That(Directory.GetFiles(root, "state.json.corrupt-*"), Is.Not.Empty);
        }

        private string NewRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "SceneAnalyzerCoordinationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _roots.Add(root);
            return root;
        }

        private static McpServerInstanceCoordinator NewCoordinator(
            string root,
            string instanceId,
            int processId,
            string projectName,
            TestClock clock,
            HashSet<int> alive)
        {
            return new McpServerInstanceCoordinator(new McpServerInstanceCoordinatorOptions
            {
                RootPath = root,
                InstanceId = instanceId,
                ProcessId = processId,
                ProjectPath = Path.Combine(root, projectName),
                ProjectName = projectName,
                PackageVersion = "test",
                UtcNow = () => clock.UtcNow,
                IsProcessAlive = alive.Contains,
                SubscribeToEditorUpdate = false,
                HeartbeatInterval = TimeSpan.FromSeconds(1),
                StaleAfter = TimeSpan.FromSeconds(4),
                HandoverTimeout = TimeSpan.FromSeconds(3),
                LockTimeoutMilliseconds = 1000,
            });
        }

        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static async Task<JObject> PostMcpAsync(
            int port, string token, int id, string method, JObject parameters)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpProtocolNegotiator.Preferred);
            request.Content = new StringContent(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JObject(),
            }.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            return JObject.Parse(body);
        }
    }
}
