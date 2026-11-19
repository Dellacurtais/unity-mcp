using System;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Broker
{
    /// One bearer token for the Editor, the broker and every MCP client.
    ///
    /// MCP clients (Claude Code, Codex) read the per-user variable UNITY_MCP_BROKER_TOKEN;
    /// the Editor kept its own copy in EditorPrefs and launched the broker with it. When
    /// the two drifted apart (a regenerated token, another Editor, a crash) every client
    /// call failed with 401 after the Editor reopened. Now the user variable wins: the
    /// Editor adopts it on every load, and creating or rotating a token updates both.
    internal static class BrokerTokenSync
    {
        private const int MinimumLength = 16;

        internal static string LastChange { get; private set; }

        internal static string Converge()
        {
            var prefs = Settings.Token;
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                if (!IsUsable(prefs)) Settings.Token = prefs = Settings.GenerateToken();
                return prefs;
            }

            string user = null;
            try
            {
                user = Environment.GetEnvironmentVariable(
                    ExternalMcpClientInstaller.TokenEnvironmentVariable, EnvironmentVariableTarget.User);
            }
            catch (Exception) { }

            if (IsUsable(user))
            {
                user = user.Trim();
                if (!string.Equals(prefs, user, StringComparison.Ordinal))
                {
                    Settings.Token = user;
                    LastChange = "EditorPrefs token replaced by the per-user UNITY_MCP_BROKER_TOKEN that MCP clients use";
                    Debug.Log("[unity-mcp] " + LastChange);
                }
                // Processes this Editor starts (the managed broker) inherit the same value.
                Environment.SetEnvironmentVariable(
                    ExternalMcpClientInstaller.TokenEnvironmentVariable, user, EnvironmentVariableTarget.Process);
                return user;
            }

            if (!IsUsable(prefs)) Settings.Token = prefs = Settings.GenerateToken();
            ExternalMcpClientInstaller.PersistToken(prefs);
            LastChange = "per-user UNITY_MCP_BROKER_TOKEN created from the EditorPrefs token";
            return prefs;
        }

        /// A new token everywhere. The broker accepts the new user value at once (it re-reads
        /// it); clients configured with the headersHelper pick it up on their next request.
        internal static string Rotate()
        {
            var token = Settings.GenerateToken();
            Settings.Token = token;
            if (Application.platform == RuntimePlatform.WindowsEditor)
                ExternalMcpClientInstaller.PersistToken(token);
            LastChange = "token rotated";
            return token;
        }

        private static bool IsUsable(string token) =>
            !string.IsNullOrWhiteSpace(token) && token.Trim().Length >= MinimumLength;
    }
}
