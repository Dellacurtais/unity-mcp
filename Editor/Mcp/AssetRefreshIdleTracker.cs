namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    /// <summary>
    /// Observes a quiet interval across Editor updates. This is a conservative
    /// settlement heuristic, not a promise that Unity cannot queue future work.
    /// </summary>
    internal sealed class AssetRefreshIdleTracker
    {
        internal const double QuietSeconds = 0.5d;
        private double _idleSince = -1d;
        private ulong? _artifactVersion;

        internal void Reset()
        {
            _idleSince = -1d;
            _artifactVersion = null;
        }

        internal bool Observe(double now, bool isCompiling, bool isUpdating, ulong artifactVersion)
        {
            var artifactsChanged = _artifactVersion != artifactVersion;
            _artifactVersion = artifactVersion;
            if (isCompiling || isUpdating)
            {
                _idleSince = -1d;
                return false;
            }
            // Import can begin and finish between two update callbacks. Its
            // dependency version still invalidates the previous idle sample.
            if (artifactsChanged || _idleSince < 0d || now < _idleSince)
            {
                _idleSince = now;
                return false;
            }
            return now - _idleSince >= QuietSeconds;
        }
    }
}
