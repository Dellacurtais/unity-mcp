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

        public static string GenerateToken()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
