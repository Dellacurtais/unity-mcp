using System;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor
{
    public enum McpTransportMode
    {
        Embedded = 0,
        Broker = 1,
    }

    /// EditorPrefs-backed settings shared by every Unity Editor process for this user.
    /// The MCP port, token, and sticky flag are intentionally global so a coordinated
    /// handover keeps the client endpoint stable; per-project ownership lives in the
    /// MCP server instance coordinator rather than in these preferences.
    public static class Settings
    {
        private const string KAutoStart = "Maicon.SceneAnalyzer.AutoStart";
        private const string KStickyRunning = "Maicon.SceneAnalyzer.ServerStickyRunning";
        private const string KAllowWrites = "Maicon.SceneAnalyzer.AllowWrites";
        private const string KPort = "Maicon.SceneAnalyzer.Port";
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
        private const string KTransportMode = "Maicon.SceneAnalyzer.TransportMode";
        private const string KBrokerUrl = "Maicon.SceneAnalyzer.BrokerUrl";
        private const string KBrokerAutoConnect = "Maicon.SceneAnalyzer.BrokerAutoConnect";
        private const string KBrokerPythonPath = "Maicon.SceneAnalyzer.BrokerPythonPath";

        // Coder client (Model B — chat panel inside Unity that talks to D:\MCP).
        private const string KCoderUrl = "Maicon.SceneAnalyzer.Coder.Url";
        private const string KCoderToken = "Maicon.SceneAnalyzer.Coder.Token";
        // Session ids became opaque filesystem UUIDs in CodeStudio. Keep them
        // under a new string key so an older numeric EditorPrefs value cannot
        // be coerced into a UUID or trigger a format exception after upgrade.
        private const string KCoderSessionId = "Maicon.SceneAnalyzer.Coder.SessionUuid";
        private const string KCoderProfile = "Maicon.SceneAnalyzer.Coder.Profile";
        private const string KCoderAgent = "Maicon.SceneAnalyzer.Coder.Agent";
        private const string KCoderProjectId = "Maicon.SceneAnalyzer.Coder.ProjectId";
        private const string KCoderProjectWorkdir = "Maicon.SceneAnalyzer.Coder.ProjectWorkdir";
        private const string KCoderProjectName = "Maicon.SceneAnalyzer.Coder.ProjectName";
        private const string KCoderSettingsVersion = "Maicon.SceneAnalyzer.Coder.SettingsVersion";
        private const string KCoderProvider = "Maicon.SceneAnalyzer.Coder.Provider";
        private const string KCoderModel = "Maicon.SceneAnalyzer.Coder.Model";
        private const string KCoderAutoInjectContext = "Maicon.SceneAnalyzer.Coder.AutoInjectContext";
        private const string KCoderCustomPreamble = "Maicon.SceneAnalyzer.Coder.CustomPreamble";
        private const string KCoderThinking = "Maicon.SceneAnalyzer.Coder.Thinking";
        private const string KCoderShowSystem = "Maicon.SceneAnalyzer.Coder.ShowSystem";
        private const string KCoderReasoningEffort = "Maicon.SceneAnalyzer.Coder.ReasoningEffort";
        private const string KCoderExecutionMode = "Maicon.SceneAnalyzer.Coder.ExecutionMode";
        private const string KCoderPlanDepth = "Maicon.SceneAnalyzer.Coder.PlanDepth";
        private const string KCoderToolOutputCapLevel = "Maicon.SceneAnalyzer.Coder.ToolOutputCapLevel";
        private const string KCoderToolPermissionMode = "Maicon.SceneAnalyzer.Coder.ToolPermissionMode";
        private const string KCoderResponseLanguage = "Maicon.SceneAnalyzer.Coder.ResponseLanguage";
        private const string KCoderOutputStyle = "Maicon.SceneAnalyzer.Coder.OutputStyle";
        private const string KCoderMaxTurns = "Maicon.SceneAnalyzer.Coder.MaxTurns";
        private const string KCoderMaxAutoContinues = "Maicon.SceneAnalyzer.Coder.MaxAutoContinues";

        public static bool AutoStartServer
        {
            get => EditorPrefs.GetBool(KAutoStart, false);
            set => EditorPrefs.SetBool(KAutoStart, value);
        }

        /// "Sticky" auto-start shared across projects. Set by a successful manual
        /// Start and cleared by an explicit Stop. The instance coordinator ensures
        /// only the owning Unity process restarts after an assembly reload; other
        /// open projects detect that owner instead of binding the same port.
        public static bool ServerStickyRunning
        {
            get => EditorPrefs.GetBool(KStickyRunning, false);
            set => EditorPrefs.SetBool(KStickyRunning, value);
        }

        public static McpTransportMode TransportMode
        {
            get => (McpTransportMode)EditorPrefs.GetInt(KTransportMode, (int)McpTransportMode.Embedded);
            set => EditorPrefs.SetInt(KTransportMode, (int)value);
        }

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

        public static int Port
        {
            get => EditorPrefs.GetInt(KPort, 8765);
            set => EditorPrefs.SetInt(KPort, value);
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

        public static string CoderUrl
        {
            get => EditorPrefs.GetString(KCoderUrl, "http://localhost:7333");
            set => EditorPrefs.SetString(KCoderUrl, value ?? "");
        }

        public static string CoderToken
        {
            get => EditorPrefs.GetString(KCoderToken, "");
            set => EditorPrefs.SetString(KCoderToken, value ?? "");
        }

        public static string CoderSessionId
        {
            get => EditorPrefs.GetString(KCoderSessionId, "");
            set => EditorPrefs.SetString(KCoderSessionId, value ?? "");
        }

        public static string CoderProfile
        {
            get => EditorPrefs.GetString(KCoderProfile, "auto");
            set => EditorPrefs.SetString(KCoderProfile, value ?? "auto");
        }

        public static string CoderAgent
        {
            get => EditorPrefs.GetString(
                KCoderAgent,
                Maicon.SceneAnalyzer.Editor.Chat.UnityBuildAgentManifest.SelectionId);
            set => EditorPrefs.SetString(
                KCoderAgent,
                value ?? Maicon.SceneAnalyzer.Editor.Chat.UnityBuildAgentManifest.SelectionId);
        }

        public static int CoderProjectId
        {
            // 0 means "no project resolved yet" — Send/CreateSession will
            // auto-detect from the Unity workdir on first use. The earlier
            // default of 1 caused new sessions to silently attach to whatever
            // project happened to have id 1 in the dashboard (usually a
            // legacy/unrelated one).
            get => EditorPrefs.GetInt(KCoderProjectId, 0);
            set => EditorPrefs.SetInt(KCoderProjectId, value);
        }

        /// Workdir that was matched when CoderProjectId was last resolved
        /// (= `Path.GetDirectoryName(Application.dataPath)` of that Unity
        /// project, normalised). Compared against the current Unity workdir
        /// to detect drift — when they differ, the cached id is stale and a
        /// re-resolve is forced.
        public static string CoderProjectWorkdir
        {
            get => EditorPrefs.GetString(KCoderProjectWorkdir, "");
            set => EditorPrefs.SetString(KCoderProjectWorkdir, value ?? "");
        }

        /// Cached project name from the last resolve, purely for UI. Lets the
        /// chat panel show "id #5 — MyGame @ F:\..." instead of just "id 5".
        public static string CoderProjectName
        {
            get => EditorPrefs.GetString(KCoderProjectName, "");
            set => EditorPrefs.SetString(KCoderProjectName, value ?? "");
        }

        /// Bumped on each migration step so Bootstrap can run one-shot fixups
        /// on existing installs without nagging the user.
        public static int CoderSettingsVersion
        {
            get => EditorPrefs.GetInt(KCoderSettingsVersion, 0);
            set => EditorPrefs.SetInt(KCoderSettingsVersion, value);
        }

        public static string CoderProvider
        {
            get => EditorPrefs.GetString(KCoderProvider, "anthropic");
            set => EditorPrefs.SetString(KCoderProvider, value ?? "anthropic");
        }

        public static string CoderModel
        {
            get => EditorPrefs.GetString(KCoderModel, "");
            set => EditorPrefs.SetString(KCoderModel, value ?? "");
        }

        public static bool CoderAutoInjectContext
        {
            get => EditorPrefs.GetBool(KCoderAutoInjectContext, true);
            set => EditorPrefs.SetBool(KCoderAutoInjectContext, value);
        }

        /// Optional override for the auto-injected preamble. When empty, the
        /// plugin builds a default that mentions the Unity workdir, the
        /// `ext_unity__*` MCP tools, and the unity-scene-audit /
        /// unity-scene-author skills. When set, this string is used verbatim.
        public static string CoderCustomPreamble
        {
            get => EditorPrefs.GetString(KCoderCustomPreamble, "");
            set => EditorPrefs.SetString(KCoderCustomPreamble, value ?? "");
        }

        /// "Think mode" toggle. When true, every outgoing message body carries
        /// `thinking: true`, which the D:\MCP coder maps to provider-specific
        /// extended-thinking flags (Anthropic budget_tokens, OpenAI
        /// reasoning_effort, Gemini thinkingConfig).
        public static bool CoderThinking
        {
            get => EditorPrefs.GetBool(KCoderThinking, false);
            set => EditorPrefs.SetBool(KCoderThinking, value);
        }

        /// When false, the chat panel hides ChatRole.System bubbles (the
        /// `[1/3] …` progress notes, `(thinking)` markers, preamble preview
        /// dumps, etc.) — useful once the dev trusts the pipeline and wants
        /// just the conversation. Defaults to true on first install.
        public static bool CoderShowSystemMessages
        {
            get => EditorPrefs.GetBool(KCoderShowSystem, true);
            set => EditorPrefs.SetBool(KCoderShowSystem, value);
        }

        /// Native provider reasoning level. Empty means the model default.
        public static string CoderReasoningEffort
        {
            get => EditorPrefs.GetString(KCoderReasoningEffort, "");
            set => EditorPrefs.SetString(KCoderReasoningEffort, value ?? "");
        }

        /// Provider-independent orchestration mode: standard or ultra.
        public static string CoderExecutionMode
        {
            get => EditorPrefs.GetString(KCoderExecutionMode, "standard");
            set => EditorPrefs.SetString(KCoderExecutionMode, value == "ultra" ? "ultra" : "standard");
        }

        public static string CoderPlanDepth
        {
            get => EditorPrefs.GetString(KCoderPlanDepth, "normal");
            set
            {
                var normalized = value == "simple" || value == "complex" ? value : "normal";
                EditorPrefs.SetString(KCoderPlanDepth, normalized);
            }
        }

        public static string CoderToolOutputCapLevel
        {
            get => EditorPrefs.GetString(KCoderToolOutputCapLevel, "economic");
            set => EditorPrefs.SetString(KCoderToolOutputCapLevel, NormalizeToolOutputCap(value));
        }

        public static string CoderToolPermissionMode
        {
            get => EditorPrefs.GetString(KCoderToolPermissionMode, "auto");
            set
            {
                var normalized = value == "ask" || value == "unrestricted" ? value : "auto";
                EditorPrefs.SetString(KCoderToolPermissionMode, normalized);
            }
        }

        public static string CoderResponseLanguage
        {
            get => EditorPrefs.GetString(KCoderResponseLanguage, "");
            set => EditorPrefs.SetString(KCoderResponseLanguage, value ?? "");
        }

        public static string CoderOutputStyle
        {
            get => EditorPrefs.GetString(KCoderOutputStyle, "");
            set
            {
                var normalized = value == "explanatory" || value == "learning" ? value : "";
                EditorPrefs.SetString(KCoderOutputStyle, normalized);
            }
        }

        /// Zero means unlimited, matching the runtime's null max_turns.
        public static int CoderMaxTurns
        {
            get => Math.Max(0, EditorPrefs.GetInt(KCoderMaxTurns, 0));
            set => EditorPrefs.SetInt(KCoderMaxTurns, Math.Max(0, value));
        }

        public static int CoderMaxAutoContinues
        {
            get => Math.Max(0, Math.Min(1000, EditorPrefs.GetInt(KCoderMaxAutoContinues, 15)));
            set => EditorPrefs.SetInt(KCoderMaxAutoContinues, Math.Max(0, Math.Min(1000, value)));
        }

        private static string NormalizeToolOutputCap(string value)
        {
            switch (value)
            {
                case "medium":
                case "high":
                case "extra":
                case "max":
                case "ultra":
                    return value;
                default:
                    return "economic";
            }
        }

        public static string GenerateToken()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
