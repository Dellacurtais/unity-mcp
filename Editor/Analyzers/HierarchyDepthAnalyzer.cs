using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;

namespace Maicon.SceneAnalyzer.Editor.Analyzers
{
    /// Reports overly-deep hierarchies and roots with too many direct
    /// children. Walks the snapshot only — no Unity API calls — so it's
    /// the cheapest analyzer in the registry.
    public sealed class HierarchyDepthAnalyzer : IAnalyzer
    {
        public string Id => "hierarchy-depth";
        public string Category => "performance";

        private const int DepthWarning = 12;
        private const int RootChildWarning = 200;

        public IEnumerable<Issue> Run(SceneSnapshot snapshot)
        {
            var depthCache = new Dictionary<int, int>();
            int maxDepth = 0;
            int rootCount = 0;

            foreach (var info in snapshot.All())
            {
                var d = Depth(info, snapshot, depthCache);
                if (d > maxDepth) maxDepth = d;

                if (info.ParentId == 0)
                {
                    rootCount++;
                    var children = info.ChildIds?.Count ?? 0;
                    if (children > RootChildWarning)
                    {
                        yield return new Issue(
                            analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                            message: $"Root '{info.Name}' has {children} direct children " +
                                     $"(> {RootChildWarning}). Group under sub-roots to keep " +
                                     "Transform sync and editor refresh fast.",
                            instanceId: info.InstanceID,
                            gameObjectPath: snapshot.GetPath(info.InstanceID));
                    }
                }

                if (d > DepthWarning)
                {
                    yield return new Issue(
                        analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                        message: $"GameObject at depth {d} (> {DepthWarning}). Deep nesting " +
                                 "compounds Transform.SetParent + GetComponent traversal cost.",
                        instanceId: info.InstanceID,
                        gameObjectPath: snapshot.GetPath(info.InstanceID));
                }
            }

            yield return new Issue(
                analyzer: Id, severity: IssueSeverity.Info, category: Category,
                message: $"Hierarchy: {rootCount} roots, max depth {maxDepth}.");
        }

        private static int Depth(GameObjectInfo info, SceneSnapshot snap, Dictionary<int, int> cache)
        {
            if (cache.TryGetValue(info.InstanceID, out var d)) return d;
            if (info.ParentId == 0) { cache[info.InstanceID] = 0; return 0; }
            var parent = snap.Get(info.ParentId);
            if (parent == null) { cache[info.InstanceID] = 0; return 0; }
            var pd = Depth(parent, snap, cache) + 1;
            cache[info.InstanceID] = pd;
            return pd;
        }
    }
}
