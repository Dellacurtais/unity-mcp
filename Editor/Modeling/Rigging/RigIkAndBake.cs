using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Modeling;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    public static class NativeRigIkSolver
    {
        public static void Solve(RigIkBinding binding, bool resetToRestPose = true)
        {
            if (binding == null || binding.Root == null || binding.Target == null) throw new InvalidOperationException("IK_BINDING_INCOMPLETE");
            var chain = ResolveChain(binding);
            if (resetToRestPose) RestoreRestPose(binding, chain);
            switch (binding.Solver)
            {
                case RigIkSolverType.TwoBone: SolveTwoBone(binding.Root, binding.Mid, binding.Tip, binding.Target.position, binding.Hint != null ? binding.Hint.position : binding.Root.position + binding.Root.up, binding.Weight); break;
                case RigIkSolverType.Aim: SolveAim(binding.Root, binding.Target.position, binding.Weight); break;
                case RigIkSolverType.Fabrik: SolveFabrik(chain, binding.Target.position, binding.Iterations, binding.Weight); break;
            }
        }

        public static void CaptureRestPose(RigIkBinding binding)
        {
            if (binding == null) return; var chain = ResolveChain(binding); binding.RestLocalRotations.Clear();
            for (int i = 0; i < chain.Length; i++) binding.RestLocalRotations.Add(chain[i] != null ? chain[i].localRotation : Quaternion.identity);
            EditorUtility.SetDirty(binding);
        }

        private static Transform[] ResolveChain(RigIkBinding binding)
        {
            var chain = binding.Chain != null ? binding.Chain.Where(value => value != null).ToArray() : Array.Empty<Transform>();
            return chain.Length >= 2 ? chain : new[] { binding.Root, binding.Mid, binding.Tip }.Where(value => value != null).ToArray();
        }

        private static void RestoreRestPose(RigIkBinding binding, IReadOnlyList<Transform> chain)
        {
            if (binding.RestLocalRotations == null || binding.RestLocalRotations.Count != chain.Count) { CaptureRestPose(binding); return; }
            for (int i = 0; i < chain.Count; i++) chain[i].localRotation = binding.RestLocalRotations[i];
        }

        public static void SolveTwoBone(Transform root, Transform mid, Transform tip, Vector3 target, Vector3 hint, float weight = 1f)
        {
            if (root == null || mid == null || tip == null) throw new ArgumentNullException(); weight = Mathf.Clamp01(weight);
            var originalRoot = root.rotation; var originalMid = mid.rotation;
            Vector3 a = root.position, b = mid.position, c = tip.position; float l1 = Vector3.Distance(a, b), l2 = Vector3.Distance(b, c);
            if (l1 <= 1e-6f || l2 <= 1e-6f) throw new InvalidOperationException("IK_ZERO_LENGTH_CHAIN");
            Vector3 toTarget = target - a; float distance = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(l1 - l2) + 1e-5f, l1 + l2 - 1e-5f); Vector3 direction = toTarget.sqrMagnitude > 1e-12f ? toTarget.normalized : (c - a).normalized;
            Vector3 pole = hint - a; pole -= direction * Vector3.Dot(pole, direction); if (pole.sqrMagnitude < 1e-10f) pole = Vector3.Cross(direction, root.right); pole.Normalize();
            float cosine = Mathf.Clamp((l1 * l1 + distance * distance - l2 * l2) / (2f * l1 * distance), -1f, 1f);
            Vector3 desiredMid = a + direction * (cosine * l1) + pole * (Mathf.Sqrt(Mathf.Max(0f, 1f - cosine * cosine)) * l1);
            root.rotation = Quaternion.Slerp(originalRoot, Quaternion.FromToRotation(b - a, desiredMid - a) * root.rotation, weight);
            b = mid.position; c = tip.position; Vector3 desiredTip = a + direction * distance;
            mid.rotation = Quaternion.Slerp(originalMid, Quaternion.FromToRotation(c - b, desiredTip - b) * mid.rotation, weight);
        }

        public static void SolveAim(Transform bone, Vector3 target, float weight = 1f)
        {
            Vector3 direction = target - bone.position; if (direction.sqrMagnitude < 1e-12f) return;
            bone.rotation = Quaternion.Slerp(bone.rotation, Quaternion.FromToRotation(bone.forward, direction) * bone.rotation, Mathf.Clamp01(weight));
        }

        public static void SolveFabrik(Transform[] chain, Vector3 target, int iterations, float weight = 1f)
        {
            if (chain == null || chain.Length < 2) throw new ArgumentException("IK_CHAIN_TOO_SHORT");
            var positions = chain.Select(value => value.position).ToArray(); var lengths = new float[chain.Length - 1]; float total = 0f;
            for (int i = 0; i < lengths.Length; i++) { lengths[i] = Vector3.Distance(positions[i], positions[i + 1]); total += lengths[i]; }
            Vector3 root = positions[0];
            if (Vector3.Distance(root, target) >= total)
            {
                Vector3 direction = (target - root).normalized; for (int i = 1; i < positions.Length; i++) positions[i] = positions[i - 1] + direction * lengths[i - 1];
            }
            else for (int iteration = 0; iteration < Mathf.Clamp(iterations, 1, 32); iteration++)
            {
                positions[positions.Length - 1] = target;
                for (int i = positions.Length - 2; i >= 0; i--) positions[i] = positions[i + 1] + (positions[i] - positions[i + 1]).normalized * lengths[i];
                positions[0] = root;
                for (int i = 1; i < positions.Length; i++) positions[i] = positions[i - 1] + (positions[i] - positions[i - 1]).normalized * lengths[i - 1];
            }
            for (int i = 0; i < chain.Length - 1; i++) chain[i].rotation = Quaternion.Slerp(chain[i].rotation, Quaternion.FromToRotation(chain[i + 1].position - chain[i].position, positions[i + 1] - positions[i]) * chain[i].rotation, Mathf.Clamp01(weight));
        }
    }

    public static class RigIkService
    {
        public static RigIkBinding Create(GameObject character, Transform root, Transform mid, Transform tip, RigIkSolverType solver = RigIkSolverType.TwoBone)
        {
            if (character == null || root == null || tip == null) throw new ArgumentNullException();
            var rigObject = new GameObject("IK_" + tip.name); Undo.RegisterCreatedObjectUndo(rigObject, "Create IK binding"); Undo.SetTransformParent(rigObject.transform, character.transform, "Parent IK binding");
            var binding = rigObject.AddComponent<RigIkBinding>(); binding.CharacterRoot = character.transform; binding.Solver = solver; binding.Root = root; binding.Mid = mid; binding.Tip = tip;
            binding.Chain.Add(root); if (mid != null) binding.Chain.Add(mid); if (tip != null) binding.Chain.Add(tip);
            var target = new GameObject("Target"); Undo.RegisterCreatedObjectUndo(target, "Create IK target"); Undo.SetTransformParent(target.transform, rigObject.transform, "Parent IK target"); target.transform.position = tip.position; binding.Target = target.transform;
            var hint = new GameObject("Hint"); Undo.RegisterCreatedObjectUndo(hint, "Create IK hint"); Undo.SetTransformParent(hint.transform, rigObject.transform, "Parent IK hint"); hint.transform.position = mid != null ? mid.position + root.up * Mathf.Max(0.1f, Vector3.Distance(root.position, mid.position)) : root.position + root.up; binding.Hint = hint.transform;
            NativeRigIkSolver.CaptureRestPose(binding); Selection.activeObject = rigObject; return binding;
        }

        public static void ApplyPlacementPreset(RigIkBinding binding, RigIkPlacementPreset preset)
        {
            if (binding == null || binding.Target == null || binding.Tip == null) throw new InvalidOperationException("IK_BINDING_INCOMPLETE");
            Undo.RecordObject(binding, "Set IK placement preset"); Undo.RecordObject(binding.Target, "Apply IK placement preset"); binding.PlacementPreset = preset;
            switch (preset)
            {
                case RigIkPlacementPreset.HandReachForward: binding.Target.position = binding.Tip.position + (binding.CharacterRoot != null ? binding.CharacterRoot.forward : binding.Tip.forward) * HandleUtility.GetHandleSize(binding.Tip.position) * 0.25f; break;
                case RigIkPlacementPreset.HandAtChest: binding.Target.position = binding.CharacterRoot != null ? binding.CharacterRoot.position + binding.CharacterRoot.up * 1.2f + binding.CharacterRoot.forward * 0.25f : binding.Tip.position; break;
                case RigIkPlacementPreset.FootPlantGround:
                    if (binding.CharacterRoot != null)
                    {
                        var renderers = binding.CharacterRoot.GetComponentsInChildren<Renderer>(true); float ground = renderers.Length > 0 ? renderers.Min(value => value.bounds.min.y) : binding.CharacterRoot.position.y;
                        var position = binding.Tip.position; position.y = ground; binding.Target.position = position;
                    }
                    break;
                default: binding.Target.position = binding.Tip.position; break;
            }
            EditorUtility.SetDirty(binding); NativeRigIkSolver.Solve(binding, true);
        }

        public static AnimationClip Bake(GameObject character, RigIkBinding binding, AnimationClip source, float frameRate = 30f, string outputFolder = null)
        {
            if (character == null || binding == null || source == null) throw new ArgumentNullException(); frameRate = Mathf.Clamp(frameRate, 1f, 120f);
            var context = RigTargetResolver.Resolve(character); var pose = RigPoseSnapshot.Capture(context.Root); int count = Mathf.Max(2, Mathf.CeilToInt(source.length * frameRate) + 1);
            var chain = binding.Chain != null && binding.Chain.Count >= 2 ? binding.Chain.Where(value => value != null) : new[] { binding.Root, binding.Mid, binding.Tip }.Where(value => value != null);
            var tracks = new Dictionary<Transform, RotationTrack>(); foreach (var bone in chain) if (bone != null && !tracks.ContainsKey(bone)) tracks[bone] = new RotationTrack();
            bool started = !AnimationMode.InAnimationMode(); if (started) AnimationMode.StartAnimationMode();
            try
            {
                for (int sample = 0; sample < count; sample++)
                {
                    float time = source.length * sample / (count - 1f); pose.Restore(); AnimationMode.BeginSampling(); try { AnimationMode.SampleAnimationClip(context.Root, source, time); } finally { AnimationMode.EndSampling(); }
                    NativeRigIkSolver.Solve(binding, false); foreach (var pair in tracks) pair.Value.Add(time, pair.Key.localRotation);
                }
            }
            finally { pose.Restore(); if (started && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode(); }
            var output = new AnimationClip { name = source.name + "_IKBake", frameRate = frameRate };
            foreach (var pair in tracks) pair.Value.Write(output, RigTargetResolver.RelativePath(pair.Key, context.Root.transform)); output.EnsureQuaternionContinuity();
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(outputFolder) ? "Assets/AssetModeler/Rigging/IKClips" : outputFolder);
            RigAssetSafety.CreateAssetWithUndo(output, AssetModelerAssetPaths.UniqueAssetPath(folder, output.name, ".anim"), "Create IK baked clip"); return output;
        }

        private sealed class RotationTrack
        {
            private readonly List<Keyframe> x = new List<Keyframe>(), y = new List<Keyframe>(), z = new List<Keyframe>(), w = new List<Keyframe>();
            public void Add(float time, Quaternion value) { x.Add(new Keyframe(time, value.x)); y.Add(new Keyframe(time, value.y)); z.Add(new Keyframe(time, value.z)); w.Add(new Keyframe(time, value.w)); }
            public void Write(AnimationClip clip, string path) { clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", new AnimationCurve(x.ToArray())); clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", new AnimationCurve(y.ToArray())); clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", new AnimationCurve(z.ToArray())); clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", new AnimationCurve(w.ToArray())); }
        }
    }

    public static class AnimationRiggingAdapter
    {
        public static bool IsAvailable => Resolve("UnityEngine.Animations.Rigging.RigBuilder") != null;
        public static string Status
        {
            get
            {
                var type = Resolve("UnityEngine.Animations.Rigging.RigBuilder");
                if (type == null) return "com.unity.animation.rigging não instalado; solver nativo ativo.";
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.Assembly);
                string version = package != null && !string.IsNullOrWhiteSpace(package.version) ? package.version : type.Assembly.GetName().Version.ToString();
                return $"com.unity.animation.rigging detectado ({version}); Two Bone, Multi-Aim e Chain IK disponíveis.";
            }
        }

        public static bool TryCreateTwoBoneConstraint(RigIkBinding binding, out Component constraint, out string message)
        {
            if (binding != null && binding.Solver != RigIkSolverType.TwoBone) { constraint = null; message = "O binding não usa Two Bone IK."; return false; }
            return TryCreateConstraint(binding, out constraint, out message);
        }

        public static bool TryCreateConstraint(RigIkBinding binding, out Component constraint, out string message)
        {
            constraint = null;
            if (binding == null || binding.CharacterRoot == null || binding.Root == null || binding.Target == null
                || (binding.Solver != RigIkSolverType.Aim && binding.Tip == null)
                || (binding.Solver == RigIkSolverType.TwoBone && binding.Mid == null))
            { message = "IK binding incompleto."; return false; }
            var builderType = Resolve("UnityEngine.Animations.Rigging.RigBuilder"); var rigType = Resolve("UnityEngine.Animations.Rigging.Rig"); var rigLayerType = Resolve("UnityEngine.Animations.Rigging.RigLayer");
            string constraintName = binding.Solver == RigIkSolverType.TwoBone ? "TwoBoneIKConstraint" : binding.Solver == RigIkSolverType.Aim ? "MultiAimConstraint" : "ChainIKConstraint";
            var constraintType = Resolve("UnityEngine.Animations.Rigging." + constraintName);
            if (builderType == null || rigType == null || rigLayerType == null || constraintType == null) { message = "Tipos do Animation Rigging não estão disponíveis."; return false; }
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Create Animation Rigging constraint");
            try
            {
                var character = binding.CharacterRoot.gameObject; if (character.GetComponent<Animator>() == null) Undo.AddComponent<Animator>(character);
                var builder = character.GetComponent(builderType) ?? Undo.AddComponent(character, builderType);
                string constrainedName = binding.Tip != null ? binding.Tip.name : binding.Root.name;
                var rigObject = new GameObject("AnimationRigging_" + constrainedName); Undo.RegisterCreatedObjectUndo(rigObject, "Create Animation Rigging root"); Undo.SetTransformParent(rigObject.transform, character.transform, "Parent Animation Rigging root");
                var rig = Undo.AddComponent(rigObject, rigType);
                var constraintObject = new GameObject(constraintName + "_" + constrainedName); Undo.RegisterCreatedObjectUndo(constraintObject, "Create Animation Rigging constraint"); Undo.SetTransformParent(constraintObject.transform, rigObject.transform, "Parent Animation Rigging constraint");
                constraint = Undo.AddComponent(constraintObject, constraintType);
                Undo.RecordObject(constraint, "Configure Animation Rigging constraint");
                constraintType.GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public)?.Invoke(constraint, null);
                ConfigureConstraintData(constraint, constraintType, binding);
                var weight = FindProperty(constraintType, "weight"); weight?.SetValue(constraint, Mathf.Clamp01(binding.Weight));
                var isValid = constraintType.GetMethod("IsValid", BindingFlags.Instance | BindingFlags.Public);
                if (isValid == null || !(bool)isValid.Invoke(constraint, null)) throw new InvalidOperationException(constraintName + " rejeitou a hierarquia ou referências configuradas.");
                Undo.RecordObject(builder, "Configure RigBuilder layers");
                var layersProperty = builderType.GetProperty("layers", BindingFlags.Instance | BindingFlags.Public); var layers = layersProperty?.GetValue(builder) as IList;
                if (layers == null) throw new InvalidOperationException("RigBuilder.layers não está disponível nesta versão do pacote.");
                layers.Add(Activator.CreateInstance(rigLayerType, new[] { rig, (object)true }));
                EditorUtility.SetDirty(builder); EditorUtility.SetDirty(constraint); EditorApplication.QueuePlayerLoopUpdate();
                Undo.CollapseUndoOperations(group); message = $"RigBuilder, Rig e {constraintName} criados e validados."; return true;
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(group); constraint = null; message = exception.Message; return false;
            }
        }

        private static Type Resolve(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var type = assembly.GetType(fullName, false); if (type != null) return type; }
            return null;
        }

        private static void ConfigureConstraintData(Component constraint, Type constraintType, RigIkBinding binding)
        {
            var field = FindField(constraintType, "m_Data") ?? throw new InvalidOperationException("Animation Rigging constraint data field not found.");
            object data = field.GetValue(constraint); Type dataType = data.GetType();
            if (binding.Solver == RigIkSolverType.TwoBone)
            {
                SetData(dataType, data, "root", binding.Root); SetData(dataType, data, "mid", binding.Mid); SetData(dataType, data, "tip", binding.Tip);
                SetData(dataType, data, "target", binding.Target); SetData(dataType, data, "hint", binding.Hint);
                SetData(dataType, data, "targetPositionWeight", binding.Weight); SetData(dataType, data, "targetRotationWeight", binding.Weight); SetData(dataType, data, "hintWeight", binding.Hint != null ? binding.Weight : 0f);
            }
            else if (binding.Solver == RigIkSolverType.Fabrik)
            {
                SetData(dataType, data, "root", binding.Root); SetData(dataType, data, "tip", binding.Tip); SetData(dataType, data, "target", binding.Target);
                SetData(dataType, data, "chainRotationWeight", binding.Weight); SetData(dataType, data, "tipRotationWeight", binding.Weight);
                SetData(dataType, data, "maxIterations", Mathf.Clamp(binding.Iterations, 1, 50)); SetData(dataType, data, "tolerance", 0.0001f);
            }
            else
            {
                SetData(dataType, data, "constrainedObject", binding.Root);
                Type arrayType = Resolve("UnityEngine.Animations.Rigging.WeightedTransformArray"); Type weightedType = Resolve("UnityEngine.Animations.Rigging.WeightedTransform");
                if (arrayType == null || weightedType == null) throw new InvalidOperationException("WeightedTransform API not found.");
                object sources = Activator.CreateInstance(arrayType, new object[] { 0 }); object weighted = Activator.CreateInstance(weightedType, new object[] { binding.Target, 1f });
                arrayType.GetMethod("Add", new[] { weightedType })?.Invoke(sources, new[] { weighted }); SetData(dataType, data, "sourceObjects", sources);
                SetData(dataType, data, "maintainOffset", false);
            }
            field.SetValue(constraint, data);
        }

        private static void SetData(Type dataType, object data, string propertyName, object value)
        {
            var property = dataType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite) throw new InvalidOperationException("Animation Rigging data property not found: " + propertyName);
            property.SetValue(data, value);
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) return field;
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (property != null) return property;
            }
            return null;
        }
    }

    [CustomEditor(typeof(RigIkBinding))]
    public sealed class RigIkBindingEditor : UnityEditor.Editor
    {
        private void OnSceneGUI()
        {
            var binding = (RigIkBinding)target; if (binding.Target == null) return;
            EditorGUI.BeginChangeCheck(); var targetPosition = Handles.PositionHandle(binding.Target.position, binding.Target.rotation); var hintPosition = binding.Hint != null ? Handles.PositionHandle(binding.Hint.position, binding.Hint.rotation) : Vector3.zero;
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(binding.Target, "Move IK target"); binding.Target.position = targetPosition;
                if (binding.Hint != null) { Undo.RecordObject(binding.Hint, "Move IK hint"); binding.Hint.position = hintPosition; }
                NativeRigIkSolver.Solve(binding, true);
            }
            Handles.color = Color.cyan; if (binding.Root != null) Handles.DrawAAPolyLine(3f, binding.Root.position, binding.Target.position);
        }
    }
}
