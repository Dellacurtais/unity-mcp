using System;
using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    /// In-memory message history for the embedded chat panel. Mirrors the
    /// transcript the dashboard would show, but lives in the editor so the
    /// dev never leaves Unity. Persistence across editor restarts is left to
    /// the server-side session (sessionId is stored in EditorPrefs).
    public sealed class ChatSession
    {
        public readonly List<ChatMessage> Messages = new();
        public ChatMessage StreamingAssistant;
        public string LastEventId;
        public bool IsRunning;
        public string LastError;

        public event Action OnChanged;

        public void AddUser(string text)
        {
            Messages.Add(new ChatMessage { Role = ChatRole.User, Text = text });
            Notify();
        }

        public void StartAssistant()
        {
            StreamingAssistant = new ChatMessage { Role = ChatRole.Assistant, Streaming = true };
            Messages.Add(StreamingAssistant);
            IsRunning = true;
            Notify();
        }

        public void AppendAssistantText(string delta)
        {
            if (StreamingAssistant == null) StartAssistant();
            StreamingAssistant.Text += delta;
            Notify();
        }

        public void AddToolCall(string id, string name, string inputJson)
        {
            if (StreamingAssistant == null) StartAssistant();
            StreamingAssistant.ToolCalls.Add(new ChatToolCall
            {
                Id = id,
                Name = name,
                InputJson = inputJson,
            });
            Notify();
        }

        public void CompleteToolCall(string id, string resultText, double durationMs)
        {
            if (StreamingAssistant == null) return;
            var call = StreamingAssistant.ToolCalls.Find(c => c.Id == id);
            if (call != null)
            {
                call.ResultText = resultText;
                call.DurationMs = durationMs;
                call.Completed = true;
            }
            Notify();
        }

        public void AddSystemNote(string text)
        {
            Messages.Add(new ChatMessage { Role = ChatRole.System, Text = text });
            Notify();
        }

        public void AddError(string text)
        {
            LastError = text;
            Messages.Add(new ChatMessage { Role = ChatRole.Error, Text = text });
            IsRunning = false;
            Notify();
        }

        public void FinishAssistant()
        {
            if (StreamingAssistant != null) StreamingAssistant.Streaming = false;
            StreamingAssistant = null;
            IsRunning = false;
            Notify();
        }

        public void Reset()
        {
            Messages.Clear();
            StreamingAssistant = null;
            LastEventId = null;
            IsRunning = false;
            LastError = null;
            Notify();
        }

        private void Notify()
        {
            try { OnChanged?.Invoke(); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }
}
