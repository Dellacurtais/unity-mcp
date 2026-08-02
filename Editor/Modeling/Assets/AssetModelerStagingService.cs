using System;
using System.Collections.Generic;
using System.IO;
using Maicon.SceneAnalyzer.Editor.Modeling.Validation;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Assets
{
    public sealed class AssetModelerStagingResult
    {
        public string AssetPath { get; internal set; }
        public long ByteCount { get; internal set; }
        public int MeshCount { get; internal set; }
        public int VertexCount { get; internal set; }
        public int TriangleCount { get; internal set; }
        public IReadOnlyList<string> Issues { get; internal set; }
        public bool IsValid { get; internal set; }
    }

    public static class AssetModelerStagingService
    {
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".obj", ".dae", ".gltf", ".glb", ".asset", ".prefab",
        };

        public static AssetModelerStagingResult Stage(
            string sourcePath,
            string outputFolder = null,
            long maximumBytes = 1024L * 1024L * 1024L)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("STAGING_SOURCE_REQUIRED: source path is required.", nameof(sourcePath));
            string extension = Path.GetExtension(sourcePath);
            if (!AllowedExtensions.Contains(extension))
                throw new InvalidOperationException($"STAGING_EXTENSION_REJECTED: '{extension}' is not an approved 3D asset type.");
            string folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(
                string.IsNullOrWhiteSpace(outputFolder) ? AssetModelerAssetPaths.DefaultRoot + "/Staging" : outputFolder);
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("UNITY_PROJECT_PATH_MISSING: cannot resolve project root.");
            string normalized = sourcePath.Replace('\\', '/');
            bool projectAsset = normalized.Equals("Assets", StringComparison.Ordinal)
                || normalized.StartsWith("Assets/", StringComparison.Ordinal);
            string absoluteSource = projectAsset
                ? Path.GetFullPath(Path.Combine(projectRoot, normalized))
                : Path.GetFullPath(sourcePath);
            if (!File.Exists(absoluteSource)) throw new FileNotFoundException("STAGING_SOURCE_NOT_FOUND: source asset does not exist.", absoluteSource);
            long byteCount = new FileInfo(absoluteSource).Length;
            if (byteCount <= 0 || byteCount > maximumBytes)
                throw new InvalidOperationException($"STAGING_SIZE_REJECTED: asset has {byteCount} bytes; maximum is {maximumBytes}.");

            string targetPath = AssetModelerAssetPaths.UniqueAssetPath(folder, Path.GetFileNameWithoutExtension(sourcePath), extension);
            string absoluteTarget = Path.GetFullPath(Path.Combine(projectRoot, targetPath));
            bool copied = false;
            try
            {
                if (projectAsset)
                {
                    if (!AssetDatabase.CopyAsset(normalized, targetPath))
                        throw new InvalidOperationException("STAGING_COPY_FAILED: AssetDatabase.CopyAsset returned false.");
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(absoluteTarget) ?? projectRoot);
                    File.Copy(absoluteSource, absoluteTarget, false);
                    AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
                copied = true;
                var result = Inspect(targetPath, byteCount);
                if (!result.IsValid)
                {
                    AssetDatabase.DeleteAsset(targetPath);
                    throw new InvalidOperationException("STAGING_VALIDATION_FAILED: " + string.Join(" | ", result.Issues));
                }
                AssetDatabase.SaveAssets();
                return result;
            }
            catch
            {
                if (copied) AssetDatabase.DeleteAsset(targetPath);
                throw;
            }
        }

        private static AssetModelerStagingResult Inspect(string assetPath, long byteCount)
        {
            var issues = new List<string>();
            int meshCount = 0, vertices = 0, triangles = 0;
            var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            for (int i = 0; i < assets.Length; i++)
            {
                if (!(assets[i] is Mesh mesh)) continue;
                meshCount++;
                vertices += mesh.vertexCount;
                if (!mesh.isReadable)
                {
                    issues.Add($"ERROR MESH_NOT_READABLE: '{mesh.name}' must enable Read/Write for modeling.");
                    continue;
                }
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    if (mesh.GetTopology(subMesh) == MeshTopology.Triangles) triangles += mesh.GetIndices(subMesh, true).Length / 3;
                var validation = MeshValidator.Validate(mesh);
                for (int issue = 0; issue < validation.Issues.Count; issue++)
                {
                    var value = validation.Issues[issue];
                    issues.Add($"{(value.IsError ? "ERROR" : "WARNING")} {value.Code}: {mesh.name}: {value.Message}");
                }
            }
            if (meshCount == 0) issues.Add("ERROR NO_MESHES: staged asset contains no Unity Mesh sub-assets.");
            bool valid = meshCount > 0;
            for (int i = 0; i < issues.Count; i++) if (issues[i].StartsWith("ERROR ", StringComparison.Ordinal)) valid = false;
            return new AssetModelerStagingResult
            {
                AssetPath = assetPath,
                ByteCount = byteCount,
                MeshCount = meshCount,
                VertexCount = vertices,
                TriangleCount = triangles,
                Issues = issues,
                IsValid = valid,
            };
        }
    }
}
