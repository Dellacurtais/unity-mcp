using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Transform;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class ModelScaleServiceTests
    {
        private const string Folder = "Assets/__ModelScaleServiceTests";
        private readonly List<Object> _objects = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__ModelScaleServiceTests");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null && !EditorUtility.IsPersistent(_objects[i])) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void BakeStaticHierarchy_PreservesWorldVerticesAndSource_WithNonIdentityParent()
        {
            var parent = Keep(new GameObject("ExternalParent"));
            parent.transform.SetPositionAndRotation(new Vector3(5, 2, -3), Quaternion.Euler(10, 35, 0));
            parent.transform.localScale = new Vector3(2, 3, 4);
            var root = StaticModel();
            root.transform.SetParent(parent.transform, false);
            root.transform.localPosition = new Vector3(1, 2, 3);
            root.transform.localRotation = Quaternion.Euler(0, 15, 0);
            root.transform.localScale = Vector3.one * 3;
            var mesh = root.GetComponentInChildren<MeshFilter>().sharedMesh;
            var originalHash = MeshFingerprint.Compute(mesh);
            var positions = WorldVertices(root.GetComponentInChildren<MeshFilter>());
            var settings = Settings();
            var beforeAssets = AssetDatabase.FindAssets("", new[] { Folder }).Length;
            var preview = ModelScaleService.Preview(root, settings);
            Assert.That(AssetDatabase.FindAssets("", new[] { Folder }).Length, Is.EqualTo(beforeAssets), "Preview must not create assets");
            Assert.That(preview.VisualScaleFactor, Is.EqualTo(1f));
            var result = Apply(root, settings, preview);
            Assert.That(result.Root.transform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(result.Root.transform.parent, Is.SameAs(parent.transform));
            AssertPoints(WorldVertices(result.Root.GetComponentInChildren<MeshFilter>()), positions);
            Assert.That(MeshFingerprint.Compute(mesh), Is.EqualTo(originalHash));
            Assert.That(root.transform.localScale, Is.EqualTo(Vector3.one * 3));
            Assert.That(result.Root.GetComponentInChildren<MeshFilter>().sharedMesh, Is.Not.SameAs(mesh));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(result.CreatedAssetPaths.Last()), Is.Not.Null);
        }

        [TestCase(ModelScaleAxis.X)]
        [TestCase(ModelScaleAxis.Y)]
        [TestCase(ModelScaleAxis.Z)]
        public void TargetDimension_UsesRootAxesAndUniformProportions(ModelScaleAxis axis)
        {
            var root = StaticModel();
            root.transform.localScale = Vector3.one * 0.1f;
            var settings = Settings(ModelScaleMode.TargetDimension);
            settings.Axis = axis;
            settings.TargetDimension = 2.5f;
            var preview = ModelScaleService.Preview(root, settings);
            var result = Apply(root, settings, preview);
            Assert.That(preview.BoundsAfter.size[(int)axis], Is.EqualTo(2.5f).Within(1e-5f));
            Assert.That(result.Root.transform.localScale, Is.EqualTo(Vector3.one));
            var after = ModelScaleService.Preview(result.Root, Settings());
            AssertPoints(new[] { after.BoundsBefore.size }, new[] { preview.BoundsAfter.size });
            for (int i = 0; i < 3; i++)
                Assert.That(preview.BoundsAfter.size[i] / preview.BoundsBefore.size[i], Is.EqualTo(preview.VisualScaleFactor).Within(1e-4f));
        }

        [Test]
        public void NativeBakeCoordinates_MatchIndependentSkinEquationAndKnownRestVertices()
        {
            var root = RigModel(out var skin, out var bone);
            root.transform.localScale = Vector3.one * 2.5f;
            root.transform.position = new Vector3(4, 1, -2);
            AssertPoints(CpuSkinnedWorldVertices(skin), new[]
            {
                new Vector3(4, 1, -2), new Vector3(6.5f, 1, 0.5f), new Vector3(4, 6, -2),
            });
            BakedWorldVertices(skin);
            root.transform.rotation = Quaternion.Euler(10, 35, 20);
            bone.localRotation = Quaternion.Euler(25, -15, 40);
            skin.SetBlendShapeWeight(0, 40);
            BakedWorldVertices(skin);
        }

        [Test]
        public void SkinnedHierarchy_BakePreservesDeformedVerticesAcrossPosesAndBlendWeights()
        {
            var root = RigModel(out var sourceSkin, out var bone);
            root.transform.localScale = Vector3.one * 2.5f;
            root.transform.position = new Vector3(4, 1, -2);
            var sourceMeshHash = MeshFingerprint.Compute(sourceSkin.sharedMesh);
            var result = Apply(root, Settings());
            var copySkin = result.Root.GetComponentInChildren<SkinnedMeshRenderer>();
            var copyBone = result.Root.transform.Find("Bone");
            Assert.That(copySkin.bones[0], Is.SameAs(copyBone));
            Assert.That(copySkin.rootBone, Is.SameAs(copyBone));
            Assert.That(copySkin.bones[0], Is.Not.SameAs(bone));
            foreach (float angle in new[] { -30f, 0f, 55f })
            {
                bone.localRotation = Quaternion.Euler(angle, angle * 0.3f, angle * 0.5f);
                copyBone.localRotation = bone.localRotation;
                foreach (float weight in new[] { 0f, 40f, 100f })
                {
                    sourceSkin.SetBlendShapeWeight(0, weight);
                    copySkin.SetBlendShapeWeight(0, weight);
                    AssertPoints(BakedWorldVertices(copySkin), BakedWorldVertices(sourceSkin));
                }
            }
            Assert.That(MeshFingerprint.Compute(sourceSkin.sharedMesh), Is.EqualTo(sourceMeshHash));
            Assert.That(copySkin.sharedMesh.bindposes[0].m13, Is.EqualTo(sourceSkin.sharedMesh.bindposes[0].m13 * 2.5f).Within(1e-6f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GenericAnimation_CopiesEffectiveOverridesAndScalesCurveValuesTangentsAndEvents(bool withGenericAvatar)
        {
            var root = RigModel(out var sourceSkin, out _);
            var sourceAvatar = withGenericAvatar ? Keep(AvatarBuilder.BuildGenericAvatar(root, "Bone")) : null;
            if (sourceAvatar != null) Assert.That(sourceAvatar.isValid && !sourceAvatar.isHuman, Is.True);
            root.transform.localScale = Vector3.one * 3;
            var baseClip = Keep(new AnimationClip { name = "BaseClip" });
            var effective = Keep(new AnimationClip { name = "EffectiveClip" });
            var binding = EditorCurveBinding.FloatCurve("Bone", typeof(Transform), "m_LocalPosition.x");
            var curve = new AnimationCurve(new Keyframe(0, 1, 2, 2), new Keyframe(1, 2, 2, 2));
            AnimationUtility.SetEditorCurve(baseClip, binding, AnimationCurve.Linear(0, 0, 1, 10));
            AnimationUtility.SetEditorCurve(effective, binding, curve);
            AnimationUtility.SetAnimationEvents(effective, new[] { new AnimationEvent { time = 0.25f, functionName = "Marker", objectReferenceParameter = sourceSkin.sharedMesh } });
            AssetDatabase.CreateAsset(baseClip, Folder + "/Base.anim");
            AssetDatabase.CreateAsset(effective, Folder + "/Effective.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Folder + "/Source.controller");
            var state = controller.layers[0].stateMachine.AddState("Motion");
            state.motion = baseClip;
            var sourceOverride = Keep(new AnimatorOverrideController(controller));
            sourceOverride[baseClip] = effective;
            var animator = root.AddComponent<Animator>();
            animator.avatar = sourceAvatar;
            animator.runtimeAnimatorController = sourceOverride;
            var result = Apply(root, Settings());
            if (sourceAvatar != null)
            {
                Assert.That(result.Root.GetComponent<Animator>().avatar, Is.Not.SameAs(sourceAvatar));
                Assert.That(result.Root.GetComponent<Animator>().avatar.isValid, Is.True);
            }
            var outputController = (AnimatorOverrideController)result.Root.GetComponent<Animator>().runtimeAnimatorController;
            var clip = outputController.animationClips.Single();
            Assert.That(clip, Is.Not.SameAs(effective));
            var outputCurve = AnimationUtility.GetEditorCurve(clip, binding);
            Assert.That(outputCurve.Evaluate(0.3f), Is.EqualTo(curve.Evaluate(0.3f) * 3).Within(1e-5f));
            Assert.That(outputCurve.keys[0].outTangent, Is.EqualTo(6f));
            Assert.That(AnimationUtility.GetEditorCurve(effective, binding).keys[0].value, Is.EqualTo(1f));
            var clipEvent = AnimationUtility.GetAnimationEvents(clip).Single();
            Assert.That(clipEvent.objectReferenceParameter, Is.SameAs(result.Root.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh));
            Assert.That(sourceOverride[baseClip], Is.SameAs(effective));
            // Actual Animator sampling exercises the replacement clips, not only
            // their serialized numbers.
            animator.Rebind();
            var outputAnimator = result.Root.GetComponent<Animator>();
            outputAnimator.Rebind();
            animator.Play("Motion", 0, 0.3f); animator.Update(0f);
            outputAnimator.Play("Motion", 0, 0.3f); outputAnimator.Update(0f);
            AssertPoints(BakedWorldVertices(result.Root.GetComponentInChildren<SkinnedMeshRenderer>()), BakedWorldVertices(sourceSkin));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HumanoidAvatar_RebuildsPhysicalScaleAndPreservesPosedMesh(bool renamedRoot)
        {
            var root = HumanModel(out var animator, out var skin);
            if (renamedRoot)
            {
                root.name += "_Editable";
                var parent = Keep(new GameObject("HumanParent"));
                parent.transform.SetPositionAndRotation(new Vector3(4, -2, 1), Quaternion.Euler(0, 30, 0));
                parent.transform.localScale = Vector3.one * 1.7f;
                root.transform.SetParent(parent.transform, false);
                root.transform.localPosition = new Vector3(1, 0, 2);
                root.transform.localRotation = Quaternion.Euler(0, 25, 0);
            }
            root.transform.localScale = Vector3.one * 2;
            float originalHumanScale = animator.humanScale;
            var sourceAvatar = animator.avatar;
            var result = Apply(root, Settings());
            var outputAnimator = result.Root.GetComponent<Animator>();
            Assert.That(outputAnimator.avatar, Is.Not.SameAs(sourceAvatar));
            Assert.That(outputAnimator.avatar.isHuman && outputAnimator.avatar.isValid, Is.True);
            Assert.That(outputAnimator.humanScale, Is.EqualTo(originalHumanScale * 2).Within(1e-3f));
            using (var before = new HumanPoseHandler(animator.avatar, root.transform))
            using (var after = new HumanPoseHandler(outputAnimator.avatar, result.Root.transform))
            {
                var pose = new HumanPose();
                before.GetHumanPose(ref pose);
                int muscle = Array.FindIndex(HumanTrait.MuscleName, name => name.Contains("Left Arm Down-Up"));
                Assert.That(muscle, Is.GreaterThanOrEqualTo(0));
                pose.muscles[muscle] = 0.3f;
                before.SetHumanPose(ref pose);
                after.SetHumanPose(ref pose);
                AssertPoints(BakedWorldVertices(result.Root.GetComponentInChildren<SkinnedMeshRenderer>()), BakedWorldVertices(skin), 0.005f);
            }
            Assert.That(animator.avatar, Is.SameAs(sourceAvatar));
        }

        [Test]
        public void AuthoredHumanoidMotion_PublicCurvesProduceNativeHumanoidClip()
        {
            var clip = Keep(new AnimationClip { name = "AuthoredHumanoidMotion" });
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Spine Front-Back"), AnimationCurve.Linear(0, 0, 1, 0.2f));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.x"), AnimationCurve.Linear(0, 0, 1, 2));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootQ.w"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.CreateAsset(clip, Folder + "/AuthoredHumanoidMotion.anim");
            TestContext.WriteLine("humanMotion=" + clip.humanMotion + ", hasRootCurves=" + clip.hasRootCurves + ", hasMotionCurves=" + clip.hasMotionCurves);
            Assert.That(clip.humanMotion, Is.True);
        }

        [TestCase(ModelScaleMode.BakeCurrentScale)]
        [TestCase(ModelScaleMode.TargetDimension)]
        public void AuthoredHumanoidMotion_PreservesAnimatorDeltaPositionAndPosesAcrossFrames(ModelScaleMode mode)
        {
            var clip = Keep(new AnimationClip { name = "HumanoidWithMotion" });
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Spine Front-Back"), AnimationCurve.Linear(0, 0, 1, 0.2f));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.x"), AnimationCurve.Linear(0, 0, 1, 2));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y"), AnimationCurve.Constant(0, 1, 1));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.z"), AnimationCurve.Constant(0, 1, 0));
            foreach (var axis in new[] { "x", "y", "z", "w" })
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootQ." + axis), AnimationCurve.Constant(0, 1, axis == "w" ? 1 : 0));
            var clipSettings = AnimationUtility.GetAnimationClipSettings(clip);
            clipSettings.loopBlendPositionXZ = false;
            clipSettings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings(clip, clipSettings);
            AssetDatabase.CreateAsset(clip, Folder + "/HumanoidWithMotion.anim");
            Assert.That(clip.humanMotion, Is.True, "Public muscle bindings must produce native Humanoid animation data");
            var root = HumanModel(out var animator, out var skin);
            root.transform.localScale = Vector3.one * 2;
            root.transform.rotation = Quaternion.Euler(0, 25, 0);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Folder + "/HumanMotion.controller");
            var state = controller.layers[0].stateMachine.AddState("Motion"); state.motion = clip;
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = true;
            var settings = Settings(mode);
            settings.TargetDimension = ModelScaleService.Preview(root, Settings()).BoundsBefore.size.y * 1.75f;
            var result = Apply(root, settings);
            var outputAnimator = result.Root.GetComponent<Animator>();
            animator.applyRootMotion = true; outputAnimator.applyRootMotion = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            outputAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind(); outputAnimator.Rebind();
            animator.Play("Motion", 0, 0); outputAnimator.Play("Motion", 0, 0);
            animator.Update(0); outputAnimator.Update(0);
            float totalMotion = 0;
            for (int frame = 0; frame < 8; frame++)
            {
                animator.Update(0.05f); outputAnimator.Update(0.05f);
                TestContext.WriteLine("Root motion source=" + animator.deltaPosition + " output=" + outputAnimator.deltaPosition);
                totalMotion += animator.deltaPosition.magnitude;
                Assert.That(Vector3.Distance(outputAnimator.deltaPosition, animator.deltaPosition * result.Preview.VisualScaleFactor), Is.LessThan(0.005f));
                Assert.That(Quaternion.Angle(outputAnimator.deltaRotation, animator.deltaRotation), Is.LessThan(0.01f));
                AssertPoints(BakedWorldVertices(result.Root.GetComponentInChildren<SkinnedMeshRenderer>()),
                    BakedWorldVertices(skin).Select(point => point * result.Preview.VisualScaleFactor).ToArray(), 0.01f);
            }
            Assert.That(totalMotion, Is.GreaterThan(0.01f), "A zero-motion clip does not verify root-motion scaling");
        }

        [Test]
        public void ColliderGeometry_FollowsCopyAndOriginalRemainsUntouched()
        {
            var root = StaticModel();
            root.transform.localScale = Vector3.one * 4;
            var child = root.GetComponentInChildren<MeshFilter>().gameObject;
            var meshCollider = child.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = child.GetComponent<MeshFilter>().sharedMesh;
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(1, 2, 3); box.size = new Vector3(2, 3, 4);
            var sphere = root.AddComponent<SphereCollider>(); sphere.radius = 0.2f;
            var capsule = child.AddComponent<CapsuleCollider>(); capsule.radius = 0.1f; capsule.height = 1.5f;
            var result = Apply(root, Settings());
            Assert.That(result.Root.GetComponent<BoxCollider>().center, Is.EqualTo(box.center * 4));
            Assert.That(result.Root.GetComponent<BoxCollider>().size, Is.EqualTo(box.size * 4));
            Assert.That(result.Root.GetComponent<SphereCollider>().radius, Is.EqualTo(0.8f));
            Assert.That(result.Root.GetComponentInChildren<CapsuleCollider>().height, Is.EqualTo(6f));
            var outputCollider = result.Root.GetComponentInChildren<MeshCollider>();
            Assert.That(outputCollider.sharedMesh, Is.SameAs(result.Root.GetComponentInChildren<MeshFilter>().sharedMesh));
            Assert.That(outputCollider.sharedMesh, Is.Not.SameAs(meshCollider.sharedMesh));
            Assert.That(box.size, Is.EqualTo(new Vector3(2, 3, 4)));
        }

        [Test]
        public void CharacterController_ScalesEverySpatialSettingAndPreservesSource()
        {
            var root = StaticModel();
            var source = root.AddComponent<CharacterController>();
            source.center = new Vector3(0.2f, 1, 0); source.height = 2; source.radius = 0.4f;
            source.stepOffset = 0.2f; source.skinWidth = 0.04f; source.minMoveDistance = 0.01f;
            root.transform.localScale = Vector3.one * 0.1f;
            var copy = Apply(root, Settings()).Root.GetComponent<CharacterController>();
            Assert.That(copy.center, Is.EqualTo(source.center * 0.1f));
            Assert.That(copy.height, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(copy.radius, Is.EqualTo(0.04f).Within(1e-6f));
            Assert.That(copy.stepOffset, Is.EqualTo(0.02f).Within(1e-6f));
            Assert.That(copy.skinWidth, Is.EqualTo(0.004f).Within(1e-6f));
            Assert.That(copy.minMoveDistance, Is.EqualTo(0.001f).Within(1e-6f));
            Assert.That(source.minMoveDistance, Is.EqualTo(0.01f));
        }

        [Test]
        public void LegacyAnimation_PreservesStateConfigurationAndCopiesTranslationClip()
        {
            var root = StaticModel(); root.transform.localScale = Vector3.one * 3;
            var clip = Keep(new AnimationClip { legacy = true, name = "LegacyMotion" });
            clip.SetCurve("Geometry", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 1, 1, 2));
            var source = root.AddComponent<Animation>(); source.playAutomatically = false;
            source.AddClip(clip, "Configured"); source.clip = clip;
            var state = source["Configured"]; state.speed = 0.4f; state.weight = 0.7f;
            state.layer = 3; state.blendMode = AnimationBlendMode.Additive; state.wrapMode = WrapMode.PingPong;
            state.time = 0.25f; state.enabled = false;
            var result = Apply(root, Settings());
            var output = result.Root.GetComponent<Animation>();
            var actual = output["Configured"];
            Assert.That(actual.clip, Is.Not.SameAs(clip));
            Assert.That(EditorUtility.IsPersistent(actual.clip), Is.True, "Aliased legacy clips must survive prefab serialization");
            Assert.That(output.clip == actual.clip, Is.EqualTo(source.clip == state.clip), "Default clip and aliased state identity relationship is preserved");
            Assert.That(output.clip, Is.Not.SameAs(source.clip));
            Assert.That(actual.speed, Is.EqualTo(state.speed)); Assert.That(actual.weight, Is.EqualTo(state.weight));
            Assert.That(actual.layer, Is.EqualTo(state.layer)); Assert.That(actual.blendMode, Is.EqualTo(state.blendMode));
            Assert.That(actual.wrapMode, Is.EqualTo(state.wrapMode)); Assert.That(actual.time, Is.EqualTo(state.time));
            Assert.That(actual.enabled, Is.EqualTo(state.enabled));
            var binding = EditorCurveBinding.FloatCurve("Geometry", typeof(Transform), "m_LocalPosition.x");
            Assert.That(AnimationUtility.GetEditorCurve(actual.clip, binding).Evaluate(0.5f), Is.EqualTo(4.5f).Within(1e-5f));
            var prefabPath = result.CreatedAssetPaths.Last();
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var serializedAnimation = new SerializedObject(prefab.GetComponent<Animation>());
            var clips = serializedAnimation.FindProperty("m_Animations");
            Assert.That(clips.arraySize, Is.GreaterThan(0));
            for (int i = 0; i < clips.arraySize; i++)
                Assert.That(clips.GetArrayElementAtIndex(i).objectReferenceValue, Is.Not.Null, "Every legacy state must retain a saved clip after reimport");
        }

        [TestCase(false, "MODEL_MESH_UNREADABLE")]
        [TestCase(true, "MODEL_RENDERER_UNSUPPORTED")]
        public void IncompleteMeshAndEffectRenderers_RejectBeforeCreatingAssets(bool effect, string errorCode)
        {
            var root = StaticModel();
            var child = new GameObject(effect ? "BeamHalo" : "MissingMeshFilter");
            child.transform.SetParent(root.transform, false);
            Renderer renderer = effect ? (Renderer)child.AddComponent<LineRenderer>() : child.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = Array.Empty<Material>();
            var settings = Settings();
            settings.OutputFolder = Folder + "/RejectedNormalization";
            int before = AssetDatabase.FindAssets("", new[] { Folder }).Length;
            Assert.That(Assert.Throws<InvalidOperationException>(() => ModelScaleService.Preview(root, settings)).Message,
                Does.StartWith(errorCode));
            Assert.That(AssetDatabase.IsValidFolder(settings.OutputFolder), Is.False);
            Assert.That(AssetDatabase.FindAssets("", new[] { Folder }).Length, Is.EqualTo(before));
        }

        [Test]
        public void ExternalLodAndTransientMaterial_RejectBeforeCreatingAssets()
        {
            var root = StaticModel();
            var external = StaticModel();
            var lod = root.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0.5f, external.GetComponentsInChildren<Renderer>()) });
            int before = AssetDatabase.FindAssets("", new[] { Folder }).Length;
            Assert.That(Assert.Throws<InvalidOperationException>(() => ModelScaleService.Preview(root, Settings())).Message, Does.StartWith("MODEL_EXTERNAL_LOD_RENDERER"));
            Object.DestroyImmediate(lod);
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            root.GetComponentInChildren<Renderer>().sharedMaterial = Keep(new Material(shader));
            Assert.That(Assert.Throws<InvalidOperationException>(() => ModelScaleService.Preview(root, Settings())).Message, Does.StartWith("MODEL_TRANSIENT_MATERIAL"));
            Assert.That(AssetDatabase.FindAssets("", new[] { Folder }).Length, Is.EqualTo(before));
        }

        [Test]
        public void UndoRedo_SceneCopyRestoresWithoutBreakingGeneratedPrefabOrMeshAssets()
        {
            var root = StaticModel();
            var result = Apply(root, Settings());
            int id = result.Root.GetInstanceID();
            var prefabPath = result.CreatedAssetPaths.Last();
            Undo.PerformUndo();
            Assert.That(EditorUtility.InstanceIDToObject(id), Is.Null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab.GetComponentInChildren<MeshFilter>().sharedMesh, Is.Not.Null);
            foreach (var path in result.CreatedAssetPaths) Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.Not.Null);
            Undo.PerformRedo();
            var restored = EditorUtility.InstanceIDToObject(id) as GameObject;
            Assert.That(restored, Is.Not.Null);
            if (restored != null) _objects.Add(restored);
            Assert.That(restored.GetComponentInChildren<MeshFilter>().sharedMesh, Is.SameAs(prefab.GetComponentInChildren<MeshFilter>().sharedMesh));
        }

        [TestCase(0f, 0f, 0f)]
        [TestCase(-1f, -1f, -1f)]
        [TestCase(1f, 2f, 1f)]
        [TestCase(0.00001f, 0.0000109f, 0.00001f)]
        public void UnsupportedRootScale_FailsBeforeAssetsAreWritten(float x, float y, float z)
        {
            var root = StaticModel();
            root.transform.localScale = new Vector3(x, y, z);
            int count = AssetDatabase.FindAssets("", new[] { Folder }).Length;
            Assert.That(Assert.Throws<InvalidOperationException>(() => ModelScaleService.Preview(root, Settings())).Message,
                Does.StartWith("MODEL_ROOT_SCALE_UNSUPPORTED"));
            Assert.That(AssetDatabase.FindAssets("", new[] { Folder }).Length, Is.EqualTo(count));
        }

        [Test]
        public void ExternalBones_AndAnimatedRootScale_AreRejectedBeforeWrites()
        {
            var root = RigModel(out var skin, out _);
            var external = Keep(new GameObject("ExternalBone"));
            skin.bones = new[] { external.transform };
            Assert.That(Assert.Throws<InvalidOperationException>(() => ModelScaleService.Preview(root, Settings())).Message, Does.StartWith("MODEL_EXTERNAL_BONE"));
            skin.bones = new[] { root.transform.Find("Bone") };
            var clip = Keep(new AnimationClip { legacy = true });
            clip.SetCurve("", typeof(Transform), "m_LocalScale.x", AnimationCurve.Linear(0, 1, 1, 2));
            var animation = root.AddComponent<Animation>(); animation.AddClip(clip, "Bad");
            Assert.That(Assert.Throws<InvalidOperationException>(() => ModelScaleService.Preview(root, Settings())).Message, Does.StartWith("MODEL_ANIMATED_ROOT_SCALE"));
        }

        [Test]
        public void StaleSourceAndChangedSettings_CannotApply()
        {
            var root = StaticModel();
            var settings = Settings();
            var preview = ModelScaleService.Preview(root, settings);
            root.transform.GetChild(0).localPosition += Vector3.up;
            Assert.That(Assert.Throws<InvalidOperationException>(() => ModelScaleService.Apply(root, settings, preview.SourceHash, preview.PreviewHash)).Message, Does.StartWith("STALE_MODEL_SOURCE"));
            preview = ModelScaleService.Preview(root, settings);
            settings.Mode = ModelScaleMode.TargetDimension;
            Assert.That(Assert.Throws<InvalidOperationException>(() => ModelScaleService.Apply(root, settings, preview.SourceHash, preview.PreviewHash)).Message, Does.StartWith("STALE_MODEL_PREVIEW"));
        }

        [Test]
        public void LegacyVertexTransform_BlocksRigAndDegenerateMatrices()
        {
            var root = RigModel(out var skin, out _);
            var document = Keep(ScriptableObject.CreateInstance<MeshEditDocument>());
            document.Initialize(root, skin.sharedMesh, skin.sharedMesh);
            var context = new MeshOperationContext(document, skin.sharedMesh, document.Revision, default);
            Assert.That(new TransformVerticesOperation(Matrix4x4.Scale(Vector3.one * 2)).Validate(context).Issues.Any(issue => issue.Code == "TRANSFORM_RIG_UNSUPPORTED"), Is.True);
            Assert.That(new TransformVerticesOperation(Matrix4x4.Scale(Vector3.zero)).Validate(context).Issues.Any(issue => issue.Code == "TRANSFORM_MATRIX_UNSUPPORTED"), Is.True);
        }

        private ModelScaleResult Apply(GameObject root, ModelScaleSettings settings, ModelScalePreview preview = null)
        {
            preview = preview ?? ModelScaleService.Preview(root, settings);
            var result = ModelScaleService.Apply(root, settings, preview.SourceHash, preview.PreviewHash);
            _objects.Add(result.Root);
            return result;
        }

        private static ModelScaleSettings Settings(ModelScaleMode mode = ModelScaleMode.BakeCurrentScale)
            => new ModelScaleSettings { Mode = mode, OutputFolder = Folder, TargetDimension = 1.8f };

        private GameObject StaticModel()
        {
            var root = Keep(new GameObject("StaticModel"));
            var child = new GameObject("Geometry"); child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(0.3f, 0.5f, 0.1f);
            child.AddComponent<MeshFilter>().sharedMesh = Keep(Mesh());
            child.AddComponent<MeshRenderer>();
            return root;
        }

        private GameObject RigModel(out SkinnedMeshRenderer renderer, out Transform bone)
        {
            var root = Keep(new GameObject("RigModel"));
            bone = new GameObject("Bone").transform; bone.SetParent(root.transform, false); bone.localPosition = Vector3.up;
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();
            var mesh = Keep(Mesh());
            mesh.bindposes = new[] { bone.worldToLocalMatrix * body.transform.localToWorldMatrix };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray();
            var delta = Enumerable.Repeat(new Vector3(0.1f, 0.05f, 0.02f), mesh.vertexCount).ToArray();
            mesh.AddBlendShapeFrame("Expand", 100, delta, new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
            renderer.sharedMesh = mesh; renderer.bones = new[] { bone }; renderer.rootBone = bone;
            renderer.localBounds = mesh.bounds; renderer.updateWhenOffscreen = true;
            return root;
        }

        private GameObject HumanModel(out Animator animator, out SkinnedMeshRenderer renderer)
        {
            var root = Keep(new GameObject("HumanModel"));
            var bones = new Dictionary<string, Transform>();
            Transform Add(string name, string parent, Vector3 position)
            {
                var value = new GameObject(name).transform;
                value.SetParent(parent == null ? root.transform : bones[parent], false);
                value.localPosition = position; bones[name] = value; return value;
            }
            Add("Hips", null, new Vector3(0, 1, 0));
            Add("Spine", "Hips", new Vector3(0, 0.25f, 0)); Add("Chest", "Spine", new Vector3(0, 0.2f, 0));
            Add("Neck", "Chest", new Vector3(0, 0.2f, 0)); Add("Head", "Neck", new Vector3(0, 0.15f, 0));
            foreach (var side in new[] { "Left", "Right" })
            {
                float sign = side == "Left" ? -1 : 1;
                Add(side + "UpperArm", "Chest", new Vector3(sign * 0.2f, 0.15f, 0));
                Add(side + "LowerArm", side + "UpperArm", new Vector3(sign * 0.35f, 0, 0));
                Add(side + "Hand", side + "LowerArm", new Vector3(sign * 0.25f, 0, 0));
                Add(side + "UpperLeg", "Hips", new Vector3(sign * 0.13f, -0.05f, 0));
                Add(side + "LowerLeg", side + "UpperLeg", new Vector3(0, -0.45f, 0));
                Add(side + "Foot", side + "LowerLeg", new Vector3(0, -0.45f, 0.1f));
            }
            var description = new HumanDescription
            {
                human = bones.Keys.Select(name => new HumanBone { boneName = name, humanName = name, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = root.GetComponentsInChildren<Transform>().Select(value => new SkeletonBone { name = value.name, position = value.localPosition, rotation = value.localRotation, scale = value.localScale }).ToArray(),
                upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f, armStretch = 0.05f, legStretch = 0.05f,
            };
            var avatar = Keep(AvatarBuilder.BuildHumanAvatar(root, description));
            Assert.That(avatar.isValid && avatar.isHuman, Is.True, "Synthetic Humanoid fixture must have a valid Avatar");
            animator = root.AddComponent<Animator>(); animator.avatar = avatar;
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();
            var mesh = Keep(Mesh());
            var weightedBone = bones["LeftUpperArm"];
            mesh.bindposes = new[] { weightedBone.worldToLocalMatrix * body.transform.localToWorldMatrix };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray();
            renderer.sharedMesh = mesh; renderer.bones = new[] { weightedBone }; renderer.rootBone = bones["Hips"]; renderer.updateWhenOffscreen = true;
            return root;
        }

        private T Keep<T>(T value) where T : Object { _objects.Add(value); return value; }
        private static Mesh Mesh()
        {
            var mesh = new Mesh { name = "ScaleMesh" };
            mesh.vertices = new[] { Vector3.zero, new Vector3(1, 0, 1), new Vector3(0, 2, 0) };
            mesh.triangles = new[] { 0, 1, 2 }; mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds(); return mesh;
        }
        private static Vector3[] WorldVertices(MeshFilter filter)
            => filter.sharedMesh.vertices.Select(value => filter.transform.TransformPoint(value)).ToArray();
        private static Vector3[] BakedWorldVertices(SkinnedMeshRenderer skin)
        {
            var mesh = new Mesh();
            try
            {
                skin.BakeMesh(mesh, false);
                // Unity 6.3's false overload retains the renderer's scale in
                // baked coordinates; adding TransformPoint would scale twice.
                var matrix = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
                var actual = mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
                // Independently verify native BakeMesh against the skinning
                // equation, so this conversion cannot conceal a product error.
                AssertPoints(actual, CpuSkinnedWorldVertices(skin), 0.002f);
                return actual;
            }
            finally { Object.DestroyImmediate(mesh); }
        }
        private static Vector3[] CpuSkinnedWorldVertices(SkinnedMeshRenderer skin)
        {
            var mesh = skin.sharedMesh;
            var vertices = mesh.vertices;
            var deltas = new Vector3[vertices.Length];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                Assert.That(mesh.GetBlendShapeFrameCount(shape), Is.EqualTo(1), "Fixture reference supports one linear frame");
                mesh.GetBlendShapeFrameVertices(shape, 0, deltas, null, null);
                float blend = skin.GetBlendShapeWeight(shape) / mesh.GetBlendShapeFrameWeight(shape, 0);
                for (int i = 0; i < vertices.Length; i++) vertices[i] += deltas[i] * blend;
            }
            var bindposes = mesh.bindposes;
            var bones = skin.bones;
            var weights = mesh.boneWeights;
            var output = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var weight = weights[i];
                Vector3 Weighted(int index, float amount) => amount == 0 ? Vector3.zero
                    : (bones[index].localToWorldMatrix * bindposes[index]).MultiplyPoint3x4(vertices[i]) * amount;
                output[i] = Weighted(weight.boneIndex0, weight.weight0) + Weighted(weight.boneIndex1, weight.weight1)
                    + Weighted(weight.boneIndex2, weight.weight2) + Weighted(weight.boneIndex3, weight.weight3);
            }
            return output;
        }
        private static void AssertPoints(Vector3[] actual, Vector3[] expected, float tolerance = 0.0002f)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < actual.Length; i++) Assert.That(Vector3.Distance(actual[i], expected[i]), Is.LessThan(tolerance), "Vertex " + i + ": " + actual[i] + " vs " + expected[i]);
        }
    }
}
