using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Modeling;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    internal static class ModularCharacterPrefabService
    {
        public static string Create(
            SmartBodySplitProposal proposal,
            IReadOnlyList<SplitPartBuildData> parts,
            BodyPartSet bodyPartSet,
            string folder)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            var characterRoot = FindCharacterRoot(proposal);
            var sourceAsset = PrefabUtility.GetCorrespondingObjectFromSource(characterRoot) as GameObject;
            GameObject temporary = null;
            try
            {
                temporary = sourceAsset != null
                    ? PrefabUtility.InstantiatePrefab(sourceAsset) as GameObject
                    : UnityEngine.Object.Instantiate(characterRoot);
                if (temporary == null) throw new InvalidOperationException("PREFAB_BUILD_FAILED: could not clone the character root.");
                temporary.name = characterRoot.name + "_Modular";

                var rendererPath = AnimationUtility.CalculateTransformPath(
                    proposal.SourceRenderer.transform,
                    characterRoot.transform);
                var destinationTransform = string.IsNullOrEmpty(rendererPath)
                    ? temporary.transform
                    : temporary.transform.Find(rendererPath);
                if (destinationTransform == null)
                    throw new InvalidOperationException($"PREFAB_RENDERER_MISSING: renderer path '{rendererPath}' was not cloned.");
                var destinationRenderer = destinationTransform.GetComponent(proposal.SourceRenderer.GetType()) as Renderer;
                if (destinationRenderer == null)
                    throw new InvalidOperationException("PREFAB_RENDERER_MISSING: cloned character has no matching renderer.");
                destinationRenderer.enabled = false;

                var partsRoot = new GameObject("AssetModeler_ModularParts");
                partsRoot.transform.SetParent(destinationTransform.parent, false);
                partsRoot.transform.localPosition = destinationTransform.localPosition;
                partsRoot.transform.localRotation = destinationTransform.localRotation;
                partsRoot.transform.localScale = destinationTransform.localScale;

                var slots = new List<ModularCharacterSlot>(parts.Count);
                var sourceSkinned = destinationRenderer as SkinnedMeshRenderer;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    var child = new GameObject(part.Proposal.Label);
                    child.transform.SetParent(partsRoot.transform, false);
                    if (sourceSkinned != null)
                    {
                        var renderer = child.AddComponent<SkinnedMeshRenderer>();
                        CopyRendererSettings(sourceSkinned, renderer);
                        renderer.sharedMesh = part.Mesh;
                        renderer.sharedMaterials = destinationRenderer.sharedMaterials;
                        renderer.bones = sourceSkinned.bones;
                        renderer.rootBone = sourceSkinned.rootBone;
                        renderer.localBounds = sourceSkinned.localBounds;
                        renderer.quality = sourceSkinned.quality;
                        renderer.updateWhenOffscreen = sourceSkinned.updateWhenOffscreen;
                        for (int shape = 0; shape < part.Mesh.blendShapeCount; shape++)
                            renderer.SetBlendShapeWeight(shape, sourceSkinned.GetBlendShapeWeight(shape));
                        var slot = new ModularCharacterSlot();
                        slot.Configure(part.Proposal.SlotId, renderer);
                        slots.Add(slot);
                    }
                    else
                    {
                        child.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                        var renderer = child.AddComponent<MeshRenderer>();
                        CopyRendererSettings(destinationRenderer, renderer);
                        renderer.sharedMaterials = destinationRenderer.sharedMaterials;
                    }
                }

                if (sourceSkinned != null)
                {
                    var assembler = temporary.GetComponent<ModularCharacterAssembler>()
                        ?? temporary.AddComponent<ModularCharacterAssembler>();
                    assembler.Configure(bodyPartSet, sourceSkinned.rootBone, sourceSkinned.bones, slots);
                }

                var prefabPath = AssetModelerAssetPaths.UniqueAssetPath(
                    folder,
                    temporary.name,
                    ".prefab");
                PrefabUtility.SaveAsPrefabAsset(temporary, prefabPath, out var success);
                if (!success) throw new InvalidOperationException("PREFAB_SAVE_FAILED: Unity did not save the modular Prefab/Variant.");
                return prefabPath;
            }
            finally
            {
                if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary);
            }
        }

        private static GameObject FindCharacterRoot(SmartBodySplitProposal proposal)
        {
            var animator = proposal.SourceRenderer.GetComponentInParent<Animator>();
            if (animator != null) return animator.gameObject;
            var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(proposal.SourceRenderer.gameObject);
            if (prefabRoot != null) return prefabRoot;
            return proposal.Target != null ? proposal.Target : proposal.SourceRenderer.gameObject;
        }

        private static void CopyRendererSettings(Renderer source, Renderer destination)
        {
            destination.enabled = true;
            destination.shadowCastingMode = source.shadowCastingMode;
            destination.receiveShadows = source.receiveShadows;
            destination.lightProbeUsage = source.lightProbeUsage;
            destination.reflectionProbeUsage = source.reflectionProbeUsage;
            destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
            destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            destination.probeAnchor = source.probeAnchor;
        }
    }
}
