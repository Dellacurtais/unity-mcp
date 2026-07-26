using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// Captures every Unity Console message (Debug.Log*, uncaught exceptions,
    /// compile errors/warnings) into an in-memory ring buffer so the MCP agent
    /// can query "did anything go wrong since I made my changes". Subscribes
    /// at editor load via [InitializeOnLoad]. State is per-AppDomain — an
    /// assembly reload resets the buffer (which is fine; reloads only happen
    /// on successful compile, so there are no errors to report).
    [InitializeOnLoad]
    public static class ConsoleCapture
    {
        public sealed class Entry
        {
            public int Seq;
            public DateTime Time;
            public LogType Severity;
            public string Message;
            public string Stack;
            public bool IsCompile;       // came from CompilationPipeline event
            public string SourceFile;    // for compile messages only
            public int SourceLine;
            public int SourceColumn;
        }

        private const int Capacity = 1000;
        private static readonly LinkedList<Entry> _entries = new();
        private static readonly object _lock = new();
        private static readonly string _domainId = Guid.NewGuid().ToString("N");
        private static int _nextSeq = 1;

        /// Current sequence number. Use this as a "since" anchor before
        /// running an operation, then pass it to Snapshot() to filter for
        /// entries that arrived during the op.
        public static int CurrentSeq { get { lock (_lock) return _nextSeq; } }

        public static int BufferSize { get { lock (_lock) return _entries.Count; } }

        public static string DomainId => _domainId;

        public static bool IsCurrentDomain(string domainId) =>
            string.IsNullOrEmpty(domainId) || string.Equals(domainId, _domainId, StringComparison.Ordinal);

        static ConsoleCapture()
        {
            // logMessageReceivedThreaded fires from any thread (including the
            // HTTP listener and async tools). Using the threaded variant
            // ensures we never miss messages from background work.
            Application.logMessageReceivedThreaded += OnLog;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            // Never propagate from a log handler — would cause an infinite loop
            // ("exception while logging exception").
            try
            {
                Push(new Entry
                {
                    Time = DateTime.Now,
                    Severity = type,
                    Message = message,
                    Stack = stack ?? "",
                });
            }
            catch { /* swallow */ }
        }

        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            try
            {
                if (messages == null) return;
                foreach (var m in messages)
                {
                    Push(new Entry
                    {
                        Time = DateTime.Now,
                        Severity = m.type == CompilerMessageType.Error ? LogType.Error : LogType.Warning,
                        Message = m.message,
                        Stack = "",
                        IsCompile = true,
                        SourceFile = m.file,
                        SourceLine = m.line,
                        SourceColumn = m.column,
                    });
                }
            }
            catch { /* swallow */ }
        }

        private static void Push(Entry e)
        {
            lock (_lock)
            {
                e.Seq = _nextSeq++;
                _entries.AddLast(e);
                while (_entries.Count > Capacity) _entries.RemoveFirst();
            }
        }

        public static List<Entry> Snapshot(int? sinceSeq, LogType? minSeverity, string contains, int limit) =>
            SnapshotCore(sinceSeq, minSeverity, contains, limit, compileOnly: false);

        public static List<Entry> SnapshotCompilerDiagnostics(int? sinceSeq, LogType? minSeverity, int limit) =>
            SnapshotCore(sinceSeq, minSeverity, contains: null, limit, compileOnly: true);

        private static List<Entry> SnapshotCore(
            int? sinceSeq,
            LogType? minSeverity,
            string contains,
            int limit,
            bool compileOnly)
        {
            lock (_lock)
            {
                IEnumerable<Entry> q = _entries;
                if (compileOnly) q = q.Where(e => e.IsCompile);
                if (sinceSeq.HasValue) q = q.Where(e => e.Seq >= sinceSeq.Value);
                if (minSeverity.HasValue) q = q.Where(e => Importance(e.Severity) >= Importance(minSeverity.Value));
                if (!string.IsNullOrEmpty(contains))
                    q = q.Where(e => e.Message != null
                        && e.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
                var list = q.ToList();
                if (list.Count > limit) list = list.GetRange(list.Count - limit, limit);
                return list;
            }
        }

        public static int Clear(bool alsoUnityConsole, out bool unityConsoleCleared)
        {
            int removed;
            lock (_lock) { removed = _entries.Count; _entries.Clear(); }
            unityConsoleCleared = false;
            if (!alsoUnityConsole) return removed;
            try
            {
                var asm = typeof(UnityEditor.Editor).Assembly;
                var type = asm.GetType("UnityEditor.LogEntries");
                var clear = type?.GetMethod("Clear",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (clear == null) return removed;
                clear.Invoke(null, null);
                unityConsoleCleared = true;
            }
            catch { }
            return removed;
        }

        private static int Importance(LogType t)
        {
            switch (t)
            {
                case LogType.Exception: return 4;
                case LogType.Error:     return 3;
                case LogType.Assert:    return 3;
                case LogType.Warning:   return 1;
                default:                return 0; // Log
            }
        }
    }
}
