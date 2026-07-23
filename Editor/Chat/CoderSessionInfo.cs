using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    public sealed class CoderSessionInfo
    {
        public int Id;
        public int ProjectId;
        public string Title;
        public string Provider;
        public string Model;
        public string Status;
        public string Agent;
        public string ToolPermissionMode;
        public bool ThinkingEnabled;
        public DateTime? CreatedAt;
        public DateTime? UpdatedAt;
        public int TotalInputTokens;
        public int TotalOutputTokens;

        public static CoderSessionInfo FromJson(JToken token)
        {
            if (token == null) return null;
            return new CoderSessionInfo
            {
                Id = (int?)token["id"] ?? 0,
                ProjectId = (int?)token["project_id"] ?? 0,
                Title = (string)token["title"],
                Provider = (string)token["provider"],
                Model = (string)token["model"],
                Status = (string)token["status"],
                Agent = (string)token["agent"],
                ToolPermissionMode = (string)token["tool_permission_mode"],
                ThinkingEnabled = (bool?)token["thinking_enabled"] ?? false,
                CreatedAt = ParseDate((string)token["created_at"]),
                UpdatedAt = ParseDate((string)token["updated_at"]),
                TotalInputTokens = (int?)token["total_input_tokens"] ?? 0,
                TotalOutputTokens = (int?)token["total_output_tokens"] ?? 0,
            };
        }

        public bool SemanticallyEquals(CoderSessionInfo other)
        {
            return other != null
                && Id == other.Id
                && ProjectId == other.ProjectId
                && string.Equals(Title, other.Title, StringComparison.Ordinal)
                && string.Equals(Provider, other.Provider, StringComparison.Ordinal)
                && string.Equals(Model, other.Model, StringComparison.Ordinal)
                && string.Equals(Status, other.Status, StringComparison.Ordinal)
                && string.Equals(Agent, other.Agent, StringComparison.Ordinal)
                && string.Equals(ToolPermissionMode, other.ToolPermissionMode, StringComparison.Ordinal)
                && ThinkingEnabled == other.ThinkingEnabled
                && Nullable.Equals(CreatedAt, other.CreatedAt)
                && Nullable.Equals(UpdatedAt, other.UpdatedAt)
                && TotalInputTokens == other.TotalInputTokens
                && TotalOutputTokens == other.TotalOutputTokens;
        }

        private static DateTime? ParseDate(string value)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : null;
        }

        public override string ToString() => string.IsNullOrEmpty(Title) ? $"Sessão #{Id}" : Title;
    }

    public sealed class CoderSessionSnapshot
    {
        public CoderSessionInfo Session;
        public readonly List<ChatMessage> Messages = new();
        public long LastMessageId;

        public static CoderSessionSnapshot FromJson(JObject root)
        {
            var snapshot = new CoderSessionSnapshot
            {
                Session = CoderSessionInfo.FromJson(root?["session"] ?? root),
            };
            var rows = root?["message_rows"] as JArray;
            if (rows != null && rows.Count > 0) ParseRows(rows, snapshot);
            else if (root?["messages"] is JArray messages) ParseMessages(messages, snapshot);
            return snapshot;
        }

        private static void ParseRows(JArray rows, CoderSessionSnapshot snapshot)
        {
            var toolCalls = new Dictionary<string, ChatToolCall>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var remoteId = (long?)row["id"] ?? 0;
                snapshot.LastMessageId = Math.Max(snapshot.LastMessageId, remoteId);
                var role = (string)row["role"];
                var content = ParseContent(row["content_json"]);
                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)
                    && ApplyToolResults(content, toolCalls)) continue;
                var message = ParseMessage(role, content, toolCalls);
                if (message == null) continue;
                message.RemoteId = remoteId;
                message.RemoteSessionId = (int?)row["session_id"] ?? snapshot.Session?.Id ?? 0;
                message.IsRemote = true;
                message.IsMeta = (int?)row["is_meta"] == 1;
                message.Timestamp = ParseDate((string)row["created_at"]) ?? DateTime.Now;
                snapshot.Messages.Add(message);
            }
        }

        private static void ParseMessages(JArray messages, CoderSessionSnapshot snapshot)
        {
            var toolCalls = new Dictionary<string, ChatToolCall>(StringComparer.Ordinal);
            long syntheticId = 1;
            foreach (var item in messages)
            {
                var content = item["content"];
                if (string.Equals((string)item["role"], "tool", StringComparison.OrdinalIgnoreCase)
                    && ApplyToolResults(content, toolCalls)) continue;
                var message = ParseMessage((string)item["role"], content, toolCalls);
                if (message == null) continue;
                message.RemoteId = syntheticId++;
                message.RemoteSessionId = snapshot.Session?.Id ?? 0;
                message.IsRemote = true;
                snapshot.Messages.Add(message);
            }
            snapshot.LastMessageId = syntheticId - 1;
        }

        private static ChatMessage ParseMessage(string role, JToken content, Dictionary<string, ChatToolCall> toolCalls)
        {
            var message = new ChatMessage { Role = MapRole(role) };
            if (content is JArray blocks)
            {
                foreach (var block in blocks)
                {
                    var type = (string)block["type"];
                    switch (type)
                    {
                        case "text": Append(ref message.Text, (string)block["text"]); break;
                        case "thinking": Append(ref message.ThinkingText, (string)block["text"]); break;
                        case "tool_use":
                        {
                            var call = new ChatToolCall
                            {
                                Id = (string)block["id"],
                                Name = (string)block["name"],
                                InputJson = block["input"]?.ToString(Formatting.None) ?? "",
                            };
                            message.ToolCalls.Add(call);
                            if (!string.IsNullOrEmpty(call.Id)) toolCalls[call.Id] = call;
                            break;
                        }
                        case "tool_result":
                            Append(ref message.Text, ToolResultText(block));
                            break;
                        default:
                            if (block.Type == JTokenType.String) Append(ref message.Text, (string)block);
                            else if (block["text"] != null) Append(ref message.Text, (string)block["text"]);
                            break;
                    }
                }
            }
            else if (content != null && content.Type != JTokenType.Null)
            {
                message.Text = content.Type == JTokenType.String ? (string)content : content.ToString(Formatting.None);
            }
            if (message.Role == ChatRole.User)
                message.Text = ChatReference.ParseMessageContent(message.Text, message.References);
            if (string.IsNullOrEmpty(message.Text)
                && string.IsNullOrEmpty(message.ThinkingText)
                && message.ToolCalls.Count == 0
                && message.References.Count == 0)
                return null;
            return message;
        }

        private static bool ApplyToolResults(JToken content, Dictionary<string, ChatToolCall> toolCalls)
        {
            var matched = false;
            if (!(content is JArray blocks)) return false;
            foreach (var block in blocks)
            {
                if ((string)block["type"] != "tool_result") continue;
                var id = (string)block["tool_use_id"];
                if (string.IsNullOrEmpty(id) || !toolCalls.TryGetValue(id, out var call)) continue;
                call.ResultText = ToolResultText(block);
                call.Completed = true;
                matched = true;
            }
            return matched;
        }

        private static string ToolResultText(JToken block)
        {
            var content = block?["content"];
            return content?.Type == JTokenType.String ? (string)content : content?.ToString(Formatting.None) ?? "";
        }

        private static JToken ParseContent(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) return token;
            var value = (string)token;
            if (string.IsNullOrEmpty(value)) return "";
            try { return JToken.Parse(value); }
            catch { return value; }
        }

        private static ChatRole MapRole(string role)
        {
            switch ((role ?? "").ToLowerInvariant())
            {
                case "user": return ChatRole.User;
                case "assistant": return ChatRole.Assistant;
                case "tool": return ChatRole.Tool;
                case "error": return ChatRole.Error;
                default: return ChatRole.System;
            }
        }

        private static void Append(ref string target, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            target = (target ?? "") + value;
        }

        private static DateTime? ParseDate(string value)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : null;
        }
    }
}
