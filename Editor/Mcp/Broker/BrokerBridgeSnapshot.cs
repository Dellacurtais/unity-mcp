using System;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Broker
{
    public sealed class BrokerBridgeSnapshot
    {
        public static readonly BrokerBridgeSnapshot Stopped = new BrokerBridgeSnapshot(
            false, "stopped", null, null, 0, null, null, DateTimeOffset.MinValue);

        public bool IsConnected { get; }
        public string State { get; }
        public string Error { get; }
        public string ProjectKey { get; }
        public int ProcessId { get; }
        public string GenerationId { get; }
        public string RegistryHash { get; }
        public DateTimeOffset UpdatedUtc { get; }

        public BrokerBridgeSnapshot(
            bool isConnected,
            string state,
            string error,
            string projectKey,
            int processId,
            string generationId,
            string registryHash,
            DateTimeOffset updatedUtc)
        {
            IsConnected = isConnected;
            State = state ?? "unknown";
            Error = error;
            ProjectKey = projectKey;
            ProcessId = processId;
            GenerationId = generationId;
            RegistryHash = registryHash;
            UpdatedUtc = updatedUtc;
        }
    }
}
