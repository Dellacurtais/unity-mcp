using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    public static class SmartBodySplitAnalyzer
    {
        private const int ScorePartCount = (int)SemanticBodyPart.Unassigned;

        public static SmartBodySplitProposal Analyze(
            GameObject target,
            SmartBodySplitPreset preset = SmartBodySplitPreset.Core)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!EditableMeshFactory.TryResolveRenderer(target, out var renderer, out var mesh, out var error))
                throw new InvalidOperationException(error);
            if (mesh.vertexCount == 0) throw new InvalidOperationException("Source mesh has no vertices.");
            if (!mesh.isReadable)
                throw new InvalidOperationException(
                    "MESH_NOT_READABLE: enable Read/Write in the model import settings before Smart Body Split. " +
                    "The tool will not change the imported FBX settings automatically.");
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                    throw new InvalidOperationException($"Submesh {submesh} uses {mesh.GetTopology(submesh)}; Smart Body Split requires triangles.");

            var skinned = renderer as SkinnedMeshRenderer;
            var animator = target.GetComponent<Animator>()
                ?? target.GetComponentInParent<Animator>()
                ?? target.GetComponentInChildren<Animator>(true);
            var activeParts = SmartBodySplitPresets.Parts(preset);
            var boneParts = skinned != null ? BuildBonePartMap(skinned, animator, preset) : Array.Empty<SemanticBodyPart>();
            var scores = BuildVertexScores(mesh, skinned, boneParts, preset, activeParts);
            var proposal = new SmartBodySplitProposal
            {
                Target = target,
                SourceRenderer = renderer,
                SourceMesh = mesh,
                SourceHash = MeshFingerprint.Compute(mesh),
                IsRigged = skinned != null && skinned.bones != null && skinned.bones.Length > 0,
                RigType = skinned == null ? "None" : animator != null && animator.isHuman ? "Humanoid" : "Generic",
                Preset = preset,
            };

            if (skinned == null)
                proposal.Warnings.Add("Mesh sem rig: a proposta usa geometria normalizada e deve ser revisada visualmente.");
            else if (animator == null || !animator.isHuman)
                proposal.Warnings.Add("Rig Generic: a proposta usa nomes, hierarquia de bones e pesos; revise bones com nomes incomuns.");
            if (skinned == null && preset == SmartBodySplitPreset.HandsAndFeet)
                proposal.Warnings.Add("Mãos e pés sem rig dependem da posição das extremidades; revise essas quatro cores com atenção.");

            var partLookup = new Dictionary<SemanticBodyPart, SmartBodySplitPartProposal>();
            for (int part = 0; part < activeParts.Count; part++)
            {
                var semanticPart = activeParts[part];
                var partProposal = new SmartBodySplitPartProposal(semanticPart);
                proposal.Parts.Add(partProposal);
                partLookup.Add(semanticPart, partProposal);
            }

            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                var triangles = mesh.GetTriangles(submesh, true);
                for (int index = 0; index < triangles.Length; index += 3)
                {
                    int a = triangles[index];
                    int b = triangles[index + 1];
                    int c = triangles[index + 2];
                    var part = ClassifyTriangle(scores, activeParts, a, b, c, out var confidence);
                    partLookup[part].Triangles.Add(new SourceTriangle(
                        submesh,
                        index / 3,
                        a,
                        b,
                        c,
                        confidence));
                }
            }

            proposal.Parts.RemoveAll(part => part.Triangles.Count == 0);
            proposal.FinalizeProposal();
            if (proposal.SourceTriangleCount == 0) throw new InvalidOperationException("Source mesh contains no triangles.");
            if (proposal.AverageConfidence < 0.55f)
                proposal.Warnings.Add("Confiança global baixa; confirme as cores antes de criar assets.");
            return proposal;
        }

        private static float[] BuildVertexScores(
            Mesh mesh,
            SkinnedMeshRenderer renderer,
            SemanticBodyPart[] boneParts,
            SmartBodySplitPreset preset,
            IReadOnlyList<SemanticBodyPart> activeParts)
        {
            var scores = new float[mesh.vertexCount * ScorePartCount];
            bool capturedWeights = false;
            if (renderer != null && boneParts.Length > 0)
            {
                using (var counts = mesh.GetBonesPerVertex())
                using (var weights = mesh.GetAllBoneWeights())
                {
                    if (counts.Length == mesh.vertexCount)
                    {
                        int weightIndex = 0;
                        for (int vertex = 0; vertex < mesh.vertexCount; vertex++)
                        {
                            int count = counts[vertex];
                            for (int influence = 0; influence < count; influence++, weightIndex++)
                            {
                                if (weightIndex >= weights.Length) break;
                                var weight = weights[weightIndex];
                                if (weight.boneIndex < 0 || weight.boneIndex >= boneParts.Length) continue;
                                var part = boneParts[weight.boneIndex];
                                if (part == SemanticBodyPart.Unassigned) continue;
                                scores[vertex * ScorePartCount + (int)part] += Mathf.Max(0f, weight.weight);
                                capturedWeights = true;
                            }
                        }
                    }
                }
            }

            var vertices = mesh.vertices;
            var bounds = mesh.bounds;
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                float total = 0f;
                for (int part = 0; part < activeParts.Count; part++)
                    total += scores[vertex * ScorePartCount + (int)activeParts[part]];
                if (!capturedWeights || total < 0.05f)
                {
                    var fallback = ClassifyPosition(vertices[vertex], bounds, preset);
                    scores[vertex * ScorePartCount + (int)fallback] += capturedWeights ? 0.15f : 1f;
                }
            }
            return scores;
        }

        private static SemanticBodyPart ClassifyTriangle(
            float[] scores,
            IReadOnlyList<SemanticBodyPart> activeParts,
            int a,
            int b,
            int c,
            out float confidence)
        {
            float best = float.MinValue;
            float second = float.MinValue;
            float sum = 0f;
            var bestPart = SemanticBodyPart.Torso;
            for (int partIndex = 0; partIndex < activeParts.Count; partIndex++)
            {
                var part = activeParts[partIndex];
                float score = scores[a * ScorePartCount + (int)part]
                    + scores[b * ScorePartCount + (int)part]
                    + scores[c * ScorePartCount + (int)part];
                sum += score;
                if (score > best)
                {
                    second = best;
                    best = score;
                    bestPart = part;
                }
                else if (score > second) second = score;
            }
            confidence = sum > 0f ? Mathf.Clamp01(best / sum) : 0f;
            if (second > float.MinValue && best > 0f)
                confidence *= Mathf.Clamp01(0.5f + 0.5f * ((best - second) / best));
            return bestPart;
        }

        private static SemanticBodyPart[] BuildBonePartMap(
            SkinnedMeshRenderer renderer,
            Animator animator,
            SmartBodySplitPreset preset)
        {
            var bones = renderer.bones ?? Array.Empty<Transform>();
            var map = new SemanticBodyPart[bones.Length];
            for (int i = 0; i < map.Length; i++) map[i] = SemanticBodyPart.Unassigned;

            Dictionary<Transform, HumanBodyBones> humanBones = null;
            if (animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isValid)
            {
                humanBones = new Dictionary<Transform, HumanBodyBones>();
                for (int value = 0; value < (int)HumanBodyBones.LastBone; value++)
                {
                    var humanBone = (HumanBodyBones)value;
                    var transform = animator.GetBoneTransform(humanBone);
                    if (transform != null) humanBones[transform] = humanBone;
                }
            }

            for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
            {
                var bone = bones[boneIndex];
                if (bone == null) continue;
                if (humanBones != null && humanBones.TryGetValue(bone, out var humanBone))
                    map[boneIndex] = FromHumanBone(humanBone, preset);
                else
                    map[boneIndex] = FromBoneName(bone.name, preset);
            }
            return map;
        }

        private static SemanticBodyPart FromHumanBone(HumanBodyBones bone, SmartBodySplitPreset preset)
        {
            var name = bone.ToString();
            if (name == "Head" || name == "Jaw" || name.EndsWith("Eye", StringComparison.Ordinal))
                return SemanticBodyPart.Head;
            if (name.StartsWith("Left", StringComparison.Ordinal))
            {
                if (preset == SmartBodySplitPreset.HandsAndFeet
                    && ContainsAny(name, "Hand", "Thumb", "Index", "Middle", "Ring", "Little"))
                    return SemanticBodyPart.LeftHand;
                if (preset == SmartBodySplitPreset.HandsAndFeet && ContainsAny(name, "Foot", "Toes"))
                    return SemanticBodyPart.LeftFoot;
                if (ContainsAny(name, "Leg", "Foot", "Toes")) return SemanticBodyPart.LeftLeg;
                if (ContainsAny(name, "Shoulder", "Arm", "Hand", "Thumb", "Index", "Middle", "Ring", "Little"))
                    return SemanticBodyPart.LeftArm;
            }
            if (name.StartsWith("Right", StringComparison.Ordinal))
            {
                if (preset == SmartBodySplitPreset.HandsAndFeet
                    && ContainsAny(name, "Hand", "Thumb", "Index", "Middle", "Ring", "Little"))
                    return SemanticBodyPart.RightHand;
                if (preset == SmartBodySplitPreset.HandsAndFeet && ContainsAny(name, "Foot", "Toes"))
                    return SemanticBodyPart.RightFoot;
                if (ContainsAny(name, "Leg", "Foot", "Toes")) return SemanticBodyPart.RightLeg;
                if (ContainsAny(name, "Shoulder", "Arm", "Hand", "Thumb", "Index", "Middle", "Ring", "Little"))
                    return SemanticBodyPart.RightArm;
            }
            return SemanticBodyPart.Torso;
        }

        private static SemanticBodyPart FromBoneName(string boneName, SmartBodySplitPreset preset)
        {
            var name = (boneName ?? string.Empty).ToLowerInvariant();
            if (ContainsAny(name, "head", "skull", "jaw", "eye")) return SemanticBodyPart.Head;
            bool left = ContainsAny(name, "left", "_l", ".l", " l ") || name.StartsWith("l_", StringComparison.Ordinal);
            bool right = ContainsAny(name, "right", "_r", ".r", " r ") || name.StartsWith("r_", StringComparison.Ordinal);
            bool hand = ContainsAny(name, "hand", "wrist", "finger", "thumb", "index", "middle", "ring", "little");
            bool foot = ContainsAny(name, "ankle", "foot", "toe");
            bool leg = ContainsAny(name, "thigh", "upleg", "leg", "calf", "knee", "shin") || foot;
            bool arm = ContainsAny(name, "shoulder", "arm", "elbow", "forearm") || hand;
            if (preset == SmartBodySplitPreset.HandsAndFeet && left && hand) return SemanticBodyPart.LeftHand;
            if (preset == SmartBodySplitPreset.HandsAndFeet && right && hand) return SemanticBodyPart.RightHand;
            if (preset == SmartBodySplitPreset.HandsAndFeet && left && foot) return SemanticBodyPart.LeftFoot;
            if (preset == SmartBodySplitPreset.HandsAndFeet && right && foot) return SemanticBodyPart.RightFoot;
            if (left && leg) return SemanticBodyPart.LeftLeg;
            if (right && leg) return SemanticBodyPart.RightLeg;
            if (left && arm) return SemanticBodyPart.LeftArm;
            if (right && arm) return SemanticBodyPart.RightArm;
            if (ContainsAny(name, "hips", "pelvis", "root", "spine", "chest", "neck", "torso"))
                return SemanticBodyPart.Torso;
            return SemanticBodyPart.Unassigned;
        }

        private static SemanticBodyPart ClassifyPosition(
            Vector3 point,
            Bounds bounds,
            SmartBodySplitPreset preset)
        {
            var size = bounds.size;
            float y = size.y > 1e-6f ? (point.y - bounds.min.y) / size.y : 0.5f;
            float x = point.x - bounds.center.x;
            if (y >= 0.78f) return SemanticBodyPart.Head;
            if (preset == SmartBodySplitPreset.HandsAndFeet && y <= 0.16f)
                return x < 0f ? SemanticBodyPart.LeftFoot : SemanticBodyPart.RightFoot;
            if (y <= 0.48f) return x < 0f ? SemanticBodyPart.LeftLeg : SemanticBodyPart.RightLeg;
            if (preset == SmartBodySplitPreset.HandsAndFeet
                && Mathf.Abs(x) >= Mathf.Max(size.x * 0.40f, 1e-5f))
                return x < 0f ? SemanticBodyPart.LeftHand : SemanticBodyPart.RightHand;
            if (Mathf.Abs(x) >= Mathf.Max(size.x * 0.22f, 1e-5f))
                return x < 0f ? SemanticBodyPart.LeftArm : SemanticBodyPart.RightArm;
            return SemanticBodyPart.Torso;
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
                if (value.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
