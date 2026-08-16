using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Maicon.SceneAnalyzer.Editor.AI3D.Tests
{
    public sealed class Ai3dGatewayProcessTests
    {
        [Test]
        public void PythonCandidates_PreferConfiguredAndRemoveDuplicates()
        {
            var candidates = Ai3dGatewayProcess.BuildPythonCandidates(
                " C:/Python/python.exe ",
                "c:/python/PYTHON.exe",
                "C:/Managed/python.exe");

            Assert.That(candidates.Select(item => item.FileName).ToArray(), Is.EqualTo(new[]
            {
                "C:/Python/python.exe",
                "C:/Managed/python.exe",
                "python3",
                "python",
            }));
        }

        [Test]
        public void ProcessSnapshot_DefaultsToSafeStoppedControls()
        {
            var snapshot = new Ai3dGatewayProcessSnapshot
            {
                State = Ai3dGatewayProcessState.Stopped,
                CanStart = true,
                CanStop = false,
            };

            Assert.That(snapshot.CanStart, Is.True);
            Assert.That(snapshot.CanStop, Is.False);
            Assert.That(snapshot.IsExternal, Is.False);
        }

        [Test]
        public void ProcessStartTimestamp_PreservesUtcInstantWhenJsonNetCreatesDateToken()
        {
            var state = JObject.Parse("{\"processStartUtc\":\"2026-08-12T01:34:04.1087143Z\"}");

            Assert.That(Ai3dGatewayProcess.TryReadUtcTimestamp(
                state["processStartUtc"], out var timestamp), Is.True);
            Assert.That(timestamp.UtcDateTime, Is.EqualTo(
                new DateTime(2026, 8, 12, 1, 34, 4, 108, DateTimeKind.Utc).AddTicks(7143)));
        }
    }
}
