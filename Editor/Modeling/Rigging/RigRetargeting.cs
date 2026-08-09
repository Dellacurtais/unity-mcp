using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Modeling;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    public sealed class RigRetargetAnalysisReport
    {
        public RigRetargetProfile Profile;
        public int SourceBoneCount;
        public int MappedBoneCount;
        public int UnmappedBoneCount;
        public float AverageConfidence;
        public readonly List<string> Warnings = new List<string>();
        public bool IsComplete => SourceBoneCount > 0 && UnmappedBoneCount == 0;
    }

    public sealed class RigRetargetMetrics
    {
        public float RootPositionError;
        public float HeadPositionError;
        public float LeftHandPositionError;
        public float RightHandPositionError;
        public float LeftFootPositionError;
        public float RightFootPositionError;
        public float MaximumRotationError;
    }

    public static class RigRetargetAnalyzer
    {
        public static RigRetargetAnalysisReport Analyze(GameObject source, GameObject destination, string outputFolder = null)
        {
            var sourceContext = RigTargetResolver.Resolve(source); var destinationContext = RigTargetResolver.Resolve(destination);
            if (!sourceContext.IsValid || !destinationContext.IsValid) throw new InvalidOperationException("RETARGET_RIG_INVALID: both characters need skinned rigs.");
            var profile = ScriptableObject.CreateInstance<RigRetargetProfile>(); profile.name = sourceContext.Root.name + "_to_" + destinationContext.Root.name;
            profile.SourceHierarchyHash = sourceContext.HierarchyHash; profile.DestinationHierarchyHash = destinationContext.HierarchyHash;
            profile.SourceStateHash = sourceContext.StateHash; profile.DestinationStateHash = destinationContext.StateHash;
            profile.SourceRigType = sourceContext.RigType; profile.DestinationRigType = destinationContext.RigType;
            profile.RootScale = Mathf.Max(0.0001f, BoundsHeight(destinationContext.Root.transform) / Mathf.Max(0.0001f, BoundsHeight(sourceContext.Root.transform)));
            if (sourceContext.Animator != null && destinationContext.Animator != null
                && sourceContext.Animator.avatar != null && sourceContext.Animator.avatar.isHuman
                && destinationContext.Animator.avatar != null && destinationContext.Animator.avatar.isHuman)
                AnalyzeHumanoid(sourceContext, destinationContext, profile);
            else AnalyzeGeneric(sourceContext, destinationContext, profile);
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(outputFolder) ? "Assets/AssetModeler/Rigging/Retarget" : outputFolder);
            RigAssetSafety.CreateAssetWithUndo(profile, AssetModelerAssetPaths.UniqueAssetPath(folder, profile.name, ".asset"), "Create retarget profile");
            return Report(profile, profile.Bones.Count);
        }

        public static RigRetargetAnalysisReport Report(RigRetargetProfile profile, int sourceBoneCount)
        {
            int mapped = profile.Bones.Count(value => value.Enabled && !string.IsNullOrEmpty(value.DestinationPath));
            return new RigRetargetAnalysisReport { Profile = profile, SourceBoneCount = sourceBoneCount, MappedBoneCount = mapped, UnmappedBoneCount = Mathf.Max(0, sourceBoneCount - mapped), AverageConfidence = mapped > 0 ? profile.Bones.Where(value => value.Enabled).Average(value => value.Confidence) : 0f };
        }

        private static void AnalyzeHumanoid(RigTargetContext source, RigTargetContext destination, RigRetargetProfile profile)
        {
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var human = (HumanBodyBones)i; var sourceBone = source.Animator.GetBoneTransform(human); if (sourceBone == null) continue;
                var destinationBone = destination.Animator.GetBoneTransform(human);
                profile.Bones.Add(Map(source.Root.transform, destination.Root.transform, sourceBone, destinationBone, destinationBone != null ? 1f : 0f, human.ToString()));
            }
        }

        private static void AnalyzeGeneric(RigTargetContext source, RigTargetContext destination, RigRetargetProfile profile)
        {
            var used = new HashSet<int>();
            for (int i = 0; i < source.Bones.Length; i++)
            {
                var sourceBone = source.Bones[i]; Transform best = null; float bestScore = 0f;
                for (int j = 0; j < destination.Bones.Length; j++)
                {
                    var candidate = destination.Bones[j]; if (candidate == null || used.Contains(candidate.GetInstanceID())) continue;
                    float score = Score(sourceBone, candidate, source.Root.transform, destination.Root.transform);
                    if (score > bestScore) { best = candidate; bestScore = score; }
                }
                if (bestScore < 0.35f) best = null; else used.Add(best.GetInstanceID());
                profile.Bones.Add(Map(source.Root.transform, destination.Root.transform, sourceBone, best, bestScore, string.Empty));
            }
        }

        private static RigRetargetBoneMap Map(Transform sourceRoot, Transform destinationRoot, Transform source, Transform destination, float confidence, string human)
            => new RigRetargetBoneMap
            {
                SourcePath = RigTargetResolver.RelativePath(source, sourceRoot), DestinationPath = destination != null ? RigTargetResolver.RelativePath(destination, destinationRoot) : string.Empty,
                HumanBone = human, RotationOffset = destination != null ? destination.localRotation * Quaternion.Inverse(source.localRotation) : Quaternion.identity,
                Confidence = Mathf.Clamp01(confidence), Enabled = destination != null, Reviewed = destination != null && confidence >= 0.9f,
                Warning = destination == null ? "Destination bone missing" : confidence < 0.9f ? "Review this automatic mapping" : string.Empty,
            };

        private static float Score(Transform source, Transform destination, Transform sourceRoot, Transform destinationRoot)
        {
            string a = Normalize(source.name), b = Normalize(destination.name); float score = a == b ? 0.8f : TokenSimilarity(a, b) * 0.55f;
            if (Side(source.name) == Side(destination.name)) score += 0.1f; else if (Side(source.name) != 0 && Side(destination.name) != 0) score -= 0.3f;
            float sourceHeight = Mathf.Max(0.001f, BoundsHeight(sourceRoot)); float destinationHeight = Mathf.Max(0.001f, BoundsHeight(destinationRoot));
            var sourcePosition = sourceRoot.InverseTransformPoint(source.position) / sourceHeight; var destinationPosition = destinationRoot.InverseTransformPoint(destination.position) / destinationHeight;
            score += Mathf.Clamp01(1f - Vector3.Distance(sourcePosition, destinationPosition)) * 0.2f;
            return Mathf.Clamp01(score);
        }

        private static float BoundsHeight(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true); if (renderers.Length == 0) return 1f;
            var bounds = renderers[0].bounds; for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds); return bounds.size.y;
        }
        private static int Side(string value) { string lower = (value ?? string.Empty).ToLowerInvariant(); if (lower.Contains("left") || lower.StartsWith("l_") || lower.EndsWith("_l")) return -1; if (lower.Contains("right") || lower.StartsWith("r_") || lower.EndsWith("_r")) return 1; return 0; }
        private static string Normalize(string value) => new string((value ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace("left", string.Empty).Replace("right", string.Empty);
        private static float TokenSimilarity(string a, string b) { if (a == b) return 1f; if (a.Contains(b) || b.Contains(a)) return 0.8f; int same = a.Intersect(b).Count(); return same / (float)Mathf.Max(1, Mathf.Max(a.Length, b.Length)); }
    }

    public static class RigRetargetService
    {
        public static void ApplyPose(GameObject source, GameObject destination, RigRetargetProfile profile)
        {
            Validate(source, destination, profile, out var sourceContext, out var destinationContext);
            var transforms = destinationContext.Root.GetComponentsInChildren<Transform>(true);
            Undo.RecordObjects(transforms.Cast<UnityEngine.Object>().ToArray(), "Apply retarget pose");
            ApplyPoseInternal(sourceContext.Root.transform, destinationContext.Root.transform, profile);
        }

        public static RigRetargetMetrics Measure(GameObject source, GameObject destination, RigRetargetProfile profile)
        {
            Validate(source, destination, profile, out var sourceContext, out var destinationContext);
            var result = new RigRetargetMetrics();
            result.RootPositionError = Vector3.Distance(sourceContext.Root.transform.position, destinationContext.Root.transform.position);
            foreach (var map in profile.Bones.Where(value => value.Enabled && !string.IsNullOrEmpty(value.DestinationPath)))
            {
                var sourceBone = string.IsNullOrEmpty(map.SourcePath) ? sourceContext.Root.transform : sourceContext.Root.transform.Find(map.SourcePath);
                var destinationBone = string.IsNullOrEmpty(map.DestinationPath) ? destinationContext.Root.transform : destinationContext.Root.transform.Find(map.DestinationPath);
                if (sourceBone == null || destinationBone == null) continue;
                float position = Vector3.Distance(sourceBone.position, destinationBone.position);
                float rotation = Quaternion.Angle(map.RotationOffset * sourceBone.localRotation, destinationBone.localRotation); result.MaximumRotationError = Mathf.Max(result.MaximumRotationError, rotation);
                if (map.HumanBone == HumanBodyBones.Head.ToString()) result.HeadPositionError = position;
                else if (map.HumanBone == HumanBodyBones.LeftHand.ToString()) result.LeftHandPositionError = position;
                else if (map.HumanBone == HumanBodyBones.RightHand.ToString()) result.RightHandPositionError = position;
                else if (map.HumanBone == HumanBodyBones.LeftFoot.ToString()) result.LeftFootPositionError = position;
                else if (map.HumanBone == HumanBodyBones.RightFoot.ToString()) result.RightFootPositionError = position;
            }
            return result;
        }

        public static AnimationClip[] RetargetClips(GameObject source, GameObject destination, RigRetargetProfile profile, IReadOnlyList<AnimationClip> clips, float frameRate = 30f, string outputFolder = null)
        {
            if (clips == null) throw new ArgumentNullException(nameof(clips));
            var output = new List<AnimationClip>();
            for (int i = 0; i < clips.Count; i++) if (clips[i] != null) output.Add(RetargetClip(source, destination, profile, clips[i], frameRate, outputFolder));
            return output.ToArray();
        }

        public static AnimationClip RetargetClip(GameObject source, GameObject destination, RigRetargetProfile profile, AnimationClip clip, float frameRate = 30f, string outputFolder = null)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            Validate(source, destination, profile, out var sourceContext, out var destinationContext);
            frameRate = Mathf.Clamp(frameRate, 1f, 120f); int samples = Mathf.Max(2, Mathf.CeilToInt(clip.length * frameRate) + 1);
            var tracks = profile.Bones.Where(value => value.Enabled && !string.IsNullOrEmpty(value.DestinationPath)).ToDictionary(value => value, value => new TransformTrack());
            var sourcePose = RigPoseSnapshot.Capture(sourceContext.Root); var destinationPose = RigPoseSnapshot.Capture(destinationContext.Root);
            bool started = !AnimationMode.InAnimationMode(); if (started) AnimationMode.StartAnimationMode();
            try
            {
                for (int sample = 0; sample < samples; sample++)
                {
                    float time = clip.length * sample / (samples - 1f); sourcePose.Restore(); destinationPose.Restore();
                    AnimationMode.BeginSampling(); try { AnimationMode.SampleAnimationClip(sourceContext.Root, clip, time); } finally { AnimationMode.EndSampling(); }
                    ApplyPoseInternal(sourceContext.Root.transform, destinationContext.Root.transform, profile);
                    foreach (var pair in tracks)
                    {
                        var transform = destinationContext.Root.transform.Find(pair.Key.DestinationPath); pair.Value.Add(time, transform.localPosition, transform.localRotation);
                    }
                }
            }
            finally { sourcePose.Restore(); destinationPose.Restore(); if (started && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode(); }
            var output = new AnimationClip { name = clip.name + "_Retarget", frameRate = frameRate };
            foreach (var pair in tracks) pair.Value.Write(output, pair.Key.DestinationPath);
            output.EnsureQuaternionContinuity();
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(outputFolder) ? "Assets/AssetModeler/Rigging/RetargetClips" : outputFolder);
            RigAssetSafety.CreateAssetWithUndo(output, AssetModelerAssetPaths.UniqueAssetPath(folder, output.name, ".anim"), "Create retarget clip"); return output;
        }

        private static void Validate(GameObject source, GameObject destination, RigRetargetProfile profile, out RigTargetContext sourceContext, out RigTargetContext destinationContext)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile)); sourceContext = RigTargetResolver.Resolve(source); destinationContext = RigTargetResolver.Resolve(destination);
            if (sourceContext.HierarchyHash != profile.SourceHierarchyHash || destinationContext.HierarchyHash != profile.DestinationHierarchyHash
                || (!string.IsNullOrEmpty(profile.SourceStateHash) && sourceContext.StateHash != profile.SourceStateHash)
                || (!string.IsNullOrEmpty(profile.DestinationStateHash) && destinationContext.StateHash != profile.DestinationStateHash))
                throw new InvalidOperationException("RETARGET_PROFILE_STALE: source or destination rig state changed.");
            var unmapped = profile.Bones.Where(value => value.Confidence <= 0f || string.IsNullOrEmpty(value.DestinationPath) || !value.Enabled || !value.Reviewed).ToArray();
            if (unmapped.Length > 0) throw new InvalidOperationException("RETARGET_UNMAPPED_BONES: " + unmapped.Length);
        }

        private static void ApplyPoseInternal(Transform sourceRoot, Transform destinationRoot, RigRetargetProfile profile)
        {
            foreach (var map in profile.Bones)
            {
                if (!map.Enabled || string.IsNullOrEmpty(map.DestinationPath)) continue;
                var source = string.IsNullOrEmpty(map.SourcePath) ? sourceRoot : sourceRoot.Find(map.SourcePath); var destination = string.IsNullOrEmpty(map.DestinationPath) ? destinationRoot : destinationRoot.Find(map.DestinationPath);
                if (source == null || destination == null) throw new InvalidOperationException("RETARGET_BONE_MISSING: " + map.SourcePath + " -> " + map.DestinationPath);
                destination.localRotation = map.RotationOffset * source.localRotation;
                if (profile.TransferRootMotion && (destination == destinationRoot || map.HumanBone == HumanBodyBones.Hips.ToString())) destination.localPosition = source.localPosition * profile.RootScale;
            }
        }

        private sealed class TransformTrack
        {
            private readonly List<Keyframe> px = new List<Keyframe>(), py = new List<Keyframe>(), pz = new List<Keyframe>();
            private readonly List<Keyframe> rx = new List<Keyframe>(), ry = new List<Keyframe>(), rz = new List<Keyframe>(), rw = new List<Keyframe>();
            public void Add(float time, Vector3 p, Quaternion r) { px.Add(new Keyframe(time, p.x)); py.Add(new Keyframe(time, p.y)); pz.Add(new Keyframe(time, p.z)); rx.Add(new Keyframe(time, r.x)); ry.Add(new Keyframe(time, r.y)); rz.Add(new Keyframe(time, r.z)); rw.Add(new Keyframe(time, r.w)); }
            public void Write(AnimationClip clip, string path)
            {
                clip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", new AnimationCurve(px.ToArray())); clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", new AnimationCurve(py.ToArray())); clip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", new AnimationCurve(pz.ToArray()));
                clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", new AnimationCurve(rx.ToArray())); clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", new AnimationCurve(ry.ToArray())); clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", new AnimationCurve(rz.ToArray())); clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", new AnimationCurve(rw.ToArray()));
            }
        }
    }
}
