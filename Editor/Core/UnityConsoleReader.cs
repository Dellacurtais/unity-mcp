using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// Reads Unity's persistent Console buffer via reflection over the
    /// internal `UnityEditor.LogEntries` API. Unlike our own ConsoleCapture
    /// (which only sees messages emitted after [InitializeOnLoad] fires —
    /// thus misses pre-existing compile errors), this surfaces the full
    /// Console state visible to the user, including entries that survived
    /// previous domain reloads.
    public static class UnityConsoleReader
    {
        public sealed class Item
        {
            public int Row;
            public string Message;
            public string File;
            public int Line;
            public int Mode;          // raw Unity LogEntry mode bitfield
            public string Severity;   // "Error" / "Warning" / "Log" — derived
        }

        private static readonly object _lock = new();
        private static Type _logEntriesType;
        private static Type _logEntryType;
        private static MethodInfo _getCount;
        private static MethodInfo _startGetting;
        private static MethodInfo _endGetting;
        private static MethodInfo _getEntry;
        private static FieldInfo _msgField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;
        private static FieldInfo _modeField;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var asm = typeof(UnityEditor.Editor).Assembly;
                _logEntriesType = asm.GetType("UnityEditor.LogEntries");
                _logEntryType = asm.GetType("UnityEditor.LogEntry");
                if (_logEntriesType != null)
                {
                    _getCount = _logEntriesType.GetMethod("GetCount",
                        BindingFlags.Public | BindingFlags.Static);
                    _startGetting = _logEntriesType.GetMethod("StartGettingEntries",
                        BindingFlags.Public | BindingFlags.Static);
                    _endGetting = _logEntriesType.GetMethod("EndGettingEntries",
                        BindingFlags.Public | BindingFlags.Static);
                    _getEntry = _logEntriesType.GetMethod("GetEntryInternal",
                        BindingFlags.Public | BindingFlags.Static);
                }
                if (_logEntryType != null)
                {
                    _msgField = _logEntryType.GetField("message");
                    _fileField = _logEntryType.GetField("file");
                    _lineField = _logEntryType.GetField("line");
                    _modeField = _logEntryType.GetField("mode");
                }
            }
            catch { /* internal API drift — tolerate */ }
        }

        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return _logEntriesType != null && _logEntryType != null
                    && _getCount != null && _getEntry != null
                    && _msgField != null && _modeField != null;
            }
        }

        /// Read up to `maxItems` most-recent entries from Unity's Console.
        /// Filters: severity ("Error"/"Warning"/"Log") and substring match.
        /// Synchronous — must be called on the editor main thread.
        public static List<Item> Read(string severityFilter, string contains, int maxItems)
        {
            var list = new List<Item>();
            Resolve();
            if (!IsAvailable) return list;

            // LogEntries needs a paired Start/End to lock the buffer for read.
            // The API is internal/static; calling pattern is: GetCount →
            // StartGettingEntries → loop GetEntryInternal(i, entry) → EndGettingEntries.
            object entryInstance;
            try { entryInstance = Activator.CreateInstance(_logEntryType); }
            catch { return list; }

            lock (_lock)
            {
                try
                {
                    int count = (int)_getCount.Invoke(null, null);
                    _startGetting?.Invoke(null, null);

                    int from = Math.Max(0, count - Math.Max(maxItems * 2, maxItems));
                    for (int i = from; i < count; i++)
                    {
                        try { _getEntry.Invoke(null, new object[] { i, entryInstance }); }
                        catch { continue; }

                        var msg = _msgField?.GetValue(entryInstance) as string ?? "";
                        var file = _fileField?.GetValue(entryInstance) as string ?? "";
                        int line = 0; int mode = 0;
                        try { line = (int)(_lineField?.GetValue(entryInstance) ?? 0); } catch { }
                        try { mode = (int)(_modeField?.GetValue(entryInstance) ?? 0); } catch { }

                        var sev = SeverityFromMode(mode, msg);

                        if (!string.IsNullOrEmpty(severityFilter)
                            && !MatchesSeverity(sev, severityFilter)) continue;
                        if (!string.IsNullOrEmpty(contains)
                            && msg.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        list.Add(new Item
                        {
                            Row = i, Message = msg, File = file, Line = line,
                            Mode = mode, Severity = sev,
                        });
                        if (list.Count >= maxItems) break;
                    }
                }
                catch { /* swallow — internal API */ }
                finally
                {
                    try { _endGetting?.Invoke(null, null); } catch { }
                }
            }
            return list;
        }

        /// Quick bucketed counts of the entire Console — handy for
        /// "how many errors are pending right now" without dumping bodies.
        public static (int errors, int warnings, int logs) Counts()
        {
            int e = 0, w = 0, l = 0;
            foreach (var it in Read(severityFilter: null, contains: null, maxItems: 1000))
            {
                if (it.Severity == "Error") e++;
                else if (it.Severity == "Warning") w++;
                else l++;
            }
            return (e, w, l);
        }

        private static bool MatchesSeverity(string sev, string filter)
        {
            switch (filter.ToLowerInvariant())
            {
                case "errors_only": return sev == "Error";
                case "warnings_and_errors": return sev == "Error" || sev == "Warning";
                default: return true;
            }
        }

        // The Unity LogEntry.mode bitfield isn't part of any public surface;
        // the bits drift slightly between Unity versions. We use a liberal
        // bitmask covering the common error/warning flags AND a textual
        // fallback ("error CS…", "warning CS…", "Exception:…") so the
        // labelling stays correct across Unity revisions.
        private const int ErrorBitMask =
            (1 << 0)  | // StickyError
            (1 << 1)  | // ScriptingError
            (1 << 4)  | // ScriptCompileError
            (1 << 7)  | // Fatal
            (1 << 8)  | // AssetImportError
            (1 << 9)  | // Error
            (1 << 18) | // ScriptingException
            (1 << 21);  // ScriptingAssertion

        private const int WarningBitMask =
            (1 << 2)  | // ScriptingWarning
            (1 << 5)  | // ScriptCompileWarning
            (1 << 13);  // AssetImportWarning (modern bit position)

        private static string SeverityFromMode(int mode, string message)
        {
            if ((mode & ErrorBitMask) != 0) return "Error";
            if ((mode & WarningBitMask) != 0) return "Warning";
            // Textual fallback — handles cases where the bit layout shifted
            // in a Unity version we haven't accounted for.
            if (!string.IsNullOrEmpty(message))
            {
                if (message.IndexOf("error CS", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("Exception:", StringComparison.Ordinal) >= 0
                    || message.IndexOf("UnityException", StringComparison.Ordinal) >= 0)
                    return "Error";
                if (message.IndexOf("warning CS", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Warning";
            }
            return "Log";
        }
    }
}
