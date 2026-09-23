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
        public GameObject EditableRoot { get; internal set; }
        public Renderer EditableRenderer { get; internal set; }
        public Mesh EditableMesh { get; internal set; }
        public MeshEditDocument Document { get; internal set; }
        public IReadOnlyList<string> CreatedAssetPaths { get; internal set; }
    }

    public static class EditableMeshFactory
    {
        public static EditableMeshResult Create(GameObject target, string outputFolder = null)
            => CreateModelCopy(target, null, outputFolder);

        public static EditableMeshResult CreateModelCopy(GameObject target, Renderer selectedRenderer, string outputFolder = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (selectedRenderer != null && selectedRenderer.transform != target.transform && !selectedRenderer.transform.IsChildOf(target.transform))
                throw new InvalidOperationException("RENDERER_OUTSIDE_ROOT: select a renderer inside the model root.");
            if (!TryResolveRenderer(selectedRenderer != null ? selectedRenderer.gameObject : target, out var sourceRenderer, out var sourceMesh, out var error))
                throw new InvalidOperationException(error);
            if (selectedRenderer != null && selectedRenderer != sourceRenderer)
                throw new InvalidOperationException("AMBIGUOUS_MESH: the selected renderer does not own the resolved mesh.");
            if (!sourceMesh.isReadable)
                throw new InvalidOperationException("MESH_NOT_READABLE: enable Read/Write on the source model before creating an editable copy.");
            var sourceRoot = ResolveCopyRoot(target, sourceRenderer);
            // Resolve collider ownership before creating folders/assets. Shared
            // meshes do not imply that all colliders edit the selected renderer.
            var ownedColliders = ResolveOwnedColliders(sourceRoot, sourceRenderer, sourceMesh);
            var rendererPath = ChildIndices(sourceRoot.transform, sourceRenderer.transform);

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

                editable = UnityEngine.Object.Instantiate(sourceRoot, sourceRoot.transform.parent, false);
                editable.name = sourceRoot.name + "_Editable";
                var rendererObject = FollowChildren(editable.transform, rendererPath).gameObject;
                Renderer editableRenderer;
                var editableSkinned = rendererObject.GetComponent<SkinnedMeshRenderer>();
                if (editableSkinned != null)
                {
                    editableSkinned.sharedMesh = meshCopy;
                    editableRenderer = editableSkinned;
                }
                else
                {
                    var filter = rendererObject.GetComponent<MeshFilter>();
                    if (filter == null) throw new InvalidOperationException("Cloned static renderer has no MeshFilter.");
                    filter.sharedMesh = meshCopy;
                    editableRenderer = rendererObject.GetComponent<MeshRenderer>();
                }
                foreach (var sourceCollider in ownedColliders)
                {
                    var colliderObject = FollowChildren(editable.transform, ChildIndices(sourceRoot.transform, sourceCollider.transform));
                    int index = Array.IndexOf(sourceCollider.GetComponents<MeshCollider>(), sourceCollider);
                    colliderObject.GetComponents<MeshCollider>()[index].sharedMesh = meshCopy;
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
                    // Keep persistent assets available if the user saves a prefab
                    // before Undo. Undo/Redo controls the scene copy, not its files.
                    mutation.Complete();
                }
                AssetDatabase.SaveAssets();

                return new EditableMeshResult
                {
                    EditableObject = editableRenderer.gameObject,
                    EditableRoot = editable,
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

        public static EditableMeshResult CreateForExistingCopy(GameObject editableRoot, Renderer renderer, string outputFolder = null)
        {
            if (editableRoot == null || renderer == null || EditorUtility.IsPersistent(editableRoot)
                || (renderer.transform != editableRoot.transform && !renderer.transform.IsChildOf(editableRoot.transform)))
                throw new InvalidOperationException("EDITABLE_ROOT_REQUIRED: choose a renderer inside the current scene copy.");
            if (!TryResolveRenderer(renderer.gameObject, out var resolved, out var sourceMesh, out var error) || resolved != renderer)
                throw new InvalidOperationException(error ?? "AMBIGUOUS_MESH: select one mesh renderer.");
            if (!sourceMesh.isReadable)
                throw new InvalidOperationException("MESH_NOT_READABLE: enable Read/Write on the source model.");
            if (ResolveCopyRoot(editableRoot, renderer) != editableRoot)
                throw new InvalidOperationException("EXTERNAL_RIG: the copy must contain its complete skeleton.");
            var ownedColliders = ResolveOwnedColliders(editableRoot, renderer, sourceMesh);
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(outputFolder)
                ? AssetModelerAssetPaths.DefaultRoot + "/Editable/" + AssetModelerAssetPaths.SafeName(editableRoot.name) : outputFolder);
            var created = new List<string>();
            try
            {
                var mesh = UnityEngine.Object.Instantiate(sourceMesh);
                mesh.name = sourceMesh.name + "_Editable";
                var meshPath = AssetModelerAssetPaths.UniqueAssetPath(folder, mesh.name, ".asset");
                AssetDatabase.CreateAsset(mesh, meshPath); created.Add(meshPath);
                var document = ScriptableObject.CreateInstance<MeshEditDocument>();
                document.name = sourceMesh.name + "_MeshEditDocument";
                document.Initialize(renderer.gameObject, sourceMesh, mesh);
                var documentPath = AssetModelerAssetPaths.UniqueAssetPath(folder, document.name, ".asset");
                AssetDatabase.CreateAsset(document, documentPath); created.Add(documentPath);
                using (var mutation = new MutationScope("Prepare mesh in editable copy"))
                {
                    if (renderer is SkinnedMeshRenderer skin)
                    {
                        mutation.Record(skin); skin.sharedMesh = mesh;
                    }
                    else
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        mutation.Record(filter); filter.sharedMesh = mesh;
                    }
                    foreach (var collider in ownedColliders)
                    {
                        mutation.Record(collider); collider.sharedMesh = mesh;
                    }
                    AssetDatabase.SaveAssets();
                    mutation.Complete();
                }
                return new EditableMeshResult
                {
                    EditableObject = renderer.gameObject, EditableRoot = editableRoot, EditableRenderer = renderer,
                    EditableMesh = mesh, Document = document, CreatedAssetPaths = created,
                };
            }
            catch
            {
                for (int i = created.Count - 1; i >= 0; i--) AssetDatabase.DeleteAsset(created[i]);
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

            // A directly selected renderer is explicit even inside a multi-mesh character.
            var directSkin = target.GetComponent<SkinnedMeshRenderer>();
            if (directSkin != null && directSkin.sharedMesh != null)
            {
                renderer = directSkin;
                mesh = directSkin.sharedMesh;
                return true;
            }
            var directFilter = target.GetComponent<MeshFilter>();
            var directRenderer = target.GetComponent<MeshRenderer>();
            if (directFilter != null && directRenderer != null && directFilter.sharedMesh != null)
            {
                renderer = directRenderer;
                mesh = directFilter.sharedMesh;
                return true;
            }
            var candidates = new List<Renderer>();
            var meshes = new List<Mesh>();
            var skinnedRenderers = target.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                var skinned = skinnedRenderers[i];
                if (skinned == null || skinned.sharedMesh == null) continue;
                candidates.Add(skinned);
                meshes.Add(skinned.sharedMesh);
            }

            var filters = target.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var filter = filters[i];
                if (filter == null || filter.sharedMesh == null) continue;
                var candidate = filter.GetComponent<MeshRenderer>();
                if (candidate == null) continue;
                candidates.Add(candidate);
                meshes.Add(filter.sharedMesh);
            }
            if (candidates.Count == 1)
            {
                renderer = candidates[0];
                mesh = meshes[0];
                return true;
            }
            error = candidates.Count == 0
                ? $"GameObject '{target.name}' has no MeshFilter or SkinnedMeshRenderer mesh."
                : $"AMBIGUOUS_MESH: '{target.name}' has {candidates.Count} meshes. Select the specific renderer to edit; size normalization accepts the complete model root.";
            return false;
        }

        private static GameObject ResolveCopyRoot(GameObject target, Renderer renderer)
        {
            var root = target.transform;
            var skin = renderer as SkinnedMeshRenderer;
            var required = skin != null ? new List<Transform>(skin.bones) : new List<Transform>();
            if (skin != null && skin.rootBone != null) required.Add(skin.rootBone);
            // Static meshes may also be animated through child Transform curves.
            var animator = renderer.GetComponentInParent<Animator>(true);
            if (animator != null) required.Add(animator.transform);
            var animation = renderer.GetComponentInParent<Animation>(true);
            if (animation != null) required.Add(animation.transform);
            foreach (var transform in required)
            {
                if (transform == null) continue;
                while (root != null && transform != root && !transform.IsChildOf(root)) root = root.parent;
                if (root == null)
                    throw new InvalidOperationException("EXTERNAL_RIG: mesh and skeleton must belong to a common model hierarchy before copying.");
            }
            // Other skinned renderers copied with the root must not retain external bones either.
            foreach (var other in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                foreach (var bone in other.bones)
                    if (bone != null && bone != root && !bone.IsChildOf(root))
                        throw new InvalidOperationException("EXTERNAL_RIG: the model contains bones outside its root. Select a complete model hierarchy.");
                if (other.rootBone != null && other.rootBone != root && !other.rootBone.IsChildOf(root))
                    throw new InvalidOperationException("EXTERNAL_RIG: the model contains a root bone outside its hierarchy.");
            }
            return root.gameObject;
        }

        private static List<MeshCollider> ResolveOwnedColliders(GameObject root, Renderer selectedRenderer, Mesh mesh)
        {
            int ownerCount = 0;
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (skin.sharedMesh == mesh) ownerCount++;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh == mesh && filter.GetComponent<MeshRenderer>() != null) ownerCount++;

            var result = new List<MeshCollider>();
            foreach (var collider in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (collider.sharedMesh != mesh) continue;
                if (collider.gameObject == selectedRenderer.gameObject)
                {
                    result.Add(collider);
                    continue;
                }
                var filter = collider.GetComponent<MeshFilter>();
                var skin = collider.GetComponent<SkinnedMeshRenderer>();
                if (filter != null && filter.sharedMesh == mesh || skin != null && skin.sharedMesh == mesh)
                    continue; // This collider has an explicit owner other than the selected renderer.
                if (ownerCount != 1)
                    throw new InvalidOperationException("AMBIGUOUS_MESH_COLLIDER: collider '" + collider.name
                        + "' shares this mesh with multiple renderers. Place it on its owning renderer before editing.");
                result.Add(collider);
            }
            return result;
        }

        private static List<int> ChildIndices(Transform root, Transform child)
        {
            var path = new List<int>();
            for (var current = child; current != root; current = current.parent)
                path.Add(current.GetSiblingIndex());
            path.Reverse();
            return path;
        }

        private static Transform FollowChildren(Transform root, List<int> path)
        {
            foreach (int index in path) root = root.GetChild(index);
            return root;
        }
    }
}
