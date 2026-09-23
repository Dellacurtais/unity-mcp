using Maicon.SceneAnalyzer.Editor.Mcp;
using NUnit.Framework;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class AssetRefreshIdleTrackerTests
    {
        [Test]
        public void FirstIdleCallback_DoesNotCompleteQueuedRefresh()
        {
            var tracker = new AssetRefreshIdleTracker();
            Assert.That(tracker.Observe(10d, false, false, 1), Is.False);
            Assert.That(tracker.Observe(10.075d, false, false, 1), Is.False);
            Assert.That(tracker.Observe(10.2d, true, false, 1), Is.False);
            Assert.That(tracker.Observe(11d, false, false, 1), Is.False);
            Assert.That(tracker.Observe(11.5d, false, false, 1), Is.True);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void BusyEditor_RestartsEntireQuietInterval(bool compiling, bool updating)
        {
            var tracker = new AssetRefreshIdleTracker();
            Assert.That(tracker.Observe(1d, false, false, 1), Is.False);
            Assert.That(tracker.Observe(2d, compiling, updating, 1), Is.False);
            Assert.That(tracker.Observe(3d, false, false, 1), Is.False);
            Assert.That(tracker.Observe(3.25d, false, false, 1), Is.False);
            Assert.That(tracker.Observe(3.5d, false, false, 1), Is.True);
        }

        [Test]
        public void ImportBetweenUpdates_RestartsQuietIntervalEvenWhenBothSamplesAreIdle()
        {
            var tracker = new AssetRefreshIdleTracker();
            Assert.That(tracker.Observe(1d, false, false, 10), Is.False);
            Assert.That(tracker.Observe(2d, false, false, 11), Is.False);
            Assert.That(tracker.Observe(2.25d, false, false, 11), Is.False);
            Assert.That(tracker.Observe(2.5d, false, false, 11), Is.True);
        }

        [Test]
        public void NewRefresh_DoesNotReusePreviousIdleObservation()
        {
            var tracker = new AssetRefreshIdleTracker();
            tracker.Observe(1d, false, false, 1);
            Assert.That(tracker.Observe(2d, false, false, 1), Is.True);
            tracker.Reset();
            Assert.That(tracker.Observe(3d, false, false, 1), Is.False);
        }
    }
}
