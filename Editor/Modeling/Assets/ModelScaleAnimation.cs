using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Assets
{
    internal static class ModelScaleAnimation
    {
        internal static IEnumerable<AnimationClip> Clips(GameObject root)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null)
                    foreach (var clip in animator.runtimeAnimatorController.animationClips)
                        if (clip != null) clips.Add(clip);
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
            {
                if (animation.clip != null) clips.Add(animation.clip);
                foreach (AnimationState state in animation) if (state.clip != null) clips.Add(state.clip);
            }
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue is AnimationClip clip)
                        clips.Add(clip);
            }
            return clips;
        }

        internal static void Validate(GameObject root)
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var baseController = animator.runtimeAnimatorController;
                while (baseController is AnimatorOverrideController overridden) baseController = overridden.runtimeAnimatorController;
                if (baseController != null && !EditorUtility.IsPersistent(baseController))
                    throw ModelScaleService.Error("MODEL_TRANSIENT_CONTROLLER", "Save the base AnimatorController as an asset before exporting a normalized prefab. Transient overrides over a saved base are supported.");
                if (animator.gameObject != root)
                    throw ModelScaleService.Error("MODEL_NESTED_ANIMATOR", "Select the Animator's own complete model root; nested independent animators need separate normalization.");
                if (!animator.hasTransformHierarchy)
                    throw ModelScaleService.Error("MODEL_OPTIMIZED_RIG", "Expose the optimized rig hierarchy before normalizing this model.");
                var avatar = animator.avatar;
                if (avatar == null) continue;
                if (!avatar.isValid)
                    throw ModelScaleService.Error("MODEL_AVATAR_INVALID", "Repair the invalid Avatar before normalization.");
                if (avatar.isHuman)
                {
                    var description = avatar.humanDescription;
                    if (description.human == null || description.human.Length == 0 || description.skeleton == null || description.skeleton.Length == 0)
                        throw ModelScaleService.Error("MODEL_AVATAR_DESCRIPTION_MISSING", "Reimport this Avatar to expose its HumanDescription.");
                    var names = root.GetComponentsInChildren<Transform>(true).GroupBy(value => value.name).ToDictionary(group => group.Key, group => group.Count());
                    foreach (var bone in description.human)
                        if (!names.TryGetValue(bone.boneName, out var count) || count != 1)
                            throw ModelScaleService.Error("MODEL_HUMAN_BONE_AMBIGUOUS", "The Humanoid mapping requires exactly one transform named '" + bone.boneName + "'.");
                }
                else if (animator.applyRootMotion && !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(avatar)) is ModelImporter))
                    throw ModelScaleService.Error("MODEL_GENERIC_MOTION_NODE_UNKNOWN", "The generic Avatar has no importer motion-node metadata. Disable root motion or normalize the imported model with its motion node available.");
            }
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
                if (animation.gameObject != root)
                    throw ModelScaleService.Error("MODEL_NESTED_ANIMATION", "Select the legacy Animation component's model root.");

            foreach (var clip in Clips(root))
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type == typeof(Transform) && string.IsNullOrEmpty(binding.path)
                        && binding.propertyName.StartsWith("m_LocalScale", StringComparison.Ordinal))
                        throw ModelScaleService.Error("MODEL_ANIMATED_ROOT_SCALE", "Clip '" + clip.name + "' animates root scale; a permanent root scale of 1 cannot be preserved.");
                    if (binding.type == typeof(Transform) && string.IsNullOrEmpty(binding.path)
                        && binding.propertyName.StartsWith("m_LocalPosition", StringComparison.Ordinal))
                        throw ModelScaleService.Error("MODEL_ANIMATED_ROOT_POSITION", "Clip '" + clip.name + "' directly animates scene-root position; use an explicit motion root below the selected root.");
                    if (typeof(Collider).IsAssignableFrom(binding.type) || binding.type == typeof(LODGroup))
                        throw ModelScaleService.Error("MODEL_ANIMATED_DIMENSION_UNSUPPORTED", "Clip '" + clip.name + "' animates collider/LOD dimensions.");
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    if (binding.type == typeof(MeshFilter) || binding.type == typeof(SkinnedMeshRenderer)
                        || binding.type == typeof(MeshCollider) || binding.type == typeof(Animator))
                        throw ModelScaleService.Error("MODEL_ANIMATED_ASSET_SWAP", "Clip '" + clip.name + "' swaps geometry or rig assets; its alternatives require separate normalization.");
            }
        }

        internal static void Scale(GameObject source, GameObject copy, float factor,
            List<Object> assets, Dictionary<Object, Object> remap)
        {
            foreach (var clip in Clips(source)) CopyClip(clip, factor, assets, remap);
            foreach (var sourceAnimator in source.GetComponentsInChildren<Animator>(true))
            {
                var animator = (Animator)remap[sourceAnimator];
                var sourceAvatar = sourceAnimator.avatar;
                if (sourceAvatar != null)
                {
                    Avatar avatar;
                    if (sourceAvatar.isHuman)
                    {
                        var description = sourceAvatar.humanDescription;
                        var skeleton = (SkeletonBone[])description.skeleton.Clone();
                        for (int i = 0; i < skeleton.Length; i++)
                        {
                            var bone = skeleton[i];
                            // Unity's Avatar description retains the original
                            // root name after a scene instance is renamed.
                            if (i == 0)
                            {
                                bone.name = copy.name;
                                // Scene placement is not part of Avatar rest
                                // coordinates. Baking it into HumanDescription
                                // changes muscle retargeting on rotated copies.
                                bone.position = Vector3.zero;
                                bone.rotation = Quaternion.identity;
                                bone.scale = Vector3.one;
                            }
                            else bone.position *= factor;
                            skeleton[i] = bone;
                        }
                        description.skeleton = skeleton;
                        var placement = copy.transform.localPosition;
                        var rotation = copy.transform.localRotation;
                        try
                        {
                            copy.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                            avatar = AvatarBuilder.BuildHumanAvatar(copy, description);
                        }
                        finally { copy.transform.SetLocalPositionAndRotation(placement, rotation); }
                        if (avatar == null || !avatar.isValid || !avatar.isHuman)
                        {
                            if (avatar != null) Object.DestroyImmediate(avatar);
                            throw ModelScaleService.Error("MODEL_AVATAR_REBUILD_FAILED", "The scaled Humanoid description did not produce a valid Avatar.");
                        }
                    }
                    else
                    {
                        var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sourceAvatar)) as ModelImporter;
                        avatar = AvatarBuilder.BuildGenericAvatar(copy, importer?.motionNodeName ?? string.Empty);
                        if (avatar == null || !avatar.isValid)
                        {
                            if (avatar != null) Object.DestroyImmediate(avatar);
                            throw ModelScaleService.Error("MODEL_AVATAR_REBUILD_FAILED", "The scaled generic hierarchy did not produce a valid Avatar.");
                        }
                    }
                    avatar.name = sourceAvatar.name + "_Normalized";
                    assets.Add(avatar);
                    remap[sourceAvatar] = avatar;
                    animator.avatar = avatar;
                }
                if (sourceAnimator.runtimeAnimatorController != null)
                {
                    var sourceController = sourceAnimator.runtimeAnimatorController;
                    var controller = new AnimatorOverrideController(sourceController) { name = sourceController.name + "_Normalized" };
                    var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    if (sourceController is AnimatorOverrideController originalOverride) originalOverride.GetOverrides(overrides);
                    else controller.GetOverrides(overrides);
                    for (int i = 0; i < overrides.Count; i++)
                    {
                        var pair = overrides[i];
                        var effective = pair.Value != null ? pair.Value : pair.Key;
                        overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(pair.Key, CopyClip(effective, factor, assets, remap));
                    }
                    controller.ApplyOverrides(overrides);
                    assets.Add(controller);
                    remap[sourceController] = controller;
                    animator.runtimeAnimatorController = controller;
                }
            }
            foreach (var animation in source.GetComponentsInChildren<Animation>(true))
            {
                var destination = (Animation)remap[animation];
                var states = animation.Cast<AnimationState>().ToArray();
                foreach (var state in states)
                {
                    destination.RemoveClip(state.name);
                    var suppliedClip = CopyClip(state.clip, factor, assets, remap);
                    destination.AddClip(suppliedClip, state.name);
                    var outputState = destination[state.name];
                    // AddClip may create a separate native clip for the alias.
                    // Persist the clip the AnimationState actually plays.
                    if (outputState.clip != suppliedClip)
                    {
                        assets.Add(outputState.clip);
                        remap[suppliedClip] = outputState.clip;
                        remap[state.clip] = outputState.clip;
                    }
                    outputState.speed = state.speed;
                    outputState.weight = state.weight;
                    outputState.layer = state.layer;
                    outputState.blendMode = state.blendMode;
                    outputState.wrapMode = state.wrapMode;
                    outputState.time = state.time;
                    outputState.enabled = state.enabled;
                }
                destination.clip = animation.clip != null ? CopyClip(animation.clip, factor, assets, remap) : null;
            }
            // Avatar/controller/other clip copies may not exist when the first
            // clip is created. Remap events and object curves after all assets.
            foreach (var clip in assets.OfType<AnimationClip>()) RemapClipReferences(clip, remap);
        }

        private static AnimationClip CopyClip(AnimationClip source, float factor,
            List<Object> assets, Dictionary<Object, Object> remap)
        {
            if (source == null) return null;
            if (remap.TryGetValue(source, out var already)) return (AnimationClip)already;
            var copy = Object.Instantiate(source);
            copy.name = source.name + "_Normalized";
            assets.Add(copy);
            remap[source] = copy;
            foreach (var binding in AnimationUtility.GetCurveBindings(source))
            {
                bool position = binding.type == typeof(Transform) && binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal);
                // Humanoid RootT/MotionT belongs to normalized muscle space;
                // the rebuilt Avatar supplies its new physical humanScale.
                bool genericMotion = !source.humanMotion && binding.type == typeof(Animator)
                    && (binding.propertyName.StartsWith("RootT.", StringComparison.Ordinal)
                        || binding.propertyName.StartsWith("MotionT.", StringComparison.Ordinal));
                if (!position && !genericMotion) continue;
                var curve = AnimationUtility.GetEditorCurve(copy, binding);
                var keys = curve.keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    var key = keys[i];
                    key.value *= factor;
                    if (ModelScaleService.Finite(key.inTangent)) key.inTangent *= factor;
                    if (ModelScaleService.Finite(key.outTangent)) key.outTangent *= factor;
                    keys[i] = key;
                }
                curve.keys = keys;
                AnimationUtility.SetEditorCurve(copy, binding, curve);
            }
            RemapClipReferences(copy, remap);
            return copy;
        }

        private static void RemapClipReferences(AnimationClip copy, IReadOnlyDictionary<Object, Object> remap)
        {
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(copy))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(copy, binding);
                for (int i = 0; i < keys.Length; i++)
                    if (keys[i].value != null && remap.TryGetValue(keys[i].value, out var replacement)) keys[i].value = replacement;
                AnimationUtility.SetObjectReferenceCurve(copy, binding, keys);
            }
            var events = AnimationUtility.GetAnimationEvents(copy);
            foreach (var item in events)
                if (item.objectReferenceParameter != null && remap.TryGetValue(item.objectReferenceParameter, out var replacement))
                    item.objectReferenceParameter = replacement;
            AnimationUtility.SetAnimationEvents(copy, events);
        }
    }
}
