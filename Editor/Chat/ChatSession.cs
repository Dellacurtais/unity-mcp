using System;
using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    public sealed class PendingQuestion
    {
        public string Id;
        public string Question;
        public List<string> Options = new();
        public bool AllowFreeform = true;
    }

    public sealed class PendingPlan
    {
        public string Id;
        public string Plan;
    }

    public sealed class PendingTasks
    {
        public string ToolUseId;
        public string Summary;
    }

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

        // Pending interactive prompts from the agent. Set by the SSE handler
        // when an `ask_user` / `submit_plan` / `submit_tasks` tool call arrives.
        // Cleared after the dev responds.
        public PendingQuestion CurrentQuestion;
        public PendingPlan CurrentPlan;
        public PendingTasks CurrentTasks;

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
            CurrentQuestion = null;
            CurrentPlan = null;
            CurrentTasks = null;
            Notify();
        }

        public void SetPendingQuestion(PendingQuestion q) { CurrentQuestion = q; Notify(); }
        public void ClearPendingQuestion() { CurrentQuestion = null; Notify(); }
        public void SetPendingPlan(PendingPlan p) { CurrentPlan = p; Notify(); }
        public void ClearPendingPlan() { CurrentPlan = null; Notify(); }
        public void SetPendingTasks(PendingTasks t) { CurrentTasks = t; Notify(); }
        public void ClearPendingTasks() { CurrentTasks = null; Notify(); }

        private void Notify()
        {
            try { OnChanged?.Invoke(); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }
}
