using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Modeling;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    public enum AnimationClipTransformOperation { Duplicate, Trim, ScaleTime, Reverse, Mirror, LoopClosure, ExtractRootMotion, RemoveRootMotion, AdditiveReference, MergeSequential, CopyRemapCurves }

    [Serializable]
    public sealed class AnimationClipTransformSettings
    {
        public AnimationClipTransformOperation Operation;
        public float StartTime;
        public float EndTime = 1f;
        public float TimeScale = 1f;
        public AnimationClip ReferenceClip;
        public List<AnimationClip> AdditionalClips = new List<AnimationClip>();
        public string SourcePathPrefix = string.Empty;
        public string DestinationPathPrefix = string.Empty;
        public string OutputFolder = "Assets/AssetModeler/Rigging/Clips";
    }

    public static class RigPoseLibraryService
    {
        public static RigPoseEntry Capture(GameObject selected, string name)
        {
            var context = RigTargetResolver.Resolve(selected);
            if (context.Root == null) throw new InvalidOperationException("RIG_TARGET_INVALID");
            var entry = new RigPoseEntry
            {
                Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(name) ? "Pose" : name.Trim(),
                RigType = context.RigType, HierarchyHash = context.HierarchyHash, CreatedUtc = DateTime.UtcNow.ToString("O"),
            };
            var transforms = context.Root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                var value = transforms[i];
                entry.Transforms.Add(new RigTransformPose
                {
                    Path = RigTargetResolver.RelativePath(value, context.Root.transform), LocalPosition = value.localPosition,
                    LocalRotation = value.localRotation, LocalScale = value.localScale,
                });
            }
            if (context.Animator != null && context.Animator.avatar != null && context.Animator.avatar.isHuman)
            {
                var human = new HumanPose();
                using (var handler = new HumanPoseHandler(context.Animator.avatar, context.Animator.transform))
                {
                    handler.GetHumanPose(ref human);
                    entry.HumanBodyPosition = human.bodyPosition; entry.HumanBodyRotation = human.bodyRotation;
                    entry.HumanMuscles = human.muscles != null ? (float[])human.muscles.Clone() : Array.Empty<float>();
                }
            }
            return entry;
        }

        public static void Add(RigPoseLibrary library, RigPoseEntry entry)
        {
            if (library == null || entry == null) throw new ArgumentNullException();
            Undo.RecordObject(library, "Capture rig pose"); library.Add(entry); EditorUtility.SetDirty(library);
        }

        public static bool Rename(RigPoseLibrary library, string id, string name)
        {
            if (library == null) throw new ArgumentNullException(nameof(library));
            Undo.RecordObject(library, "Rename rig pose"); bool changed = library.Rename(id, name); if (changed) EditorUtility.SetDirty(library); return changed;
        }

        public static RigPoseEntry Duplicate(RigPoseLibrary library, string id, string name = null)
        {
            if (library == null) throw new ArgumentNullException(nameof(library));
            Undo.RecordObject(library, "Duplicate rig pose"); var copy = library.Duplicate(id, name); if (copy != null) EditorUtility.SetDirty(library); return copy;
        }

        public static void Apply(GameObject selected, RigPoseEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var context = RigTargetResolver.Resolve(selected);
            if (context.Root == null) throw new InvalidOperationException("RIG_TARGET_INVALID");
            if (!string.Equals(context.HierarchyHash, entry.HierarchyHash, StringComparison.Ordinal))
                throw new InvalidOperationException("POSE_HIERARCHY_CHANGED: the pose belongs to a different hierarchy revision.");
            var transforms = context.Root.GetComponentsInChildren<Transform>(true);
            Undo.RecordObjects(transforms.Cast<UnityEngine.Object>().ToArray(), "Apply rig pose");
            if (entry.HumanMuscles != null && entry.HumanMuscles.Length > 0 && context.Animator != null && context.Animator.avatar != null && context.Animator.avatar.isHuman)
            {
                var human = new HumanPose { bodyPosition = entry.HumanBodyPosition, bodyRotation = entry.HumanBodyRotation, muscles = (float[])entry.HumanMuscles.Clone() };
                using (var handler = new HumanPoseHandler(context.Animator.avatar, context.Animator.transform)) handler.SetHumanPose(ref human);
            }
            for (int i = 0; i < entry.Transforms.Count; i++)
            {
                var pose = entry.Transforms[i];
                var transform = string.IsNullOrEmpty(pose.Path) ? context.Root.transform : context.Root.transform.Find(pose.Path);
                if (transform == null) throw new InvalidOperationException("POSE_BONE_MISSING: " + pose.Path);
                transform.localPosition = pose.LocalPosition; transform.localRotation = pose.LocalRotation; transform.localScale = pose.LocalScale;
            }
        }

        public static RigPoseLibrary CreateLibrary(string outputFolder, string name = "RigPoseLibrary")
        {
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(outputFolder) ? "Assets/AssetModeler/Rigging/Poses" : outputFolder);
            var library = ScriptableObject.CreateInstance<RigPoseLibrary>(); library.name = name;
            RigAssetSafety.CreateAssetWithUndo(library, AssetModelerAssetPaths.UniqueAssetPath(folder, name, ".asset"), "Create rig pose library");
            return library;
        }
    }

    public static class AnimationClipStudioService
    {
        public static AnimationClip Transform(AnimationClip source, AnimationClipTransformSettings settings)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            settings = settings ?? new AnimationClipTransformSettings();
            var output = new AnimationClip { name = source.name + "_" + settings.Operation, frameRate = source.frameRate, wrapMode = source.wrapMode, legacy = source.legacy };
            float start = settings.Operation == AnimationClipTransformOperation.Trim ? Mathf.Clamp(settings.StartTime, 0f, source.length) : 0f;
            float end = settings.Operation == AnimationClipTransformOperation.Trim ? Mathf.Clamp(settings.EndTime, start, source.length) : source.length;
            float scale = settings.Operation == AnimationClipTransformOperation.ScaleTime ? Mathf.Max(1e-4f, settings.TimeScale) : 1f;
            var sources = settings.Operation == AnimationClipTransformOperation.MergeSequential
                ? new[] { source }.Concat(settings.AdditionalClips ?? new List<AnimationClip>()).Where(value => value != null).ToArray()
                : new[] { source };
            float timeOffset = 0f;
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                var currentSource = sources[sourceIndex];
                var bindings = AnimationUtility.GetCurveBindings(currentSource);
                for (int i = 0; i < bindings.Length; i++)
                {
                    var sourceBinding = bindings[i]; bool rootMotion = IsRootMotion(sourceBinding);
                    if (settings.Operation == AnimationClipTransformOperation.RemoveRootMotion && rootMotion) continue;
                    if (settings.Operation == AnimationClipTransformOperation.ExtractRootMotion && !rootMotion) continue;
                    var destinationBinding = TransformBinding(sourceBinding, settings);
                    var curve = AnimationUtility.GetEditorCurve(currentSource, sourceBinding);
                    var transformed = TransformCurve(curve, settings.Operation, start, end, scale, destinationBinding, settings.ReferenceClip, sourceBinding);
                    if (settings.Operation == AnimationClipTransformOperation.MergeSequential) transformed = OffsetCurve(transformed, timeOffset);
                    AnimationUtility.SetEditorCurve(output, destinationBinding, MergeCurve(AnimationUtility.GetEditorCurve(output, destinationBinding), transformed));
                }
                var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(currentSource);
                for (int i = 0; i < objectBindings.Length; i++)
                {
                    var sourceBinding = objectBindings[i]; bool rootMotion = IsRootMotion(sourceBinding);
                    if (settings.Operation == AnimationClipTransformOperation.RemoveRootMotion && rootMotion) continue;
                    if (settings.Operation == AnimationClipTransformOperation.ExtractRootMotion && !rootMotion) continue;
                    var destinationBinding = TransformBinding(sourceBinding, settings);
                    var keys = TransformObjectKeys(AnimationUtility.GetObjectReferenceCurve(currentSource, sourceBinding), settings.Operation, start, end, scale);
                    if (settings.Operation == AnimationClipTransformOperation.MergeSequential) for (int key = 0; key < keys.Length; key++) keys[key].time += timeOffset;
                    var existing = AnimationUtility.GetObjectReferenceCurve(output, destinationBinding) ?? Array.Empty<ObjectReferenceKeyframe>();
                    AnimationUtility.SetObjectReferenceCurve(output, destinationBinding, MergeObjectKeys(existing, keys));
                }
                timeOffset += currentSource.length;
            }
            if (settings.Operation == AnimationClipTransformOperation.AdditiveReference && settings.ReferenceClip != null)
                ApplyQuaternionAdditive(output, source, settings.ReferenceClip);
            var events = new List<AnimationEvent>(); timeOffset = 0f;
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                var transformed = TransformEvents(AnimationUtility.GetAnimationEvents(sources[sourceIndex]), settings.Operation, start, end, scale);
                if (settings.Operation == AnimationClipTransformOperation.MergeSequential) for (int i = 0; i < transformed.Length; i++) transformed[i].time += timeOffset;
                events.AddRange(transformed); timeOffset += sources[sourceIndex].length;
            }
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(settings.OutputFolder) ? "Assets/AssetModeler/Rigging/Clips" : settings.OutputFolder);
            RigAssetSafety.CreateAssetWithUndo(output, AssetModelerAssetPaths.UniqueAssetPath(folder, output.name, ".anim"), "Create animation clip");
            AnimationUtility.SetAnimationEvents(output, events.OrderBy(value => value.time).ToArray());
            output.EnsureQuaternionContinuity(); EditorUtility.SetDirty(output); return output;
        }

        public static void AddTransformKey(AnimationClip clip, Transform root, Transform target, float time)
        {
            if (clip == null || root == null || target == null) throw new ArgumentNullException();
            string path = AssetDatabase.GetAssetPath(clip);
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !string.Equals(System.IO.Path.GetExtension(path), ".anim", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("EDITABLE_CLIP_REQUIRED: duplicate the imported clip to a standalone .anim first.");
            string transformPath = RigTargetResolver.RelativePath(target, root);
            Undo.RegisterCompleteObjectUndo(clip, "Add animation transform key");
            AddKey(clip, EditorCurveBinding.FloatCurve(transformPath, typeof(Transform), "m_LocalPosition.x"), time, target.localPosition.x);
            AddKey(clip, EditorCurveBinding.FloatCurve(transformPath, typeof(Transform), "m_LocalPosition.y"), time, target.localPosition.y);
            AddKey(clip, EditorCurveBinding.FloatCurve(transformPath, typeof(Transform), "m_LocalPosition.z"), time, target.localPosition.z);
            AddKey(clip, EditorCurveBinding.FloatCurve(transformPath, typeof(Transform), "m_LocalRotation.x"), time, target.localRotation.x);
            AddKey(clip, EditorCurveBinding.FloatCurve(transformPath, typeof(Transform), "m_LocalRotation.y"), time, target.localRotation.y);
            AddKey(clip, EditorCurveBinding.FloatCurve(transformPath, typeof(Transform), "m_LocalRotation.z"), time, target.localRotation.z);
            AddKey(clip, EditorCurveBinding.FloatCurve(transformPath, typeof(Transform), "m_LocalRotation.w"), time, target.localRotation.w);
            EditorUtility.SetDirty(clip);
        }

        private static AnimationCurve TransformCurve(AnimationCurve source, AnimationClipTransformOperation operation, float start, float end, float scale, EditorCurveBinding destinationBinding, AnimationClip referenceClip, EditorCurveBinding sourceBinding)
        {
            var keys = new List<Keyframe>();
            if (operation == AnimationClipTransformOperation.Trim)
            {
                keys.Add(BoundaryKey(source, start, 0f));
                foreach (var original in source.keys) if (original.time > start && original.time < end) { var key = original; key.time -= start; keys.Add(key); }
                keys.Add(BoundaryKey(source, end, end - start));
            }
            else
            {
                foreach (var original in source.keys)
                {
                    var key = original;
                    if (operation == AnimationClipTransformOperation.ScaleTime) key.time *= scale;
                    else if (operation == AnimationClipTransformOperation.Reverse)
                    {
                        key.time = source.keys.Last().time - key.time; float tangent = key.inTangent; key.inTangent = -key.outTangent; key.outTangent = -tangent;
                        float weight = key.inWeight; key.inWeight = key.outWeight; key.outWeight = weight;
                        if (key.weightedMode == WeightedMode.In) key.weightedMode = WeightedMode.Out; else if (key.weightedMode == WeightedMode.Out) key.weightedMode = WeightedMode.In;
                    }
                    else if (operation == AnimationClipTransformOperation.Mirror && MirrorSign(destinationBinding) < 0f) { key.value = -key.value; key.inTangent = -key.inTangent; key.outTangent = -key.outTangent; }
                    keys.Add(key);
                }
                if (operation == AnimationClipTransformOperation.LoopClosure && keys.Count > 1) { var key = keys[keys.Count - 1]; key.value = keys[0].value; keys[keys.Count - 1] = key; }
            }
            if (operation == AnimationClipTransformOperation.AdditiveReference && referenceClip != null && !IsQuaternionRotationBinding(sourceBinding))
            {
                var reference = AnimationUtility.GetEditorCurve(referenceClip, sourceBinding);
                if (reference != null) for (int i = 0; i < keys.Count; i++) { var key = keys[i]; key.value -= reference.Evaluate(Mathf.Min(key.time, referenceClip.length)); keys[i] = key; }
            }
            keys.Sort((left, right) => left.time.CompareTo(right.time));
            var curve = new AnimationCurve(keys.ToArray()) { preWrapMode = source.preWrapMode, postWrapMode = source.postWrapMode };
            return curve;
        }

        private static EditorCurveBinding TransformBinding(EditorCurveBinding binding, AnimationClipTransformSettings settings)
        {
            var result = binding;
            if (settings.Operation == AnimationClipTransformOperation.Mirror)
            {
                result.path = SwapSide(binding.path);
                result.propertyName = SwapHumanoidPropertySide(binding.propertyName);
            }
            if (settings.Operation == AnimationClipTransformOperation.CopyRemapCurves && !string.IsNullOrEmpty(settings.SourcePathPrefix) && binding.path.StartsWith(settings.SourcePathPrefix, StringComparison.Ordinal))
                result.path = settings.DestinationPathPrefix + binding.path.Substring(settings.SourcePathPrefix.Length);
            return result;
        }

        private static AnimationCurve OffsetCurve(AnimationCurve curve, float offset)
        {
            var keys = curve.keys; for (int i = 0; i < keys.Length; i++) keys[i].time += offset;
            return new AnimationCurve(keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
        }

        private static AnimationCurve MergeCurve(AnimationCurve existing, AnimationCurve addition)
        {
            if (existing == null) return addition;
            var ordered = existing.keys.Select(value => new KeyValuePair<Keyframe, bool>(value, false))
                .Concat(addition.keys.Select(value => new KeyValuePair<Keyframe, bool>(value, true)))
                .OrderBy(value => value.Key.time)
                .ThenBy(value => value.Value ? 1 : 0)
                .ToArray();
            var merged = new List<Keyframe>(ordered.Length);
            for (int i = 0; i < ordered.Length; i++)
            {
                var key = ordered[i].Key;
                if (merged.Count > 0 && Mathf.Abs(merged[merged.Count - 1].time - key.time) <= 1e-5f)
                {
                    // The first frame of the next clip owns a shared boundary. Unity's
                    // curve serializer can assert when two keys have the same time.
                    merged[merged.Count - 1] = key;
                }
                else merged.Add(key);
            }
            return new AnimationCurve(merged.ToArray()) { preWrapMode = existing.preWrapMode, postWrapMode = addition.postWrapMode };
        }

        private static ObjectReferenceKeyframe[] MergeObjectKeys(IEnumerable<ObjectReferenceKeyframe> existing, IEnumerable<ObjectReferenceKeyframe> addition)
        {
            var ordered = existing.Select(value => new KeyValuePair<ObjectReferenceKeyframe, bool>(value, false))
                .Concat(addition.Select(value => new KeyValuePair<ObjectReferenceKeyframe, bool>(value, true)))
                .OrderBy(value => value.Key.time)
                .ThenBy(value => value.Value ? 1 : 0)
                .ToArray();
            var merged = new List<ObjectReferenceKeyframe>(ordered.Length);
            for (int i = 0; i < ordered.Length; i++)
            {
                var key = ordered[i].Key;
                if (merged.Count > 0 && Mathf.Abs(merged[merged.Count - 1].time - key.time) <= 1e-5f) merged[merged.Count - 1] = key;
                else merged.Add(key);
            }
            return merged.ToArray();
        }

        private static Keyframe BoundaryKey(AnimationCurve curve, float sourceTime, float destinationTime)
        {
            float epsilon = Mathf.Max(1e-5f, Mathf.Min(0.001f, Mathf.Max(curve.length, 0.001f) * 0.001f));
            float before = curve.Evaluate(Mathf.Max(0f, sourceTime - epsilon)); float after = curve.Evaluate(sourceTime + epsilon);
            float tangent = (after - before) / (2f * epsilon);
            return new Keyframe(destinationTime, curve.Evaluate(sourceTime), tangent, tangent);
        }

        private static void AddKey(AnimationClip clip, EditorCurveBinding binding, float time, float value)
        {
            var curve = AnimationUtility.GetEditorCurve(clip, binding) ?? new AnimationCurve(); curve.AddKey(new Keyframe(Mathf.Max(0f, time), value)); AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static float MirrorSign(EditorCurveBinding binding)
        {
            string property = binding.propertyName ?? string.Empty;
            if (property.Equals("RootT.x", StringComparison.Ordinal)) return -1f;
            if (property.Equals("RootQ.y", StringComparison.Ordinal) || property.Equals("RootQ.z", StringComparison.Ordinal)) return -1f;
            if (binding.type == typeof(Animator) && property.IndexOf("Left-Right", StringComparison.OrdinalIgnoreCase) >= 0
                && property.IndexOf("Left ", StringComparison.OrdinalIgnoreCase) < 0 && property.IndexOf("Right ", StringComparison.OrdinalIgnoreCase) < 0) return -1f;
            if (property.EndsWith(".x", StringComparison.Ordinal) && property.IndexOf("Position", StringComparison.OrdinalIgnoreCase) >= 0) return -1f;
            if ((property.EndsWith(".y", StringComparison.Ordinal) || property.EndsWith(".z", StringComparison.Ordinal)) && property.IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) >= 0) return -1f;
            return 1f;
        }

        private static bool IsQuaternionRotationBinding(EditorCurveBinding binding)
        {
            string property = binding.propertyName ?? string.Empty;
            return (property.StartsWith("m_LocalRotation.", StringComparison.Ordinal) || property.StartsWith("RootQ.", StringComparison.Ordinal))
                && QuaternionComponent(property) >= 0;
        }

        private static void ApplyQuaternionAdditive(AnimationClip output, AnimationClip source, AnimationClip reference)
        {
            var groups = new Dictionary<string, EditorCurveBinding[]>();
            foreach (var binding in AnimationUtility.GetCurveBindings(source))
            {
                if (!IsQuaternionRotationBinding(binding)) continue;
                int component = QuaternionComponent(binding.propertyName);
                string prefix = binding.propertyName.Substring(0, binding.propertyName.Length - 2);
                string key = (binding.type != null ? binding.type.AssemblyQualifiedName : string.Empty) + "\u001f" + binding.path + "\u001f" + prefix;
                if (!groups.TryGetValue(key, out var bindings)) groups[key] = bindings = new EditorCurveBinding[4];
                bindings[component] = binding;
            }

            foreach (var pair in groups)
            {
                var bindings = pair.Value;
                if (bindings.Any(value => string.IsNullOrEmpty(value.propertyName))) continue;
                var sourceCurves = bindings.Select(value => AnimationUtility.GetEditorCurve(source, value)).ToArray();
                if (sourceCurves.Any(value => value == null)) continue;
                var referenceCurves = bindings.Select(value => AnimationUtility.GetEditorCurve(reference, value)).ToArray();
                var times = new SortedSet<float> { 0f, source.length };
                foreach (var curve in sourceCurves) foreach (var key in curve.keys) times.Add(Mathf.Clamp(key.time, 0f, source.length));
                foreach (var curve in referenceCurves) if (curve != null) foreach (var key in curve.keys) times.Add(Mathf.Clamp(key.time, 0f, source.length));
                var keys = new[] { new List<Keyframe>(), new List<Keyframe>(), new List<Keyframe>(), new List<Keyframe>() };
                Quaternion previous = Quaternion.identity; bool hasPrevious = false;
                foreach (float time in times)
                {
                    Quaternion sourceRotation = EvaluateQuaternion(sourceCurves, time, Quaternion.identity);
                    Quaternion referenceRotation = EvaluateQuaternion(referenceCurves, Mathf.Min(time, reference.length), Quaternion.identity);
                    Quaternion delta = Normalize(Quaternion.Inverse(referenceRotation) * sourceRotation);
                    if (hasPrevious && Quaternion.Dot(previous, delta) < 0f) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
                    previous = delta; hasPrevious = true;
                    keys[0].Add(new Keyframe(time, delta.x)); keys[1].Add(new Keyframe(time, delta.y));
                    keys[2].Add(new Keyframe(time, delta.z)); keys[3].Add(new Keyframe(time, delta.w));
                }
                for (int component = 0; component < 4; component++)
                {
                    var curve = new AnimationCurve(keys[component].ToArray());
                    for (int key = 0; key < curve.length; key++)
                    {
                        AnimationUtility.SetKeyLeftTangentMode(curve, key, AnimationUtility.TangentMode.Linear);
                        AnimationUtility.SetKeyRightTangentMode(curve, key, AnimationUtility.TangentMode.Linear);
                    }
                    AnimationUtility.SetEditorCurve(output, bindings[component], curve);
                }
            }
        }

        private static Quaternion EvaluateQuaternion(IReadOnlyList<AnimationCurve> curves, float time, Quaternion fallback)
        {
            if (curves == null || curves.Count < 4 || curves.Any(value => value == null)) return fallback;
            return Normalize(new Quaternion(curves[0].Evaluate(time), curves[1].Evaluate(time), curves[2].Evaluate(time), curves[3].Evaluate(time)));
        }

        private static Quaternion Normalize(Quaternion value)
        {
            float magnitude = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            return magnitude > 1e-8f ? new Quaternion(value.x / magnitude, value.y / magnitude, value.z / magnitude, value.w / magnitude) : Quaternion.identity;
        }

        private static int QuaternionComponent(string property)
        {
            if (string.IsNullOrEmpty(property) || property.Length < 2 || property[property.Length - 2] != '.') return -1;
            switch (property[property.Length - 1]) { case 'x': return 0; case 'y': return 1; case 'z': return 2; case 'w': return 3; default: return -1; }
        }

        private static string SwapSide(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            const string marker = "__RIG_SIDE__";
            return value.Replace("Left", marker).Replace("Right", "Left").Replace(marker, "Right")
                .Replace("left", marker).Replace("right", "left").Replace(marker, "right")
                .Replace("L_", marker).Replace("R_", "L_").Replace(marker, "R_")
                .Replace("_L", marker).Replace("_R", "_L").Replace(marker, "_R");
        }

        private static string SwapHumanoidPropertySide(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.StartsWith("Left", StringComparison.Ordinal)) return "Right" + value.Substring(4);
            if (value.StartsWith("Right", StringComparison.Ordinal)) return "Left" + value.Substring(5);
            return value;
        }

        private static ObjectReferenceKeyframe[] TransformObjectKeys(ObjectReferenceKeyframe[] source, AnimationClipTransformOperation operation, float start, float end, float scale)
        {
            var result = new List<ObjectReferenceKeyframe>(); float length = source.Length > 0 ? source.Max(key => key.time) : 0f;
            foreach (var original in source)
            {
                var key = original;
                if (operation == AnimationClipTransformOperation.Trim) { if (key.time < start || key.time > end) continue; key.time -= start; }
                else if (operation == AnimationClipTransformOperation.ScaleTime) key.time *= scale;
                else if (operation == AnimationClipTransformOperation.Reverse) key.time = length - key.time;
                result.Add(key);
            }
            return result.OrderBy(key => key.time).ToArray();
        }

        private static AnimationEvent[] TransformEvents(AnimationEvent[] source, AnimationClipTransformOperation operation, float start, float end, float scale)
        {
            var result = new List<AnimationEvent>(); float length = source.Length > 0 ? source.Max(value => value.time) : 0f;
            foreach (var original in source)
            {
                if (operation == AnimationClipTransformOperation.Trim && (original.time < start || original.time > end)) continue;
                var copy = new AnimationEvent { functionName = original.functionName, stringParameter = original.stringParameter, floatParameter = original.floatParameter, intParameter = original.intParameter, objectReferenceParameter = original.objectReferenceParameter, messageOptions = original.messageOptions };
                copy.time = operation == AnimationClipTransformOperation.Trim ? original.time - start
                    : operation == AnimationClipTransformOperation.ScaleTime ? original.time * scale
                    : operation == AnimationClipTransformOperation.Reverse ? length - original.time : original.time;
                result.Add(copy);
            }
            return result.OrderBy(value => value.time).ToArray();
        }

        private static bool IsRootMotion(EditorCurveBinding binding)
            => string.IsNullOrEmpty(binding.path) && (binding.propertyName.StartsWith("RootT", StringComparison.Ordinal)
                || binding.propertyName.StartsWith("RootQ", StringComparison.Ordinal)
                || binding.propertyName.StartsWith("m_LocalPosition", StringComparison.Ordinal)
                || binding.propertyName.StartsWith("m_LocalRotation", StringComparison.Ordinal));
    }
}
