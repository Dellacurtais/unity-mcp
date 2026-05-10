using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Analyzers
{
    /// Counts triangles across MeshRenderer + SkinnedMeshRenderer and detects
    /// mesh reuse. Flags scenes whose total triangle budget crosses a soft
    /// warning threshold or a hard ceiling, and meshes shared by enough
    /// renderers to be worth GPU-instancing.
    public sealed class GeometryAnalyzer : IAnalyzer
    {
        public string Id => "geometry";
        public string Category => "performance";

        // Heuristic thresholds — tune later or expose via settings.
        private const long HighTriangleBudget = 500_000;
        private const long TriangleCeiling = 2_000_000;
        private const int DuplicateMeshThreshold = 10;

        public IEnumerable<Issue> Run(SceneSnapshot snapshot)
        {
            var perMesh = new Dictionary<Mesh, (int instances, long tris)>();
            long totalTris = 0;
            int rendererCount = 0;

            foreach (var info in snapshot.All())
            {
                if (!info.IsActive) continue;
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                foreach (var r in go.GetComponents<Renderer>())
                {
                    var mesh = ResolveMesh(r);
                    if (mesh == null) continue;

                    rendererCount++;
                    var tris = TriangleCount(mesh);
                    totalTris += tris;

                    if (perMesh.TryGetValue(mesh, out var v))
                        perMesh[mesh] = (v.instances + 1, v.tris);
                    else
                        perMesh[mesh] = (1, tris);
                }
            }

            yield return new Issue(
                analyzer: Id, severity: IssueSeverity.Info, category: Category,
                message: $"{totalTris:N0} triangles across {rendererCount} renderers (mesh + skinned)");

            if (totalTris > TriangleCeiling)
            {
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Error, category: Category,
                    message: $"Scene has {totalTris:N0} triangles (> {TriangleCeiling:N0} ceiling). " +
                             "Aggressive culling / streaming required.");
            }
            else if (totalTris > HighTriangleBudget)
            {
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                    message: $"Scene has {totalTris:N0} triangles (> {HighTriangleBudget:N0} budget). " +
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

        private static Mesh ResolveMesh(Renderer r)
        {
            switch (r)
            {
                case SkinnedMeshRenderer smr:
                    return smr.sharedMesh;
                case MeshRenderer _:
                    var mf = r.GetComponent<MeshFilter>();
                    return mf != null ? mf.sharedMesh : null;
                default:
                    return null;
            }
        }

        private static long TriangleCount(Mesh mesh)
        {
            long tris = 0;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                tris += mesh.GetIndexCount(sm) / 3;
            }
            return tris;
        }
    }
}
