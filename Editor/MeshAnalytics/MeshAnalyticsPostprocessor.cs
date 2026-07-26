using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics
{
    /// Invalidates persisted mesh-analysis results whenever imported model
    /// geometry changes. Analyses are owner- and transform-scoped, so they
    /// cannot be safely precomputed from the asset import hierarchy alone.
    public sealed class MeshAnalyticsPostprocessor : AssetPostprocessor
    {
        public void OnPostprocessModel(GameObject root)
        {
            if (root == null) return;

            var meshes = new HashSet<Mesh>();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) meshes.Add(filter.sharedMesh);
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer.sharedMesh != null) meshes.Add(renderer.sharedMesh);

            foreach (var mesh in meshes)
                MeshAnalysisDiskCache.InvalidateMesh(MeshResolver.MakeMeshKey(mesh));
            MeshAnalysisCache.Clear();
        }
    }
}
