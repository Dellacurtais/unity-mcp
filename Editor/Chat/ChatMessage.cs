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
        public long RemoteId;
        public int RemoteSessionId;
        public bool IsRemote;
        public bool IsMeta;
        public int Revision;

        public void Touch() => Revision++;

        public void CopyRemoteContentFrom(ChatMessage source)
        {
            if (source == null) return;
            Role = source.Role;
            Text = source.Text ?? "";
            ThinkingText = source.ThinkingText ?? "";
            Timestamp = source.Timestamp;
            RemoteId = source.RemoteId;
            RemoteSessionId = source.RemoteSessionId;
            IsRemote = true;
            IsMeta = source.IsMeta;
            Streaming = false;
            ToolCalls.Clear();
            ToolCalls.AddRange(source.ToolCalls);
            References.Clear();
            References.AddRange(source.References);
            Touch();
        }

        public override string ToString() => $"[{Role}] {Text}";
    }
}
