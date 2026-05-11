using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Bridge
{
    /// One step inside a BridgeScript. Editor-side serializes into this shape;
    /// runtime side consumes it. JSON payload is kept as a string so the
    /// runtime doesn't depend on Newtonsoft (editor-only in many setups).
    public sealed class BridgeStep
    {
        /// Handler name registered via McpCommandBridge.Register(name, ...).
        public string Name;

        /// Free-form JSON string. Handler deserializes if it needs structured
        /// data. Null/empty for handlers that take no args.
        public string PayloadJson;

        /// Wait N frames after the previous step completes before invoking
        /// this one. 0 = run on the next Update tick.
        public int DelayFrames;

        /// If true, runtime snapshots ConsoleCapture seq before invoking and
        /// includes the post-step log delta in this step's result. Editor side
        /// reads ConsoleCapture — runtime just carries the flag through.
        public bool MarkLogAnchorBefore;

        /// If true, runtime captures the Game-view back buffer after the step
        /// (one WaitForEndOfFrame) and includes the PNG base64 in this step's
        /// result. Adds ~16ms latency.
        public bool CaptureScreenshotAfter;
    }

    /// Result of one BridgeStep. Filled by the runtime; passed back to the
    /// editor channel through the script's completion callback.
    public sealed class BridgeStepResult
    {
        public string Name;
        public bool Ok;
        public string Error;
        public float DurationMs;
        public int FrameAtStart;
        public int FrameAtEnd;
        public bool Skipped;
        public int LogAnchorSeq;       // seq captured before invoke if MarkLogAnchorBefore
        public byte[] ScreenshotPng;   // null unless CaptureScreenshotAfter
        public int ScreenshotWidth;
        public int ScreenshotHeight;
    }

    /// A submitted script — list of steps plus completion plumbing the
    /// runtime needs to signal each result back to the editor.
    public sealed class BridgeScript
    {
        public int Id;
        public List<BridgeStep> Steps;
        public bool StopOnError;
        public bool ReturnPerStepResults;

        /// Filled by the runtime as each step completes. Editor channel polls
        /// IsComplete to know when to wake the awaiting tool.
        public List<BridgeStepResult> Results = new();
        public volatile bool IsComplete;
        public volatile bool StartedExecution;
    }
}
