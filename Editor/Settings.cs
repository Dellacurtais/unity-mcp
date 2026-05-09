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

        // Coder client (Model B — chat panel inside Unity that talks to D:\MCP).
        private const string KCoderUrl = "Maicon.SceneAnalyzer.Coder.Url";
        private const string KCoderToken = "Maicon.SceneAnalyzer.Coder.Token";
        private const string KCoderSessionId = "Maicon.SceneAnalyzer.Coder.SessionId";
        private const string KCoderProfile = "Maicon.SceneAnalyzer.Coder.Profile";
        private const string KCoderAgent = "Maicon.SceneAnalyzer.Coder.Agent";
        private const string KCoderProjectId = "Maicon.SceneAnalyzer.Coder.ProjectId";
        private const string KCoderProvider = "Maicon.SceneAnalyzer.Coder.Provider";
        private const string KCoderModel = "Maicon.SceneAnalyzer.Coder.Model";
        private const string KCoderAutoInjectContext = "Maicon.SceneAnalyzer.Coder.AutoInjectContext";
        private const string KCoderCustomPreamble = "Maicon.SceneAnalyzer.Coder.CustomPreamble";

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
            get => EditorPrefs.GetInt(KCoderProjectId, 1);
            set => EditorPrefs.SetInt(KCoderProjectId, value);
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

        public static string GenerateToken()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
