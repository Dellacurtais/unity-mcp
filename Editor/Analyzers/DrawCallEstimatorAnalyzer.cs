using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Analyzers
{
    /// Heuristic draw-call estimator. Groups Renderer × material slot by a
    /// composite BatchKey (material, lightmap, shadow casting, layer, static
    /// batching flag) and emits one draw call per group, except groups whose
    /// members are all marked BatchingStatic (treated as merged).
    ///
    /// Pipeline-agnostic: SRP batcher compatibility, GPU instancing flags,
    /// motion vectors, and dynamic batching are not read. Numeric outputs are
    /// suffixed with "_estimated" so consumers don't treat them as ground truth.
    public sealed class DrawCallEstimatorAnalyzer : IAnalyzer
    {
        public string Id => "draw-call-estimator";
        public string Category => "performance";

        private const int NonStaticDrawCallWarning = 1500;
        private const int TopOffendersCap = 20;
        private const int MaterialReuseWarning = 8;

        private readonly struct BatchKey
        {
            public readonly Material Material;
            public readonly int LightmapIndex;
            public readonly ShadowCastingMode Shadows;
            public readonly int Layer;
            public readonly bool BatchableStatic;

            public BatchKey(Material m, int lm, ShadowCastingMode s, int layer, bool batchable)
            {
                Material = m; LightmapIndex = lm; Shadows = s; Layer = layer; BatchableStatic = batchable;
            }
        }

        public IEnumerable<Issue> Run(SceneSnapshot snapshot)
        {
            var groups = new Dictionary<BatchKey, int>();
            var materialNonStaticCount = new Dictionary<Material, int>();
            var nullMaterialIssues = new List<Issue>();
            int rendererSlots = 0;

            foreach (var info in snapshot.All())
            {
                if (!info.IsActive) continue;
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                var staticFlags = GameObjectUtility.GetStaticEditorFlags(go);
                var batchable = (staticFlags & StaticEditorFlags.BatchingStatic) != 0;

                foreach (var r in go.GetComponents<Renderer>())
                {
                    if (!r.enabled) continue;
                    var mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0)
                    {
                        nullMaterialIssues.Add(new Issue(
                            analyzer: Id, severity: IssueSeverity.Error, category: Category,
                            message: $"Renderer on '{info.Name}' has no materials assigned " +
                                     "(forces magenta/error pass).",
                            instanceId: info.InstanceID,
                            gameObjectPath: snapshot.GetPath(info.InstanceID)));
                        continue;
                    }

                    for (int i = 0; i < mats.Length; i++)
                    {
                        rendererSlots++;
                        var mat = mats[i];
                        if (mat == null)
                        {
                            nullMaterialIssues.Add(new Issue(
                                analyzer: Id, severity: IssueSeverity.Error, category: Category,
                                message: $"Renderer on '{info.Name}' has null material at slot {i} " +
                                         "(forces magenta/error pass).",
                                instanceId: info.InstanceID,
                                gameObjectPath: snapshot.GetPath(info.InstanceID)));
                            continue;
                        }

                        var key = new BatchKey(mat, r.lightmapIndex, r.shadowCastingMode, go.layer, batchable);
                        groups[key] = groups.TryGetValue(key, out var c) ? c + 1 : 1;

                        if (!batchable)
                            materialNonStaticCount[mat] = materialNonStaticCount.TryGetValue(mat, out var mc) ? mc + 1 : 1;
                    }
                }
            }

            int estimatedDrawCalls = 0;
            int nonStaticDrawCalls = 0;
            foreach (var kv in groups)
            {
                int dc = kv.Key.BatchableStatic ? 1 : kv.Value;
                estimatedDrawCalls += dc;
                if (!kv.Key.BatchableStatic) nonStaticDrawCalls += dc;
            }

            yield return new Issue(
                analyzer: Id, severity: IssueSeverity.Info, category: Category,
                message: $"~{estimatedDrawCalls} draw calls_estimated across {rendererSlots} " +
                         $"renderer slots ({nonStaticDrawCalls} non-static_estimated).");

            if (nonStaticDrawCalls > NonStaticDrawCallWarning)
            {
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                    message: $"~{nonStaticDrawCalls} non-static draw calls_estimated " +
                             $"(> {NonStaticDrawCallWarning}). Mark static where possible, " +
                             "enable GPU Instancing on shared materials, or merge meshes.");
            }

            foreach (var ni in nullMaterialIssues) yield return ni;

            var topOffenders = materialNonStaticCount
                .Where(kv => kv.Value >= MaterialReuseWarning)
                .OrderByDescending(kv => kv.Value)
                .Take(TopOffendersCap);

            foreach (var kv in topOffenders)
            {
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                    message: $"Material '{kv.Key.name}' used by {kv.Value} non-static renderer slots — " +
                             "mark roots BatchingStatic or enable GPU Instancing on the material.");
            }
        }
    }
}
