using System;
using System.Collections.Generic;
using System.Reflection;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Analyzers
{
    /// Static-reflection heuristic for per-frame script cost. Counts active
    /// MonoBehaviours that declare Update / LateUpdate / FixedUpdate (walking
    /// the inheritance chain up to MonoBehaviour) and flags Types defining
    /// all three or scenes with too many Update callbacks. Not a profiler —
    /// can't measure actual cost; reports per-Type, never per-instance.
    public sealed class ScriptCostAnalyzer : IAnalyzer
    {
        public string Id => "script-cost";
        public string Category => "performance";

        private const int UpdateInstanceWarning = 300;

        [Flags]
        private enum UpdateMask
        {
            None = 0,
            Update = 1,
            Late = 2,
            Fixed = 4,
            All = Update | Late | Fixed,
        }

        // Domain-lifetime cache; do not clear. Type identity is stable until
        // domain reload, which re-allocates the dict.
        private static readonly Dictionary<Type, UpdateMask> _cache = new();

        private const BindingFlags MethodFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public IEnumerable<Issue> Run(SceneSnapshot snapshot)
        {
            var typeCount = new Dictionary<Type, int>();
            int updateInstances = 0;
            int activeBehaviours = 0;

            foreach (var info in snapshot.All())
            {
                if (!info.IsActive) continue;
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                foreach (var mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null || !mb.enabled) continue;
                    activeBehaviours++;

                    var type = mb.GetType();
                    typeCount[type] = typeCount.TryGetValue(type, out var c) ? c + 1 : 1;

                    if ((InspectType(type) & UpdateMask.Update) != 0)
                        updateInstances++;
                }
            }

            yield return new Issue(
                analyzer: Id, severity: IssueSeverity.Info, category: Category,
                message: $"Script cost: heuristic from static reflection, not profiler measurement. " +
                         $"{activeBehaviours} active MonoBehaviours; {updateInstances} bear Update_estimated.");

            foreach (var kv in typeCount)
            {
                if (InspectType(kv.Key) != UpdateMask.All) continue;
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                    message: $"Type '{kv.Key.Name}' defines Update + LateUpdate + FixedUpdate " +
                             $"({kv.Value} instances). Consolidate into a single tick or schedule via a manager.");
            }

            if (updateInstances > UpdateInstanceWarning)
            {
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                    message: $"{updateInstances} Update callbacks per frame_estimated " +
                             $"(> {UpdateInstanceWarning}). Consider pooling, manager-driven ticks, or coroutines.");
            }
        }

        private static UpdateMask InspectType(Type t)
        {
            if (_cache.TryGetValue(t, out var cached)) return cached;
            var mask = UpdateMask.None;
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
            {
                if (HasNoArg(cur, "Update")) mask |= UpdateMask.Update;
                if (HasNoArg(cur, "LateUpdate")) mask |= UpdateMask.Late;
                if (HasNoArg(cur, "FixedUpdate")) mask |= UpdateMask.Fixed;
            }
            _cache[t] = mask;
            return mask;
        }

        private static bool HasNoArg(Type t, string name)
        {
            // Match Unity's reflection lookup: parameterless overload only.
            // Specifying Type.EmptyTypes also avoids AmbiguousMatchException.
            return t.GetMethod(name, MethodFlags, binder: null, types: Type.EmptyTypes, modifiers: null) != null;
        }
    }
}
