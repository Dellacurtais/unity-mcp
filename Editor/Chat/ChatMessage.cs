using System;
using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    public enum ChatRole
    {
        User,
        Assistant,
        System,
        Error,
        Tool,
    }

    public sealed class ChatToolCall
    {
        public string Id;
        public string Name;
        public string InputJson;
        public string ResultText;
        public bool Completed;
        public bool Failed;
        public double DurationMs;
        public bool Expanded;
    }

    public sealed class ChatMessage
    {
        public ChatRole Role;
        public string Text = "";
        public string ThinkingText = "";
        public DateTime Timestamp = DateTime.Now;
        public readonly List<ChatToolCall> ToolCalls = new();
        public readonly List<ChatReference> References = new();
        public bool Streaming;
        public string SystemNote;
        public string RemoteId;
        public string RemoteSessionId;
        public bool IsRemote;
        public bool IsMeta;
        public bool ThinkingExpanded;
        public int Revision;

        public void Touch() => Revision++;

        public bool CopyRemoteContentFrom(ChatMessage source)
        {
            if (source == null) return false;

            var toolsChanged = !ToolCallsEqual(ToolCalls, source.ToolCalls);
            var referencesChanged = !ReferencesEqual(References, source.References);
            var changed = Role != source.Role
                || !string.Equals(Text ?? "", source.Text ?? "", StringComparison.Ordinal)
                || !string.Equals(ThinkingText ?? "", source.ThinkingText ?? "", StringComparison.Ordinal)
                || Timestamp != source.Timestamp
                || !string.Equals(RemoteId, source.RemoteId, StringComparison.Ordinal)
                || !string.Equals(RemoteSessionId, source.RemoteSessionId, StringComparison.Ordinal)
                || !IsRemote
                || IsMeta != source.IsMeta
                || Streaming
                || !string.Equals(SystemNote, source.SystemNote, StringComparison.Ordinal)
                || toolsChanged
                || referencesChanged;
            if (!changed) return false;

            Role = source.Role;
            Text = source.Text ?? "";
            ThinkingText = source.ThinkingText ?? "";
            Timestamp = source.Timestamp;
            RemoteId = source.RemoteId;
            RemoteSessionId = source.RemoteSessionId;
            IsRemote = true;
            IsMeta = source.IsMeta;
            Streaming = false;
            SystemNote = source.SystemNote;
            if (toolsChanged) CopyToolCallsFrom(source.ToolCalls);
            if (referencesChanged)
            {
                References.Clear();
                References.AddRange(source.References);
            }
            Touch();
            return true;
        }

        private void CopyToolCallsFrom(List<ChatToolCall> source)
        {
            var previous = new List<ChatToolCall>(ToolCalls);
            ToolCalls.Clear();
            for (var index = 0; index < source.Count; index++)
            {
                var incoming = source[index];
                if (incoming == null) continue;

                ChatToolCall target = null;
                for (var previousIndex = 0; previousIndex < previous.Count; previousIndex++)
                {
                    var candidate = previous[previousIndex];
                    if (candidate == null) continue;
                    var sameId = !string.IsNullOrEmpty(incoming.Id)
                        && string.Equals(candidate.Id, incoming.Id, StringComparison.Ordinal);
                    var samePositionWithoutId = string.IsNullOrEmpty(incoming.Id)
                        && previousIndex == index
                        && string.IsNullOrEmpty(candidate.Id);
                    if (!sameId && !samePositionWithoutId) continue;
                    target = candidate;
                    previous[previousIndex] = null;
                    break;
                }

                if (target == null) target = new ChatToolCall();
                target.Id = incoming.Id;
                target.Name = incoming.Name;
                target.InputJson = incoming.InputJson;
                target.ResultText = incoming.ResultText;
                target.Completed = incoming.Completed;
                target.Failed = incoming.Failed;
                target.DurationMs = incoming.DurationMs;
                ToolCalls.Add(target);
            }
        }

        private static bool ToolCallsEqual(List<ChatToolCall> left, List<ChatToolCall> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                var a = left[index];
                var b = right[index];
                if (ReferenceEquals(a, b)) continue;
                if (a == null || b == null
                    || !string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                    || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                    || !string.Equals(a.InputJson, b.InputJson, StringComparison.Ordinal)
                    || !string.Equals(a.ResultText, b.ResultText, StringComparison.Ordinal)
                    || a.Completed != b.Completed
                    || a.Failed != b.Failed
                    || a.DurationMs != b.DurationMs)
                    return false;
            }
            return true;
        }

        private static bool ReferencesEqual(List<ChatReference> left, List<ChatReference> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                var a = left[index];
                var b = right[index];
                if (ReferenceEquals(a, b)) continue;
                if (a == null || b == null
                    || !string.Equals(a.CanonicalKey, b.CanonicalKey, StringComparison.Ordinal)
                    || !string.Equals(a.Label, b.Label, StringComparison.Ordinal)
                    || a.IsFolder != b.IsFolder
                    || a.IsAvailable != b.IsAvailable
                    || !string.Equals(a.ValidationError, b.ValidationError, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        public override string ToString() => $"[{Role}] {Text}";
    }
}
