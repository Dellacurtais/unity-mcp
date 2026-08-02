using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Assets
{
    public sealed class EditableMeshResult
    {
        public GameObject EditableObject { get; internal set; }
        public Renderer EditableRenderer { get; internal set; }
        public Mesh EditableMesh { get; internal set; }
        public MeshEditDocument Document { get; internal set; }
        public IReadOnlyList<string> CreatedAssetPaths { get; internal set; }
    }

    public static class EditableMeshFactory
    {
        public static EditableMeshResult Create(GameObject target, string outputFolder = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!TryResolveRenderer(target, out var sourceRenderer, out var sourceMesh, out var error))
                throw new InvalidOperationException(error);

            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(
                string.IsNullOrWhiteSpace(outputFolder)
                    ? $"{AssetModelerAssetPaths.DefaultRoot}/Editable/{AssetModelerAssetPaths.SafeName(target.name)}"
                    : outputFolder);
            var createdPaths = new List<string>();
            GameObject editable = null;
            try
            {
                var meshCopy = UnityEngine.Object.Instantiate(sourceMesh);
                meshCopy.name = sourceMesh.name + "_Editable";
                var meshPath = AssetModelerAssetPaths.UniqueAssetPath(folder, meshCopy.name, ".asset");
                AssetDatabase.CreateAsset(meshCopy, meshPath);
                createdPaths.Add(meshPath);

                editable = UnityEngine.Object.Instantiate(sourceRenderer.gameObject, sourceRenderer.transform.parent, false);
                editable.name = sourceRenderer.gameObject.name + "_Editable";
                Renderer editableRenderer;
                var editableSkinned = editable.GetComponent<SkinnedMeshRenderer>();
                if (editableSkinned != null)
                {
                    editableSkinned.sharedMesh = meshCopy;
                    editableRenderer = editableSkinned;
                }
                else
                {
                    var filter = editable.GetComponent<MeshFilter>();
                    if (filter == null) throw new InvalidOperationException("Cloned static renderer has no MeshFilter.");
                    filter.sharedMesh = meshCopy;
                    editableRenderer = editable.GetComponent<MeshRenderer>();
                }

                var document = ScriptableObject.CreateInstance<MeshEditDocument>();
                document.name = sourceMesh.name + "_MeshEditDocument";
                document.Initialize(sourceRenderer.gameObject, sourceMesh, meshCopy);
                var documentPath = AssetModelerAssetPaths.UniqueAssetPath(folder, document.name, ".asset");
                AssetDatabase.CreateAsset(document, documentPath);
                createdPaths.Add(documentPath);

                using (var mutation = new MutationScope("Create editable mesh copy"))
                {
                    mutation.RegisterCreated(editable, "Create editable mesh copy");
                    mutation.RegisterCreated(meshCopy, "Create editable mesh asset");
                    mutation.RegisterCreated(document, "Create mesh edit document");
                    mutation.Complete();
                }
                AssetDatabase.SaveAssets();

                return new EditableMeshResult
                {
                    EditableObject = editable,
                    EditableRenderer = editableRenderer,
                    EditableMesh = meshCopy,
                    Document = document,
                    CreatedAssetPaths = createdPaths,
                };
            }
            catch
            {
                if (editable != null) UnityEngine.Object.DestroyImmediate(editable);
                for (int i = createdPaths.Count - 1; i >= 0; i--)
                    if (!string.IsNullOrEmpty(createdPaths[i])) AssetDatabase.DeleteAsset(createdPaths[i]);
                throw;
            }
        }

        public static bool TryResolveRenderer(
            GameObject target,
            out Renderer renderer,
            out Mesh mesh,
            out string error)
        {
            renderer = null;
            mesh = null;
            error = null;
            if (target == null)
            {
                error = "Target is null.";
                return false;
            }

            var skinnedRenderers = target.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                var skinned = skinnedRenderers[i];
                if (skinned == null || skinned.sharedMesh == null) continue;
                renderer = skinned;
                mesh = skinned.sharedMesh;
                return true;
            }

            var filters = target.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var filter = filters[i];
                if (filter == null || filter.sharedMesh == null) continue;
                renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null)
                {
                    error = $"MeshFilter '{filter.name}' has no MeshRenderer.";
                    return false;
                }
                mesh = filter.sharedMesh;
                return true;
            }

            error = $"GameObject '{target.name}' has no readable MeshFilter or SkinnedMeshRenderer mesh.";
            return false;
        }
    }
}
