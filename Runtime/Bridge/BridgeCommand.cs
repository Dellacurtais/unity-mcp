using System;
using System.Collections.Generic;
using System.Threading;

namespace Maicon.SceneAnalyzer.Bridge
{
    public sealed class BridgeStep
    {
        public string Name;
        public string PayloadJson;
        public int DelayFrames;
        public bool MarkLogAnchorBefore;
        public bool CaptureScreenshotAfter;
        public int TimeoutFrames;
        public float TimeoutSeconds;
    }

    public sealed class BridgeStepResult
    {
        public string Name;
        public bool Ok;
        public string Error;
        public float DurationMs;
        public int FrameAtStart;
        public int FrameAtEnd;
        public bool Skipped;
        public int LogAnchorSeq;
        public byte[] ScreenshotPng;
        public int ScreenshotWidth;
        public int ScreenshotHeight;
    }

    public enum BridgeScriptStatus
    {
        Queued = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4,
    }

    public sealed class BridgeScript
    {
        private int _status = (int)BridgeScriptStatus.Queued;
        private int _cancelRequested;
        private Action<BridgeScript> _completionCallback;

        public int Id;
        public List<BridgeStep> Steps = new List<BridgeStep>();
        public bool StopOnError;
        public bool ReturnPerStepResults;
        public List<BridgeStepResult> Results = new List<BridgeStepResult>();
        public string TerminalError;
        public long SubmittedUtcTicks;
        public long StartedUtcTicks;
        public long CompletedUtcTicks;

        public BridgeScriptStatus Status => (BridgeScriptStatus)Volatile.Read(ref _status);
        public bool IsComplete => IsTerminal(Status);
        public bool StartedExecution => Status != BridgeScriptStatus.Queued;
        public bool CancelRequested => Volatile.Read(ref _cancelRequested) != 0;

        public Action<BridgeScript> CompletionCallback
        {
            set { Interlocked.Exchange(ref _completionCallback, value); }
        }

        public void ClearCompletionCallback() => Interlocked.Exchange(ref _completionCallback, null);

        public bool TryStart()
        {
            if (Interlocked.CompareExchange(ref _status, (int)BridgeScriptStatus.Running,
                    (int)BridgeScriptStatus.Queued) != (int)BridgeScriptStatus.Queued) return false;
            StartedUtcTicks = DateTime.UtcNow.Ticks;
            return true;
        }

        public bool RequestCancel(string reason = null)
        {
            Interlocked.Exchange(ref _cancelRequested, 1);
            if (!string.IsNullOrEmpty(reason)) TerminalError = reason;
            return !IsComplete;
        }

        public bool TrySetTerminal(BridgeScriptStatus status, string error = null)
        {
            if (!IsTerminal(status)) throw new ArgumentOutOfRangeException(nameof(status));
            while (true)
            {
                var current = Status;
                if (IsTerminal(current)) return false;
                if (Interlocked.CompareExchange(ref _status, (int)status, (int)current) != (int)current) continue;
                TerminalError = error;
                CompletedUtcTicks = DateTime.UtcNow.Ticks;
                var callback = Interlocked.Exchange(ref _completionCallback, null);
                try { callback?.Invoke(this); } catch { }
                return true;
            }
        }

        public static bool IsTerminal(BridgeScriptStatus status)
        {
            return status == BridgeScriptStatus.Completed || status == BridgeScriptStatus.Failed
                || status == BridgeScriptStatus.Cancelled;
        }
    }
}
