using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// Owns the registry of analyzers and produces an aggregated list of issues.
    /// Cached against TWO signals: an explicit `_cacheValid` bool cleared by
    /// `InvalidateCache` (used by ChangeWatcher and the post-write hook), AND
    /// `_cachedRevision` matched against `SceneSnapshot.Revision` (catches
    /// snapshot mutations that bypassed InvalidateCache). Either miss triggers
    /// a rebuild.
    public sealed class AnalysisRunner
    {
        private readonly List<IAnalyzer> _analyzers = new();
        private readonly SceneSnapshot _snapshot;
        private List<Issue> _cachedIssues;
        private bool _cacheValid;
        private long _cachedRevision = -1;

        public AnalysisRunner(SceneSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public void Register(IAnalyzer analyzer) => _analyzers.Add(analyzer);

        public IEnumerable<IAnalyzer> Analyzers => _analyzers;

        public void InvalidateCache() => _cacheValid = false;

        public IReadOnlyList<Issue> GetIssues(IEnumerable<string> categories = null)
        {
            _snapshot.RefreshIfNeeded();
            // Two layers of cache miss: explicit InvalidateCache (ChangeWatcher,
            // post-write hook) AND revision drift (someone mutated the snapshot
            // without invalidating us). Either triggers a rebuild.
            if (!_cacheValid || _cachedRevision != _snapshot.Revision)
            {
                var all = new List<Issue>();
                foreach (var a in _analyzers)
                {
                    IEnumerable<Issue> produced;
                    try { produced = a.Run(_snapshot); }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        all.Add(BuildAnalyzerErrorIssue(a, e, midEnumeration: false));
                        continue;
                    }

                    try
                    {
                        foreach (var i in produced)
                            if (i != null) all.Add(i);
                    }
                    catch (Exception e)
                    {
                        // Lazy iterators (yield return) can throw mid-enumeration —
                        // contain the same way so one bad analyzer can't kill the run.
                        Debug.LogException(e);
                        all.Add(BuildAnalyzerErrorIssue(a, e, midEnumeration: true));
                    }
                }
                _cachedIssues = all;
                _cacheValid = true;
                _cachedRevision = _snapshot.Revision;
            }
            if (categories == null) return _cachedIssues;
            var set = new HashSet<string>(categories);
            return _cachedIssues.Where(i => set.Contains(i.Category)).ToList();
        }

        private static Issue BuildAnalyzerErrorIssue(IAnalyzer a, Exception e, bool midEnumeration)
        {
            var when = midEnumeration ? "mid-enumeration" : "during Run";
            return new Issue(
                analyzer: a.Id,
                severity: IssueSeverity.Error,
                category: a.Category,
                message: $"Analyzer '{a.Id}' threw {e.GetType().Name} {when}: {e.Message}. " +
                         "Other analyzers continued; see Console for stack trace.",
                data: new Dictionary<string, object>
                {
                    ["exceptionType"] = e.GetType().FullName,
                    ["analyzerFault"] = true,
                });
        }
    }
}
