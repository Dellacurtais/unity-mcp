using System.Collections.Generic;
using System.Linq;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// Owns the registry of analyzers and produces an aggregated list of issues.
    /// Result is cached against the current snapshot revision to avoid repeated
    /// work when multiple tools query the same state.
    public sealed class AnalysisRunner
    {
        private readonly List<IAnalyzer> _analyzers = new();
        private readonly SceneSnapshot _snapshot;
        private List<Issue> _cachedIssues;
        private bool _cacheValid;

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
            if (!_cacheValid)
            {
                var all = new List<Issue>();
                foreach (var a in _analyzers)
                {
                    foreach (var i in a.Run(_snapshot))
                    {
                        if (i != null) all.Add(i);
                    }
                }
                _cachedIssues = all;
                _cacheValid = true;
            }
            if (categories == null) return _cachedIssues;
            var set = new HashSet<string>(categories);
            return _cachedIssues.Where(i => set.Contains(i.Category)).ToList();
        }
    }
}
