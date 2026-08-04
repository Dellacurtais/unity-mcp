using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    internal sealed class ExternalMcpClientSnapshot
    {
        public string State { get; }
        public bool IsConfigured { get; }
        public bool NeedsUpdate { get; }
        public string Error { get; }

        public ExternalMcpClientSnapshot(
            string state, bool isConfigured, bool needsUpdate, string error = null)
        {
            State = state;
            IsConfigured = isConfigured;
            NeedsUpdate = needsUpdate;
            Error = error;
        }
    }

    /// Installs the current Unity MCP endpoint into the global configuration
    /// used by ChatGPT desktop and Claude Code. Both clients resolve the bearer
    /// token from a user-scoped environment variable, so their config files do
    /// not contain the secret itself.
    internal static class ExternalMcpClientInstaller
    {
        internal const string TokenEnvironmentVariable = "UNITY_MCP_BROKER_TOKEN";
        internal const string ChatGptServerName = "unity";
        internal const string ClaudeServerName = "unity-mcp";

        private const string ClaudeLegacyServerName = "unity";
        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;
        private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);

        private static readonly Regex AnyTomlTable = new Regex(
            @"^\s*\[", RegexOptions.Compiled);
        private static readonly Regex ChatGptMainTable = new Regex(
            "^\\s*\\[\\s*mcp_servers\\s*\\.\\s*(?:unity|\"unity\")\\s*\\]\\s*(?:#.*)?$",
            RegexOptions.Compiled);
        private static readonly Regex ChatGptEnvTable = new Regex(
            "^\\s*\\[\\s*mcp_servers\\s*\\.\\s*(?:unity|\"unity\")\\s*\\.\\s*env\\s*\\]\\s*(?:#.*)?$",
            RegexOptions.Compiled);
        private static readonly HashSet<string> ControlledChatGptKeys =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "url",
                "bearer_token_env_var",
                "enabled",
                "auth",
                "command",
                "args",
                "cwd",
                "env",
                "env_vars",
                "http_headers",
                "env_http_headers",
            };

        internal static string ChatGptConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "config.toml");

        internal static string ClaudeConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude.json");

        internal static ExternalMcpClientSnapshot InspectChatGpt(string endpoint, string token)
        {
            try
            {
                if (!File.Exists(ChatGptConfigPath))
                    return NotInstalled();

                var config = File.ReadAllText(ChatGptConfigPath);
                if (!ContainsChatGptServer(config)) return NotInstalled();

                var current = IsChatGptConfigCurrent(config, endpoint)
                              && IsPersistedTokenCurrent(token);
                return current ? Installed() : UpdateAvailable();
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }
        }

        internal static ExternalMcpClientSnapshot InspectClaudeCode(string endpoint, string token)
        {
            try
            {
                if (!File.Exists(ClaudeConfigPath)) return NotInstalled();
                var root = ReadJsonObject(ClaudeConfigPath);
                var servers = root["mcpServers"] as JObject;
                var serverName = FindClaudeServerName(servers, existingOnly: true);
                if (serverName == null) return NotInstalled();

                var current = IsClaudeConfigCurrent(servers[serverName] as JObject, endpoint)
                              && IsPersistedTokenCurrent(token);
                return current ? Installed() : UpdateAvailable();
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }
        }

        internal static void InstallOrUpdateChatGpt(string endpoint, string token)
        {
            ValidateInstallArguments(endpoint, token);
            PersistToken(token);

            var path = ChatGptConfigPath;
            var original = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var updated = UpsertChatGptConfig(original, endpoint);
            WriteAllTextAtomic(path, updated);
        }

        internal static void InstallOrUpdateClaudeCode(string endpoint, string token)
        {
            ValidateInstallArguments(endpoint, token);
            PersistToken(token);

            var path = ClaudeConfigPath;
            var root = File.Exists(path) ? ReadJsonObject(path) : new JObject();
            UpsertClaudeConfig(root, endpoint);
            WriteAllTextAtomic(path, root.ToString(Formatting.Indented) + Environment.NewLine);
        }

        internal static string UpsertChatGptConfig(string original, string endpoint)
        {
            original = original ?? string.Empty;
            var newline = original.Contains("\r\n") ? "\r\n" : "\n";
            var normalized = original.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = new List<string>(normalized.Split('\n'));

            RemoveChatGptEnvironmentTables(lines);

            var tableIndex = FindLine(lines, ChatGptMainTable, 0);
            if (tableIndex < 0)
            {
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                    lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add(string.Empty);
                lines.Add("[mcp_servers." + ChatGptServerName + "]");
                lines.Add("url = \"" + EscapeTomlString(endpoint) + "\"");
                lines.Add("bearer_token_env_var = \"" + TokenEnvironmentVariable + "\"");
                lines.Add("enabled = true");
                lines.Add(string.Empty);
                return string.Join(newline, lines);
            }

            var tableEnd = FindNextTomlTable(lines, tableIndex + 1);
            RemoveControlledChatGptAssignments(lines, tableIndex + 1, tableEnd);
            lines.Insert(tableIndex + 1, "enabled = true");
            lines.Insert(tableIndex + 1,
                "bearer_token_env_var = \"" + TokenEnvironmentVariable + "\"");
            lines.Insert(tableIndex + 1, "url = \"" + EscapeTomlString(endpoint) + "\"");

            return string.Join(newline, lines);
        }

        internal static JObject UpsertClaudeConfig(JObject root, string endpoint)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            var servers = root["mcpServers"] as JObject;
            if (root["mcpServers"] != null && servers == null)
                throw new InvalidDataException("Claude Code mcpServers must be a JSON object.");
            if (servers == null)
            {
                servers = new JObject();
                root["mcpServers"] = servers;
            }

            var serverName = FindClaudeServerName(servers, existingOnly: false);
            var server = servers[serverName] as JObject ?? new JObject();
            server.Remove("command");
            server.Remove("args");
            server.Remove("env");
            server["type"] = "http";
            server["url"] = endpoint;

            var headers = server["headers"] as JObject ?? new JObject();
            headers["Authorization"] = "Bearer ${" + TokenEnvironmentVariable + "}";
            server["headers"] = headers;
            servers[serverName] = server;
            return root;
        }

        internal static bool IsChatGptConfigCurrent(string config, string endpoint)
        {
            var section = GetChatGptMainSection(config);
            if (section == null) return false;

            return HasTomlStringAssignment(section, "url", endpoint)
                   && HasTomlStringAssignment(
                       section, "bearer_token_env_var", TokenEnvironmentVariable)
                   && !HasTomlBooleanAssignment(section, "enabled", false)
                   && !HasTomlAssignment(section, "command")
                   && !HasTomlAssignment(section, "args")
                   && !HasTomlAssignment(section, "http_headers")
                   && !HasTomlAssignment(section, "env_http_headers");
        }

        internal static bool IsClaudeConfigCurrent(JObject server, string endpoint)
        {
            if (server == null) return false;
            return string.Equals((string)server["type"], "http", StringComparison.OrdinalIgnoreCase)
                   && string.Equals((string)server["url"], endpoint, StringComparison.Ordinal)
                   && server["command"] == null
                   && server["args"] == null
                   && server["env"] == null
                   && string.Equals(
                       (string)server["headers"]?["Authorization"],
                       "Bearer ${" + TokenEnvironmentVariable + "}",
                       StringComparison.Ordinal);
        }

        private static void ValidateInstallArguments(string endpoint, string token)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsLoopback)
                throw new ArgumentException("Unity MCP endpoint must be a loopback HTTP URL.", nameof(endpoint));
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Start Unity MCP once to generate an authentication token.", nameof(token));
            if (Application.platform != RuntimePlatform.WindowsEditor)
                throw new PlatformNotSupportedException(
                    "One-click ChatGPT/Claude Code installation currently supports Windows Editor only.");
        }

        private static void PersistToken(string token)
        {
            Environment.SetEnvironmentVariable(
                TokenEnvironmentVariable, token, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(
                TokenEnvironmentVariable, token, EnvironmentVariableTarget.Process);
            BroadcastEnvironmentChange();
        }

        private static bool IsPersistedTokenCurrent(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (Application.platform != RuntimePlatform.WindowsEditor) return false;
            return string.Equals(
                Environment.GetEnvironmentVariable(
                    TokenEnvironmentVariable, EnvironmentVariableTarget.User),
                token,
                StringComparison.Ordinal);
        }

        private static void BroadcastEnvironmentChange()
        {
            try
            {
                SendMessageTimeout(
                    HwndBroadcast,
                    WmSettingChange,
                    UIntPtr.Zero,
                    "Environment",
                    SmtoAbortIfHung,
                    1000,
                    out _);
            }
            catch (Exception)
            {
                // The registry value is already persisted. A client restart
                // still picks it up even if the shell ignores the broadcast.
            }
        }

        private static JObject ReadJsonObject(string path)
        {
            try
            {
                using (var reader = File.OpenText(path))
                using (var json = new JsonTextReader(reader)
                       {
                           DateParseHandling = DateParseHandling.None,
                       })
                    return JObject.Load(
                        json,
                        new JsonLoadSettings
                        {
                            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        });
            }
            catch (Exception exception) when (exception is JsonException || exception is IOException)
            {
                throw new InvalidDataException(
                    "Could not safely read " + path + ": " + exception.Message,
                    exception);
            }
        }

        private static string FindClaudeServerName(JObject servers, bool existingOnly)
        {
            if (servers?[ClaudeServerName] != null) return ClaudeServerName;
            if (servers?[ClaudeLegacyServerName] != null) return ClaudeLegacyServerName;
            return existingOnly ? null : ClaudeServerName;
        }

        private static bool ContainsChatGptServer(string config)
            => GetChatGptMainSection(config) != null;

        private static string GetChatGptMainSection(string config)
        {
            var lines = (config ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');
            var start = FindLine(lines, ChatGptMainTable, 0);
            if (start < 0) return null;
            var end = FindNextTomlTable(lines, start + 1);
            return string.Join("\n", lines, start + 1, end - start - 1);
        }

        private static void RemoveChatGptEnvironmentTables(List<string> lines)
        {
            for (var index = lines.Count - 1; index >= 0; index--)
            {
                if (!ChatGptEnvTable.IsMatch(lines[index])) continue;
                var end = FindNextTomlTable(lines, index + 1);
                lines.RemoveRange(index, end - index);
            }
        }

        private static void RemoveControlledChatGptAssignments(
            List<string> lines, int start, int end)
        {
            for (var index = start; index < end;)
            {
                var match = Regex.Match(lines[index], @"^\s*([A-Za-z0-9_-]+)\s*=");
                if (!match.Success || !ControlledChatGptKeys.Contains(match.Groups[1].Value))
                {
                    index++;
                    continue;
                }

                var assignmentEnd = FindTomlAssignmentEnd(lines, index, end);
                var count = Math.Max(1, assignmentEnd - index);
                lines.RemoveRange(index, count);
                end -= count;
            }
        }

        private static int FindTomlAssignmentEnd(
            IReadOnlyList<string> lines, int start, int sectionEnd)
        {
            var squareDepth = 0;
            var curlyDepth = 0;
            var sawContainer = false;
            var inSingleQuotedString = false;
            var inDoubleQuotedString = false;
            var escaped = false;

            for (var lineIndex = start; lineIndex < sectionEnd; lineIndex++)
            {
                var line = lines[lineIndex];
                var offset = lineIndex == start ? line.IndexOf('=') + 1 : 0;
                for (var charIndex = Math.Max(0, offset); charIndex < line.Length; charIndex++)
                {
                    var character = line[charIndex];
                    if (inDoubleQuotedString)
                    {
                        if (escaped) escaped = false;
                        else if (character == '\\') escaped = true;
                        else if (character == '"') inDoubleQuotedString = false;
                        continue;
                    }
                    if (inSingleQuotedString)
                    {
                        if (character == '\'') inSingleQuotedString = false;
                        continue;
                    }
                    if (character == '#') break;
                    if (character == '"') inDoubleQuotedString = true;
                    else if (character == '\'') inSingleQuotedString = true;
                    else if (character == '[') { squareDepth++; sawContainer = true; }
                    else if (character == ']') squareDepth = Math.Max(0, squareDepth - 1);
                    else if (character == '{') { curlyDepth++; sawContainer = true; }
                    else if (character == '}') curlyDepth = Math.Max(0, curlyDepth - 1);
                }

                if (!inSingleQuotedString
                    && !inDoubleQuotedString
                    && squareDepth == 0
                    && curlyDepth == 0)
                    return lineIndex + 1;
                if (!sawContainer) return lineIndex + 1;
            }
            return sectionEnd;
        }

        private static int FindLine(IReadOnlyList<string> lines, Regex pattern, int start)
        {
            for (var index = start; index < lines.Count; index++)
            {
                if (pattern.IsMatch(lines[index])) return index;
            }
            return -1;
        }

        private static int FindNextTomlTable(IReadOnlyList<string> lines, int start)
        {
            var next = FindLine(lines, AnyTomlTable, start);
            return next < 0 ? lines.Count : next;
        }

        private static bool HasTomlAssignment(string section, string key)
            => Regex.IsMatch(
                section ?? string.Empty,
                @"(?m)^\s*" + Regex.Escape(key) + @"\s*=",
                RegexOptions.CultureInvariant);

        private static bool HasTomlStringAssignment(string section, string key, string value)
            => Regex.IsMatch(
                section ?? string.Empty,
                "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*\""
                + Regex.Escape(EscapeTomlString(value)) + "\"\\s*(?:#.*)?$",
                RegexOptions.CultureInvariant);

        private static bool HasTomlBooleanAssignment(string section, string key, bool value)
            => Regex.IsMatch(
                section ?? string.Empty,
                @"(?im)^\s*" + Regex.Escape(key) + @"\s*=\s*"
                + (value ? "true" : "false") + @"\s*(?:#.*)?$",
                RegexOptions.CultureInvariant);

        private static string EscapeTomlString(string value)
            => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static void WriteAllTextAtomic(string path, string contents)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("Configuration path has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporary = path + ".unity-mcp-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, contents ?? string.Empty, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static ExternalMcpClientSnapshot NotInstalled()
            => new ExternalMcpClientSnapshot("Not installed", false, false);

        private static ExternalMcpClientSnapshot Installed()
            => new ExternalMcpClientSnapshot("Installed", true, false);

        private static ExternalMcpClientSnapshot UpdateAvailable()
            => new ExternalMcpClientSnapshot("Update available", true, true);

        private static ExternalMcpClientSnapshot Failed(Exception exception)
            => new ExternalMcpClientSnapshot(
                "Configuration error", true, true, exception.GetBaseException().Message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint message,
            UIntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);
    }
}
