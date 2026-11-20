using System;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor
{
    /// EditorPrefs-backed settings shared by every Unity Editor process for this user.
    /// The token, broker URL and safety switches are global so every Editor talks to the
    /// same per-user broker with the same credentials.
    public static class Settings
    {
        private const string KAllowWrites = "Maicon.SceneAnalyzer.AllowWrites";
        private const string KToken = "Maicon.SceneAnalyzer.Token";
        private const string KMaxBatchOps = "Maicon.SceneAnalyzer.MaxBatchOps";
        private const string KSnapshotIncludeRendererSummary = "Maicon.SceneAnalyzer.SnapshotIncludeRendererSummary";
        private const string KMaxRequestBodyBytes = "Maicon.SceneAnalyzer.MaxRequestBodyBytes";
        private const string KMaxConcurrentRequests = "Maicon.SceneAnalyzer.MaxConcurrentRequests";
        private const string KToolTimeoutSeconds = "Maicon.SceneAnalyzer.ToolTimeoutSeconds";
        private const string KRequireHighRiskConfirmation = "Maicon.SceneAnalyzer.RequireHighRiskConfirmation";
        private const string KAutoActivateRequiredWindows = "Maicon.SceneAnalyzer.AutoActivateRequiredWindows";
        private const string KSafetySettingsVersion = "Maicon.SceneAnalyzer.SafetySettingsVersion";
        private const string KAllowReflectionTools = "Maicon.SceneAnalyzer.AllowReflectionTools";
        private const string KAllowDestructiveTools = "Maicon.SceneAnalyzer.AllowDestructiveTools";
        private const string KAllowPackageTools = "Maicon.SceneAnalyzer.AllowPackageTools";
        private const string KAllowBuildTools = "Maicon.SceneAnalyzer.AllowBuildTools";
        private const string KAllowProjectSettingsTools = "Maicon.SceneAnalyzer.AllowProjectSettingsTools";
        private const string KAllowPlayControlTools = "Maicon.SceneAnalyzer.AllowPlayControlTools";
        private const string KAllowRuntimeCommandTools = "Maicon.SceneAnalyzer.AllowRuntimeCommandTools";
        private const string KAllowExternalServiceTools = "Maicon.SceneAnalyzer.AllowExternalServiceTools";
        private const string KBrokerUrl = "Maicon.SceneAnalyzer.BrokerUrl";
        private const string KBrokerAutoConnect = "Maicon.SceneAnalyzer.BrokerAutoConnect";
        private const string KBrokerPythonPath = "Maicon.SceneAnalyzer.BrokerPythonPath";

        public static string BrokerUrl
        {
            get => EditorPrefs.GetString(KBrokerUrl, "ws://127.0.0.1:8765/bridge");
            set => EditorPrefs.SetString(KBrokerUrl, value ?? string.Empty);
        }

        public static bool BrokerAutoConnect
        {
            get => EditorPrefs.GetBool(KBrokerAutoConnect, true);
            set => EditorPrefs.SetBool(KBrokerAutoConnect, value);
        }

        public static string BrokerPythonPath
        {
            get => EditorPrefs.GetString(KBrokerPythonPath, string.Empty);
            set => EditorPrefs.SetString(KBrokerPythonPath, value ?? string.Empty);
        }

        public static bool AllowWrites
        {
            get => EditorPrefs.GetBool(KAllowWrites, true);
            set => EditorPrefs.SetBool(KAllowWrites, value);
        }

        public static string Token
        {
            get => EditorPrefs.GetString(KToken, "");
            set => EditorPrefs.SetString(KToken, value ?? "");
        }

        public static int MaxBatchOps
        {
            get => EditorPrefs.GetInt(KMaxBatchOps, 500);
            set => EditorPrefs.SetInt(KMaxBatchOps, value);
        }

        /// When true, SceneSnapshot.Refresh populates a per-Renderer summary
        /// (materials, shadow casting, batchable static, world bounds) keyed by
        /// renderer InstanceID. Off by default — the populate adds 10-50ms per
        /// refresh on medium scenes (sharedMaterials + AssetDatabase paths +
        /// r.bounds force-compute). Turn on when an analyzer needs the cache.
        public static bool SnapshotIncludeRendererSummary
        {
            get => EditorPrefs.GetBool(KSnapshotIncludeRendererSummary, false);
            set => EditorPrefs.SetBool(KSnapshotIncludeRendererSummary, value);
        }

        public static int MaxRequestBodyBytes
        {
            get => Math.Max(64 * 1024, EditorPrefs.GetInt(KMaxRequestBodyBytes, 8 * 1024 * 1024));
            set => EditorPrefs.SetInt(KMaxRequestBodyBytes, Math.Max(64 * 1024, value));
        }

        public static int MaxConcurrentRequests
        {
            get => Math.Max(1, Math.Min(32, EditorPrefs.GetInt(KMaxConcurrentRequests, 8)));
            set => EditorPrefs.SetInt(KMaxConcurrentRequests, Math.Max(1, Math.Min(32, value)));
        }

        public static int ToolTimeoutSeconds
        {
            get => Math.Max(1, Math.Min(3600, EditorPrefs.GetInt(KToolTimeoutSeconds, 900)));
            set => EditorPrefs.SetInt(KToolTimeoutSeconds, Math.Max(1, Math.Min(3600, value)));
        }

        public static bool RequireHighRiskConfirmation
        {
            get => EditorPrefs.GetBool(KRequireHighRiskConfirmation, false);
            set => EditorPrefs.SetBool(KRequireHighRiskConfirmation, value);
        }

        public static bool AutoActivateRequiredWindows
        {
            get => EditorPrefs.GetBool(KAutoActivateRequiredWindows, true);
            set => EditorPrefs.SetBool(KAutoActivateRequiredWindows, value);
        }

        public static int SafetySettingsVersion
        {
            get => EditorPrefs.GetInt(KSafetySettingsVersion, 0);
            set => EditorPrefs.SetInt(KSafetySettingsVersion, value);
        }

        public static bool AllowReflectionTools
        {
            get => EditorPrefs.GetBool(KAllowReflectionTools, true);
            set => EditorPrefs.SetBool(KAllowReflectionTools, value);
        }

        public static bool AllowDestructiveTools
        {
            get => EditorPrefs.GetBool(KAllowDestructiveTools, true);
            set => EditorPrefs.SetBool(KAllowDestructiveTools, value);
        }

        public static bool AllowPackageTools
        {
            get => EditorPrefs.GetBool(KAllowPackageTools, true);
            set => EditorPrefs.SetBool(KAllowPackageTools, value);
        }

        public static bool AllowBuildTools
        {
            get => EditorPrefs.GetBool(KAllowBuildTools, true);
            set => EditorPrefs.SetBool(KAllowBuildTools, value);
        }

        public static bool AllowProjectSettingsTools
        {
            get => EditorPrefs.GetBool(KAllowProjectSettingsTools, true);
            set => EditorPrefs.SetBool(KAllowProjectSettingsTools, value);
        }

        public static bool AllowPlayControlTools
        {
            get => EditorPrefs.GetBool(KAllowPlayControlTools, true);
            set => EditorPrefs.SetBool(KAllowPlayControlTools, value);
        }

        public static bool AllowRuntimeCommandTools
        {
            get => EditorPrefs.GetBool(KAllowRuntimeCommandTools, true);
            set => EditorPrefs.SetBool(KAllowRuntimeCommandTools, value);
        }

        /// <summary>
        /// Opt-in gate for tools that can disclose data to or spend credits on an
        /// external provider. Kept disabled by default independently of ordinary
        /// Unity writes so connecting an MCP client cannot authorize paid work.
        /// </summary>
        public static bool AllowExternalServiceTools
        {
            get => EditorPrefs.GetBool(KAllowExternalServiceTools, false);
            set => EditorPrefs.SetBool(KAllowExternalServiceTools, value);
        }

        public static string GenerateToken()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
