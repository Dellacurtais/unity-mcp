using System;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor
{
    /// EditorPrefs-backed settings shared between the window, the bootstrap, and the
    /// HTTP server. Keys are namespaced so they survive across projects on the same
    /// machine without collisions.
    public static class Settings
    {
        private const string KAutoStart = "Maicon.SceneAnalyzer.AutoStart";
        private const string KAllowWrites = "Maicon.SceneAnalyzer.AllowWrites";
        private const string KPort = "Maicon.SceneAnalyzer.Port";
        private const string KToken = "Maicon.SceneAnalyzer.Token";
        private const string KMaxBatchOps = "Maicon.SceneAnalyzer.MaxBatchOps";
        private const string KSnapshotIncludeRendererSummary = "Maicon.SceneAnalyzer.SnapshotIncludeRendererSummary";

        // Coder client (Model B — chat panel inside Unity that talks to D:\MCP).
        private const string KCoderUrl = "Maicon.SceneAnalyzer.Coder.Url";
        private const string KCoderToken = "Maicon.SceneAnalyzer.Coder.Token";
        private const string KCoderSessionId = "Maicon.SceneAnalyzer.Coder.SessionId";
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

        public static bool AutoStartServer
        {
            get => EditorPrefs.GetBool(KAutoStart, false);
            set => EditorPrefs.SetBool(KAutoStart, value);
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

        public static string CoderUrl
        {
            get => EditorPrefs.GetString(KCoderUrl, "http://localhost:3333");
            set => EditorPrefs.SetString(KCoderUrl, value ?? "");
        }

        public static string CoderToken
        {
            get => EditorPrefs.GetString(KCoderToken, "");
            set => EditorPrefs.SetString(KCoderToken, value ?? "");
        }

        public static int CoderSessionId
        {
            get => EditorPrefs.GetInt(KCoderSessionId, 0);
            set => EditorPrefs.SetInt(KCoderSessionId, value);
        }

        public static string CoderProfile
        {
            get => EditorPrefs.GetString(KCoderProfile, "auto");
            set => EditorPrefs.SetString(KCoderProfile, value ?? "auto");
        }

        public static string CoderAgent
        {
            get => EditorPrefs.GetString(KCoderAgent, "build");
            set => EditorPrefs.SetString(KCoderAgent, value ?? "build");
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
        /// `ext_unity_editor__*` MCP tools, and the unity-scene-audit /
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

        public static string GenerateToken()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
