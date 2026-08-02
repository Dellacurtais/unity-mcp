using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Modeling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    public sealed class SmartBodySplitResult
    {
        public string SplitId { get; internal set; }
        public string OutputFolder { get; internal set; }
        public GameObject PartsRoot { get; internal set; }
        public BodyPartSet BodyPartSet { get; internal set; }
        public SmartBodySplitManifest Manifest { get; internal set; }
        public IReadOnlyList<string> CreatedAssetPaths { get; internal set; }
        public MeshValidationReport Validation { get; internal set; }
    }

    public static class SmartBodySplitService
    {
        public static SmartBodySplitResult Apply(
            SmartBodySplitProposal proposal,
            string outputFolder = null,
            string expectedSourceHash = null,
            string expectedPreviewHash = null)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            if (proposal.SourceMesh == null || proposal.SourceRenderer == null)
                throw new InvalidOperationException("Split proposal has no source mesh/renderer.");
            if (string.IsNullOrEmpty(expectedSourceHash) || string.IsNullOrEmpty(expectedPreviewHash))
                throw new InvalidOperationException(
                    "PREVIEW_APPROVAL_REQUIRED: expectedSourceHash and previewHash are required before creating split assets.");
            if (CurrentRendererMesh(proposal.SourceRenderer) != proposal.SourceMesh)
                throw new InvalidOperationException(
                    "STALE_SOURCE_BINDING: the renderer mesh changed after the split preview was created.");

            var currentHash = MeshFingerprint.Compute(proposal.SourceMesh);
            if (!string.Equals(currentHash, expectedSourceHash, StringComparison.Ordinal))
                throw new InvalidOperationException("STALE_SOURCE_HASH: source mesh changed after analysis.");
            if (!string.Equals(proposal.PreviewHash, expectedPreviewHash, StringComparison.Ordinal))
                throw new InvalidOperationException("STALE_PREVIEW_HASH: split proposal differs from the approved preview.");

            var splitId = Guid.NewGuid().ToString("N");
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(
                string.IsNullOrWhiteSpace(outputFolder)
                    ? $"{AssetModelerAssetPaths.DefaultRoot}/SmartSplits/{AssetModelerAssetPaths.SafeName(proposal.Target.name)}"
                    : outputFolder);
            var createdPaths = new List<string>();
            var buildParts = new List<SplitPartBuildData>();
            GameObject partsRoot = null;
            try
            {
                var snapshot = MeshChannelSnapshot.Capture(proposal.SourceMesh);
                for (int partIndex = 0; partIndex < proposal.Parts.Count; partIndex++)
                {
                    var part = proposal.Parts[partIndex];
                    if (part.Triangles.Count == 0) continue;
                    buildParts.Add(BuildPart(proposal, snapshot, part, folder, createdPaths));
                }

                var validation = SplitFidelityValidator.Validate(proposal, snapshot, buildParts);
                if (!validation.IsValid)
                    throw new InvalidOperationException("SPLIT_VALIDATION_FAILED: " + ValidationSummary(validation));

                var skeleton = BuildSkeletonSignature(proposal);
                var definitions = new List<BodyPartDefinition>(buildParts.Count);
                var manifestParts = new List<SmartBodySplitManifestPart>(buildParts.Count);
                var sourceMaterials = proposal.SourceRenderer.sharedMaterials;
                for (int i = 0; i < buildParts.Count; i++)
                {
                    var build = buildParts[i];
                    var definition = new BodyPartDefinition();
                    definition.Configure(
                        build.Proposal.SlotId,
                        build.Proposal.Label,
                        build.Mesh,
                        sourceMaterials,
                        build.Proposal.Triangles.Count,
                        build.SourceVertexIndices.Count,
                        build.Proposal.AverageConfidence);
                    definitions.Add(definition);

                    var manifestPart = new SmartBodySplitManifestPart();
                    manifestPart.Configure(
                        build.Proposal.SlotId,
                        build.Proposal.Label,
                        build.Mesh,
                        build.AssetPath,
                        build.Proposal.Triangles.Count,
                        build.SourceVertexIndices.ToArray(),
                        build.Proposal.AverageConfidence);
                    manifestParts.Add(manifestPart);
                }

                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    proposal.SourceMesh,
                    out string sourceAssetGuid,
                    out long _);
                var bodyPartSet = ScriptableObject.CreateInstance<BodyPartSet>();
                bodyPartSet.name = proposal.SourceMesh.name + "_BodyPartSet";
                bodyPartSet.Configure(sourceAssetGuid, proposal.SourceHash, splitId, skeleton, definitions);
                var bodyPartSetPath = AssetModelerAssetPaths.UniqueAssetPath(folder, bodyPartSet.name, ".asset");
                AssetDatabase.CreateAsset(bodyPartSet, bodyPartSetPath);
                createdPaths.Add(bodyPartSetPath);

                var manifest = ScriptableObject.CreateInstance<SmartBodySplitManifest>();
                manifest.name = proposal.SourceMesh.name + "_SplitManifest";
                manifest.Configure(
                    splitId,
                    proposal,
                    validation.IsValid,
                    ValidationMessages(validation),
                    manifestParts);
                var manifestPath = AssetModelerAssetPaths.UniqueAssetPath(folder, manifest.name, ".asset");
                AssetDatabase.CreateAsset(manifest, manifestPath);
                createdPaths.Add(manifestPath);

                partsRoot = CreateSceneParts(proposal, buildParts, bodyPartSet);
                using (var mutation = new MutationScope("Smart Body Split"))
                {
                    mutation.RegisterCreated(partsRoot, "Create split body parts");
                    for (int i = 0; i < buildParts.Count; i++)
                        mutation.RegisterCreated(buildParts[i].Mesh, "Create split mesh asset");
                    mutation.RegisterCreated(bodyPartSet, "Create body part set");
                    mutation.RegisterCreated(manifest, "Create split manifest");
                    mutation.Complete();
                }
                AssetDatabase.SaveAssets();

                return new SmartBodySplitResult
                {
                    SplitId = splitId,
                    OutputFolder = folder,
                    PartsRoot = partsRoot,
                    BodyPartSet = bodyPartSet,
                    Manifest = manifest,
                    CreatedAssetPaths = createdPaths,
                    Validation = validation,
                };
            }
            catch
            {
                if (partsRoot != null) UnityEngine.Object.DestroyImmediate(partsRoot);
                for (int i = createdPaths.Count - 1; i >= 0; i--)
                    if (!string.IsNullOrEmpty(createdPaths[i])) AssetDatabase.DeleteAsset(createdPaths[i]);
                throw;
            }
        }

        private static SplitPartBuildData BuildPart(
            SmartBodySplitProposal proposal,
            MeshChannelSnapshot snapshot,
            SmartBodySplitPartProposal part,
            string folder,
            List<string> createdPaths)
        {
            var sourceToDestination = new Dictionary<int, int>();
            var sourceVertexIndices = new List<int>();
            var submeshIndices = new List<int>[proposal.SourceMesh.subMeshCount];
            for (int submesh = 0; submesh < submeshIndices.Length; submesh++)
                submeshIndices[submesh] = new List<int>();

            for (int triangleIndex = 0; triangleIndex < part.Triangles.Count; triangleIndex++)
            {
                var triangle = part.Triangles[triangleIndex];
                var indices = submeshIndices[triangle.SubMesh];
                indices.Add(GetOrAdd(triangle.A, sourceToDestination, sourceVertexIndices));
                indices.Add(GetOrAdd(triangle.B, sourceToDestination, sourceVertexIndices));
                indices.Add(GetOrAdd(triangle.C, sourceToDestination, sourceVertexIndices));
            }

            var mesh = new Mesh
            {
                name = proposal.SourceMesh.name + "_" + AssetModelerAssetPaths.SafeName(part.SlotId),
                indexFormat = proposal.SourceMesh.indexFormat == IndexFormat.UInt32 || sourceVertexIndices.Count > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16,
            };
            snapshot.ApplyVertexChannels(mesh, sourceVertexIndices);
            mesh.subMeshCount = proposal.SourceMesh.subMeshCount;
            for (int submesh = 0; submesh < submeshIndices.Length; submesh++)
                mesh.SetTriangles(submeshIndices[submesh], submesh, false);
            mesh.RecalculateBounds();

            var assetPath = AssetModelerAssetPaths.UniqueAssetPath(folder, mesh.name, ".asset");
            AssetDatabase.CreateAsset(mesh, assetPath);
            createdPaths.Add(assetPath);
            return new SplitPartBuildData
            {
                Proposal = part,
                Mesh = mesh,
                AssetPath = assetPath,
                SourceVertexIndices = sourceVertexIndices,
            };
        }

        private static int GetOrAdd(
            int sourceIndex,
            Dictionary<int, int> sourceToDestination,
            List<int> sourceVertexIndices)
        {
            if (sourceToDestination.TryGetValue(sourceIndex, out var destinationIndex)) return destinationIndex;
            destinationIndex = sourceVertexIndices.Count;
            sourceToDestination.Add(sourceIndex, destinationIndex);
            sourceVertexIndices.Add(sourceIndex);
            return destinationIndex;
        }

        private static Mesh CurrentRendererMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            return renderer != null ? renderer.GetComponent<MeshFilter>()?.sharedMesh : null;
        }

        private static GameObject CreateSceneParts(
            SmartBodySplitProposal proposal,
            IReadOnlyList<SplitPartBuildData> parts,
            BodyPartSet bodyPartSet)
        {
            var sourceTransform = proposal.SourceRenderer.transform;
            var root = new GameObject(sourceTransform.gameObject.name + "_SplitParts");
            root.transform.SetParent(sourceTransform.parent, false);
            root.transform.localPosition = sourceTransform.localPosition;
            root.transform.localRotation = sourceTransform.localRotation;
            root.transform.localScale = sourceTransform.localScale;

            var modularSlots = new List<ModularCharacterSlot>(parts.Count);
            var sourceSkinned = proposal.SourceRenderer as SkinnedMeshRenderer;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var child = new GameObject(part.Proposal.Label);
                child.transform.SetParent(root.transform, false);
                Renderer destinationRenderer;
                if (sourceSkinned != null)
                {
                    var renderer = child.AddComponent<SkinnedMeshRenderer>();
                    CopyRendererSettings(sourceSkinned, renderer);
                    renderer.sharedMesh = part.Mesh;
                    renderer.bones = sourceSkinned.bones;
                    renderer.rootBone = sourceSkinned.rootBone;
                    renderer.localBounds = sourceSkinned.localBounds;
                    renderer.quality = sourceSkinned.quality;
                    renderer.updateWhenOffscreen = sourceSkinned.updateWhenOffscreen;
                    renderer.skinnedMotionVectors = sourceSkinned.skinnedMotionVectors;
                    for (int shapeIndex = 0; shapeIndex < part.Mesh.blendShapeCount; shapeIndex++)
                        renderer.SetBlendShapeWeight(shapeIndex, sourceSkinned.GetBlendShapeWeight(shapeIndex));
                    destinationRenderer = renderer;

                    var slot = new ModularCharacterSlot();
                    slot.Configure(part.Proposal.SlotId, renderer);
                    modularSlots.Add(slot);
                }
                else
                {
                    child.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                    var renderer = child.AddComponent<MeshRenderer>();
                    CopyRendererSettings(proposal.SourceRenderer, renderer);
                    destinationRenderer = renderer;
                }
                destinationRenderer.sharedMaterials = proposal.SourceRenderer.sharedMaterials;
            }

            if (sourceSkinned != null)
            {
                var assembler = root.AddComponent<ModularCharacterAssembler>();
                assembler.Configure(bodyPartSet, sourceSkinned.rootBone, sourceSkinned.bones, modularSlots);
            }
            return root;
        }

        private static void CopyRendererSettings(Renderer source, Renderer destination)
        {
            destination.enabled = source.enabled;
            destination.shadowCastingMode = source.shadowCastingMode;
            destination.receiveShadows = source.receiveShadows;
            destination.lightProbeUsage = source.lightProbeUsage;
            destination.reflectionProbeUsage = source.reflectionProbeUsage;
            destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
            destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            destination.probeAnchor = source.probeAnchor;
        }

        private static SkeletonCompatibilitySignature BuildSkeletonSignature(SmartBodySplitProposal proposal)
        {
            var signature = new SkeletonCompatibilitySignature();
            var skinned = proposal.SourceRenderer as SkinnedMeshRenderer;
            if (skinned == null)
            {
                signature.Configure("None", string.Empty, string.Empty, 0);
                return signature;
            }

            var hierarchy = new StringBuilder();
            var bones = skinned.bones ?? Array.Empty<Transform>();
            for (int i = 0; i < bones.Length; i++)
                hierarchy.Append(i).Append(':').Append(TransformPath(bones[i], skinned.rootBone)).Append('|');
            signature.Configure(
                proposal.RigType,
                Sha256(hierarchy.ToString()),
                HashBindPoses(proposal.SourceMesh.bindposes),
                bones.Length);
            return signature;
        }

        private static string TransformPath(Transform bone, Transform root)
        {
            if (bone == null) return "<null>";
            var parts = new Stack<string>();
            var current = bone;
            while (current != null)
            {
                parts.Push(current.name);
                if (current == root) break;
                current = current.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private static string HashBindPoses(Matrix4x4[] bindPoses)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(bindPoses?.Length ?? 0);
                if (bindPoses != null)
                    for (int pose = 0; pose < bindPoses.Length; pose++)
                        for (int value = 0; value < 16; value++) writer.Write(bindPoses[pose][value]);
                writer.Flush();
                stream.Position = 0;
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        private static IEnumerable<string> ValidationMessages(MeshValidationReport report)
        {
            for (int i = 0; i < report.Issues.Count; i++)
            {
                var issue = report.Issues[i];
                yield return $"{(issue.IsError ? "ERROR" : "WARNING")} {issue.Code}: {issue.Message}";
            }
        }

        private static string ValidationSummary(MeshValidationReport report)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < report.Issues.Count && i < 5; i++)
            {
                if (i > 0) builder.Append(" | ");
                builder.Append(report.Issues[i].Code).Append(": ").Append(report.Issues[i].Message);
            }
            return builder.Length > 0 ? builder.ToString() : "unknown validation error";
        }
    }
}
