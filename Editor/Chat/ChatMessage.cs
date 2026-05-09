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
    }

    public sealed class ChatToolCall
    {
        public string Id;
        public string Name;
        public string InputJson;
        public string ResultText;
        public bool Completed;
        public double DurationMs;
    }

    public sealed class ChatMessage
    {
        public ChatRole Role;
        public string Text = "";
        public DateTime Timestamp = DateTime.Now;
        public List<ChatToolCall> ToolCalls = new();
        public bool Streaming;       // assistant message being built incrementally
        public string SystemNote;    // info bubble (e.g. plan submission, ask_user prompt)

        public override string ToString()
        {
            return $"[{Role}] {Text}";
        }
    }
}
