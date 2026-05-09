using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Analyzers
{
    /// Counts triangles and detects mesh reuse across renderers. Flags scenes
    /// with very high triangle counts and duplicated meshes that could benefit
    /// from GPU instancing.
    public sealed class GeometryAnalyzer : IAnalyzer
    {
        public string Id => "geometry";
        public string Category => "performance";

        // Heuristic thresholds — tune later or expose via settings.
        private const int HighTriangleBudget = 500_000;
        private const int DuplicateMeshThreshold = 10;

        public IEnumerable<Issue> Run(SceneSnapshot snapshot)
        {
            var perMesh = new Dictionary<Mesh, (int instances, long tris)>();
            long totalTris = 0;
            int rendererCount = 0;

            foreach (var info in snapshot.All())
            {
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                foreach (var mf in go.GetComponents<MeshFilter>())
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null) continue;
                    rendererCount++;
                    long tris = 0;
                    try { tris = mesh.triangles.LongLength / 3; }
                    catch { /* unreadable mesh */ continue; }
                    totalTris += tris;
                    if (perMesh.TryGetValue(mesh, out var v))
                        perMesh[mesh] = (v.instances + 1, v.tris);
                    else
                        perMesh[mesh] = (1, tris);
                }
            }

            yield return new Issue(
                analyzer: Id, severity: IssueSeverity.Info, category: Category,
                message: $"{totalTris:N0} triangles across {rendererCount} mesh renderers");

            if (totalTris > HighTriangleBudget)
            {
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                    message: $"Scene has {totalTris:N0} triangles (> {HighTriangleBudget:N0}). " +
                             "Consider LOD groups, instancing, or culling.");
            }

            foreach (var kv in perMesh)
            {
                if (kv.Value.instances < DuplicateMeshThreshold) continue;
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Info, category: Category,
                    message: $"Mesh '{kv.Key.name}' used by {kv.Value.instances} renderers " +
                             $"({kv.Value.tris:N0} tris each) — candidate for GPU instancing.");
            }
        }
    }
}
