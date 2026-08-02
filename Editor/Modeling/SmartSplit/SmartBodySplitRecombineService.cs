using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Validation;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    public sealed class SmartBodySplitRecombineResult
    {
        public Mesh Mesh { get; internal set; }
        public string AssetPath { get; internal set; }
        public MeshValidationReport Validation { get; internal set; }
        public int UpdatedVertexCount { get; internal set; }
    }

    public static class SmartBodySplitRecombineService
    {
        public static SmartBodySplitRecombineResult Recombine(
            SmartBodySplitManifest manifest,
            string outputFolder,
            float seamTolerance = 1e-5f)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (!manifest.IsValid) throw new InvalidOperationException("SPLIT_MANIFEST_INVALID: only validated splits can be recombined.");
            if (manifest.SourceMesh == null) throw new InvalidOperationException("SOURCE_MISSING: split manifest has no source mesh reference.");
            if (!string.Equals(MeshFingerprint.Compute(manifest.SourceMesh), manifest.SourceHash, StringComparison.Ordinal))
                throw new InvalidOperationException("SOURCE_DRIFTED: source mesh changed after the split was created.");

            var mesh = UnityEngine.Object.Instantiate(manifest.SourceMesh);
            mesh.name = manifest.SourceMesh.name + "_Recombined";
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var assigned = new bool[positions.Length];
            int updated = 0;
            try
            {
                for (int partIndex = 0; partIndex < manifest.Parts.Count; partIndex++)
                {
                    var part = manifest.Parts[partIndex];
                    if (part.Mesh == null) throw new InvalidOperationException($"PART_MESH_MISSING: '{part.SlotId}'.");
                    if (part.SourceVertexIndices.Count != part.Mesh.vertexCount)
                        throw new InvalidOperationException($"PART_VERTEX_MAP_INVALID: '{part.SlotId}' maps {part.SourceVertexIndices.Count} vertices for mesh count {part.Mesh.vertexCount}.");
                    var partPositions = part.Mesh.vertices;
                    var partNormals = part.Mesh.normals;
                    var partTangents = part.Mesh.tangents;
                    for (int local = 0; local < part.SourceVertexIndices.Count; local++)
                    {
                        int source = part.SourceVertexIndices[local];
                        if (source < 0 || source >= positions.Length)
                            throw new InvalidOperationException($"PART_VERTEX_MAP_INVALID: '{part.SlotId}' references source vertex {source}.");
                        if (assigned[source] && (positions[source] - partPositions[local]).sqrMagnitude > seamTolerance * seamTolerance)
                            throw new InvalidOperationException(
                                $"SEAM_CONFLICT: source vertex {source} has divergent positions across parts; weld/align the seam before recombining.");
                        if (!assigned[source]) updated++;
                        assigned[source] = true;
                        positions[source] = partPositions[local];
                        if (normals.Length == positions.Length && partNormals.Length == partPositions.Length)
                            normals[source] = partNormals[local];
                        if (tangents.Length == positions.Length && partTangents.Length == partPositions.Length)
                            tangents[source] = partTangents[local];
                    }
                }
                mesh.vertices = positions;
                if (normals.Length == positions.Length) mesh.normals = normals;
                if (tangents.Length == positions.Length) mesh.tangents = tangents;
                mesh.RecalculateBounds();
                var validation = MeshValidator.Validate(mesh);
                if (!validation.IsValid)
                    throw new InvalidOperationException("RECOMBINE_VALIDATION_FAILED: recombined mesh did not pass channel/index validation.");
                var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(
                    string.IsNullOrWhiteSpace(outputFolder)
                        ? $"{AssetModelerAssetPaths.DefaultRoot}/Recombined"
                        : outputFolder);
                var path = AssetModelerAssetPaths.UniqueAssetPath(folder, mesh.name, ".asset");
                AssetDatabase.CreateAsset(mesh, path);
                AssetDatabase.SaveAssets();
                return new SmartBodySplitRecombineResult
                {
                    Mesh = mesh,
                    AssetPath = path,
                    Validation = validation,
                    UpdatedVertexCount = updated,
                };
            }
            catch
            {
                if (mesh != null && !AssetDatabase.Contains(mesh)) UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
        }
    }
}
