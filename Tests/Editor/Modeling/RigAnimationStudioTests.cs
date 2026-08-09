using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Modeling.Rigging;
using Maicon.SceneAnalyzer.Modeling;
using Maicon.SceneAnalyzer.Editor.Mcp;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    public sealed class RigAnimationStudioTests
    {
        private const string TempFolder = "Assets/__RigAnimationStudioTests";
        private GameObject root;
        private readonly System.Collections.Generic.List<GameObject> extraRoots = new System.Collections.Generic.List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "__RigAnimationStudioTests");
            root = CreateCharacter(out _);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            for (int i = 0; i < extraRoots.Count; i++) if (extraRoots[i] != null) UnityEngine.Object.DestroyImmediate(extraRoots[i]);
            extraRoots.Clear();
            AssetDatabase.DeleteAsset(TempFolder);
            AnimationMode.StopAnimationMode();
        }

        [Test]
        public void InspectorAndHeatmap_PreserveMoreThanFourInfluences()
        {
            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>();
            var report = RigInspectorService.Inspect(root);
            Assert.That(report.VertexCount, Is.EqualTo(3));
            Assert.That(report.MaximumInfluences, Is.EqualTo(5));
            Assert.That(report.UnweightedVertexCount, Is.Zero);
            var heatmap = RigInspectorService.ComputeInfluenceHeatmap(renderer.sharedMesh, 0);
            Assert.That(heatmap.Length, Is.EqualTo(3));
            Assert.That(heatmap[0], Is.EqualTo(0.2f).Within(1e-5f));
        }

        [Test]
        public void Resolver_AcceptsCharacterRootRendererAndBone()
        {
            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>(); var bone = renderer.bones[2];
            Assert.That(RigTargetResolver.Resolve(root).Root, Is.EqualTo(root));
            Assert.That(RigTargetResolver.Resolve(renderer.gameObject).Root, Is.EqualTo(root));
            Assert.That(RigTargetResolver.Resolve(bone.gameObject).Root, Is.EqualTo(root));
        }

        [Test]
        public void ClipValidation_AlwaysRestoresPose()
        {
            var bone = root.transform.GetChild(0);
            bone.localPosition = new Vector3(0f, 1f, 0f);
            var clip = new AnimationClip();
            clip.SetCurve(bone.name, typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 2f));
            var report = RigClipValidationService.Validate(root, new[] { clip }, 3);
            Assert.That(report.Clips.Count, Is.EqualTo(1));
            Assert.That(bone.localPosition, Is.EqualTo(new Vector3(0f, 1f, 0f)));
            UnityEngine.Object.DestroyImmediate(clip);
        }

        [Test]
        public void WeightPainter_RequiresEditableAssetAndNormalizesAllVertices()
        {
            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.Throws<InvalidOperationException>(() => SkinWeightEditorService.ApplyRadialBrush(
                renderer, 0, Vector3.zero, new SkinWeightBrushSettings { Mode = SkinWeightPaintMode.Flood }));
            var mesh = RigAssetSafety.CreateEditableSkinCopy(renderer, TempFolder);
            var result = SkinWeightEditorService.ApplyRadialBrush(renderer, 1, Vector3.zero,
                new SkinWeightBrushSettings { Mode = SkinWeightPaintMode.Flood, Strength = 0.5f, MaximumInfluences = 8 });
            Assert.That(result.AffectedVertices, Is.EqualTo(mesh.vertexCount));
            var table = SkinWeightTable.Read(mesh);
            Assert.That(table.Vertices.All(value => Math.Abs(value.Values.Sum() - 1f) < 1e-5f), Is.True);
            Assert.That(table.Vertices.All(value => value.ContainsKey(1)), Is.True);
        }

        [Test]
        public void BindPoseAndWeightTransfer_MapCompleteSkeletonWithoutDroppingWeights()
        {
            var source = root.GetComponentInChildren<SkinnedMeshRenderer>();
            var destinationRoot = CreateCharacter(out var generatedMesh);
            extraRoots.Add(destinationRoot);
            var destination = destinationRoot.GetComponentInChildren<SkinnedMeshRenderer>();
            var editable = UnityEngine.Object.Instantiate(generatedMesh);
            AssetDatabase.CreateAsset(editable, TempFolder + "/TransferDestination.asset");
            destination.sharedMesh = editable;
            var report = SkinWeightTransferService.Transfer(source, destination);
            Assert.That(report.IsValid, Is.True);
            Assert.That(report.ExactVertexMatches, Is.EqualTo(editable.vertexCount));
            BindPoseService.Rebuild(destination);
            Assert.That(editable.bindposeCount, Is.EqualTo(destination.bones.Length));
            var table = SkinWeightTable.Read(editable);
            Assert.That(table.Vertices[0].Count, Is.EqualTo(5));
        }

        [Test]
        public void PoseLibrary_CapturesAndRestoresTransformPose()
        {
            var bone = root.transform.GetChild(0);
            bone.localRotation = Quaternion.Euler(10f, 20f, 30f);
            var pose = RigPoseLibraryService.Capture(root, "Aim");
            bone.localRotation = Quaternion.Euler(-30f, 0f, 0f);
            RigPoseLibraryService.Apply(root, pose);
            Assert.That(Quaternion.Angle(bone.localRotation, Quaternion.Euler(10f, 20f, 30f)), Is.LessThan(0.001f));
            Assert.That(pose.Transforms.Count, Is.EqualTo(root.GetComponentsInChildren<Transform>(true).Length));
        }

        [Test]
        public void ClipStudio_TrimCreatesStandaloneClipAndPreservesEvent()
        {
            var source = new AnimationClip { name = "SourceMotion", frameRate = 30f };
            source.SetCurve("Bone0", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0f, 0f, 2f, 2f));
            AnimationUtility.SetAnimationEvents(source, new[] { new AnimationEvent { time = 1f, functionName = "Footstep" } });
            var output = AnimationClipStudioService.Transform(source, new AnimationClipTransformSettings
            {
                Operation = AnimationClipTransformOperation.Trim, StartTime = 0.5f, EndTime = 1.5f, OutputFolder = TempFolder,
            });
            Assert.That(output.length, Is.EqualTo(1f).Within(0.001f));
            Assert.That(AnimationUtility.GetAnimationEvents(output).Single().time, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(AssetDatabase.GetAssetPath(output), Does.EndWith(".anim"));
            UnityEngine.Object.DestroyImmediate(source);
        }

        [Test]
        public void GenericRetarget_MapsByNameAppliesPoseAndBakesClip()
        {
            var destination = CreateCharacter(out _); extraRoots.Add(destination);
            var analysis = RigRetargetAnalyzer.Analyze(root, destination, TempFolder);
            Assert.That(analysis.IsComplete, Is.True);
            var sourceBone = root.transform.GetChild(0); var destinationBone = destination.transform.GetChild(0);
            sourceBone.localRotation = Quaternion.Euler(0f, 0f, 40f);
            RigRetargetService.ApplyPose(root, destination, analysis.Profile);
            Assert.That(Quaternion.Angle(sourceBone.localRotation, destinationBone.localRotation), Is.LessThan(0.001f));
            sourceBone.localRotation = Quaternion.identity; destinationBone.localRotation = Quaternion.identity;
            var clip = new AnimationClip { name = "GenericTurn" };
            clip.SetCurve("Bone0", typeof(Transform), "m_LocalRotation.z", AnimationCurve.Linear(0f, 0f, 1f, 0.5f));
            clip.SetCurve("Bone0", typeof(Transform), "m_LocalRotation.w", AnimationCurve.Linear(0f, 1f, 1f, 0.8660254f));
            var output = RigRetargetService.RetargetClip(root, destination, analysis.Profile, clip, 10f, TempFolder);
            Assert.That(AnimationUtility.GetCurveBindings(output).Length, Is.GreaterThanOrEqualTo(4));
            Assert.That(sourceBone.localRotation, Is.EqualTo(Quaternion.identity));
            Assert.That(destinationBone.localRotation, Is.EqualTo(Quaternion.identity));
            UnityEngine.Object.DestroyImmediate(clip);
        }

        [Test]
        public void NativeTwoBoneIk_ReducesTipErrorAndBakeRestoresPose()
        {
            var chainRoot = root.transform.GetChild(0); var mid = chainRoot.GetChild(0); var tip = mid.GetChild(0);
            var binding = RigIkService.Create(root, chainRoot, mid, tip, RigIkSolverType.TwoBone);
            binding.Target.position = chainRoot.position + new Vector3(0.15f, 0.28f, 0f);
            binding.Hint.position = chainRoot.position + Vector3.forward;
            float before = Vector3.Distance(tip.position, binding.Target.position);
            NativeRigIkSolver.Solve(binding);
            float after = Vector3.Distance(tip.position, binding.Target.position);
            Assert.That(after, Is.LessThan(before));
            var originalRootRotation = chainRoot.localRotation;
            var clip = new AnimationClip { name = "IdleForIk" };
            clip.SetCurve("Bone0", typeof(Transform), "m_LocalRotation.w", AnimationCurve.Constant(0f, 1f, chainRoot.localRotation.w));
            var output = RigIkService.Bake(root, binding, clip, 10f, TempFolder);
            Assert.That(AnimationUtility.GetCurveBindings(output).Length, Is.GreaterThanOrEqualTo(8));
            Assert.That(chainRoot.localRotation, Is.EqualTo(originalRootRotation));
            UnityEngine.Object.DestroyImmediate(clip);
        }

        [Test]
        public void AutoRig_RequiresExactHashesAndProducesNormalizedEditableSkin()
        {
            var staticObject = GameObject.CreatePrimitive(PrimitiveType.Cube); staticObject.name = "StaticHumanoidCandidate";
            staticObject.transform.localScale = new Vector3(1.2f, 2.2f, 0.6f); extraRoots.Add(staticObject);
            var proposal = HumanoidAutoRigAnalyzer.Analyze(staticObject);
            Assert.That(proposal.Landmarks.Count, Is.EqualTo(19));
            Assert.Throws<InvalidOperationException>(() => HumanoidAutoRigService.Apply(proposal, "wrong", proposal.MeshHash, TempFolder));
            var result = HumanoidAutoRigService.Apply(proposal, proposal.ProposalHash, proposal.MeshHash, TempFolder);
            Assert.That(result.BoneCount, Is.EqualTo(19));
            Assert.That(RigAssetSafety.IsEditableStandaloneMesh(result.Renderer.sharedMesh), Is.True);
            var table = SkinWeightTable.Read(result.Renderer.sharedMesh);
            Assert.That(table.Vertices.All(value => Math.Abs(value.Values.Sum() - 1f) < 1e-5f), Is.True);
            Assert.That(RigInspectorService.Inspect(result.Renderer.gameObject).IsValid, Is.True);
        }

        [Test]
        public void RigMcpContracts_RegisterAllNineteenTools()
        {
            var registry = new McpToolRegistry();
            registry.RegisterRange(new IMcpTool[]
            {
                new RigInspectTool(), new RigValidateClipsTool(), new RigCreateEditableSkinTool(), new RigApplyWeightOperationTool(),
                new RigRebuildBindPosesTool(), new RigTransferWeightsTool(), new RigEditSkeletonTool(), new AnimationCapturePoseTool(),
                new AnimationApplyPoseTool(), new AnimationTransformClipTool(), new RigAnalyzeRetargetTool(), new RigApplyRetargetPoseTool(),
                new RigRetargetClipTool(), new RigCreateIkTool(), new RigPreviewIkTool(), new RigBakeIkClipTool(),
                new RigAnalyzeAutoRigTool(), new RigPreviewAutoRigTool(), new RigApplyAutoRigTool(),
            });
            Assert.That(registry.Count, Is.EqualTo(19));
            Assert.That(registry.All.Count(tool => tool.IsWrite), Is.EqualTo(15));
        }

        [Test]
        public void RigContext_StateHashChangesWithWeightsAndBindPoses()
        {
            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>(); RigAssetSafety.CreateEditableSkinCopy(renderer, TempFolder);
            var before = RigTargetResolver.Resolve(root);
            SkinWeightEditorService.ApplyRadialBrush(renderer, 1, Vector3.zero, new SkinWeightBrushSettings { Mode = SkinWeightPaintMode.Flood, Strength = .25f });
            var afterWeights = RigTargetResolver.Resolve(root);
            Assert.That(afterWeights.SkinWeightHash, Is.Not.EqualTo(before.SkinWeightHash)); Assert.That(afterWeights.StateHash, Is.Not.EqualTo(before.StateHash));
            Undo.RegisterCompleteObjectUndo(renderer.sharedMesh, "Test bind pose hash"); var poses = renderer.sharedMesh.bindposes; poses[0].m03 += .01f; renderer.sharedMesh.bindposes = poses;
            var afterBind = RigTargetResolver.Resolve(root); Assert.That(afterBind.BindPoseHash, Is.Not.EqualTo(afterWeights.BindPoseHash));
        }

        [Test]
        public void EditableSkinCopy_UndoRestoresRendererAndRemovesCreatedAsset()
        {
            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>(); var original = renderer.sharedMesh;
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); var copy = RigAssetSafety.CreateEditableSkinCopy(renderer, TempFolder); string path = AssetDatabase.GetAssetPath(copy); Undo.CollapseUndoOperations(group);
            Assert.That(renderer.sharedMesh, Is.EqualTo(copy)); Undo.PerformUndo();
            Assert.That(renderer.sharedMesh, Is.EqualTo(original)); Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.Null);
        }

        [Test]
        public void WeightPainter_RespectsLocksAndRegion()
        {
            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>(); var mesh = RigAssetSafety.CreateEditableSkinCopy(renderer, TempFolder); var before = SkinWeightTable.Read(mesh);
            SkinWeightEditorService.ApplyRadialBrush(renderer, 1, Vector3.zero, new SkinWeightBrushSettings { Mode = SkinWeightPaintMode.Flood, Strength = .5f }, null, null, null, new System.Collections.Generic.HashSet<int> { 1 });
            var region = SkinWeightTable.Read(mesh); Assert.That(region.Vertices[0][0], Is.EqualTo(before.Vertices[0][0]).Within(1e-6f)); Assert.That(region.Vertices[1].ContainsKey(1), Is.True);
            float locked = region.Vertices[1][0]; SkinWeightEditorService.ApplyRadialBrush(renderer, 0, Vector3.zero, new SkinWeightBrushSettings { Mode = SkinWeightPaintMode.Subtract, Strength = 1f }, new System.Collections.Generic.HashSet<int> { 0 }, null, null, new System.Collections.Generic.HashSet<int> { 1 });
            Assert.That(SkinWeightTable.Read(mesh).Vertices[1][0], Is.EqualTo(locked).Within(1e-6f));
        }

        [Test]
        public void WeightPainterSpatialIndex_RaycastsAndQueriesWithoutScanningTheWholeMesh()
        {
            CreateGrid(64, out var vertices, out var triangles);
            var index = new SkinWeightSpatialIndex(vertices, triangles);
            Assert.That(index.Raycast(new Ray(new Vector3(31.5f, 31.5f, -10f), Vector3.forward), false, out float distance, out var normal), Is.True);
            Assert.That(distance, Is.EqualTo(10f).Within(1e-4f)); Assert.That(normal.sqrMagnitude, Is.GreaterThan(.9f));
            Assert.That(index.LastTriangleTests, Is.LessThan(index.TriangleCount));
            var nearby = index.QuerySphere(new Vector3(31.5f, 31.5f, 0f), 1.6f);
            Assert.That(nearby.Length, Is.GreaterThan(0)); Assert.That(nearby.Length, Is.LessThan(index.VertexCount / 10));
            Debug.Log($"RIG_WEIGHT_SPATIAL_METRIC triangles={index.TriangleCount} triangleTests={index.LastTriangleTests} vertices={index.VertexCount} brushCandidates={nearby.Length}");
        }

        [Test]
        public void BindPoseSnapshot_CapturesAndRestoresStandaloneMesh()
        {
            var renderer = root.GetComponentInChildren<SkinnedMeshRenderer>(); RigAssetSafety.CreateEditableSkinCopy(renderer, TempFolder);
            var snapshot = BindPoseService.Capture(renderer, root, TempFolder); var expected = renderer.sharedMesh.bindposes;
            var changed = (Matrix4x4[])expected.Clone(); changed[0].m03 += .25f; renderer.sharedMesh.bindposes = changed;
            BindPoseService.Restore(renderer, root, snapshot); Assert.That(renderer.sharedMesh.bindposes[0], Is.EqualTo(expected[0]));
        }

        [Test]
        public void ClipStudio_AdvancedOperationsMirrorMergeAdditiveAndRemap()
        {
            var source = new AnimationClip { name = "AdvancedSource" }; source.SetCurve("LeftArm", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0f, 1f, 1f, 2f));
            var mirror = AnimationClipStudioService.Transform(source, new AnimationClipTransformSettings { Operation = AnimationClipTransformOperation.Mirror, OutputFolder = TempFolder });
            var binding = AnimationUtility.GetCurveBindings(mirror).Single(value => value.propertyName == "m_LocalPosition.x"); Assert.That(binding.path, Is.EqualTo("RightArm")); Assert.That(AnimationUtility.GetEditorCurve(mirror, binding).Evaluate(0f), Is.EqualTo(-1f).Within(1e-5f));
            var reference = new AnimationClip(); reference.SetCurve("LeftArm", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Constant(0f, 1f, .5f));
            var additive = AnimationClipStudioService.Transform(source, new AnimationClipTransformSettings { Operation = AnimationClipTransformOperation.AdditiveReference, ReferenceClip = reference, OutputFolder = TempFolder });
            var additiveBinding = AnimationUtility.GetCurveBindings(additive).Single(value => value.propertyName == "m_LocalPosition.x"); Assert.That(AnimationUtility.GetEditorCurve(additive, additiveBinding).Evaluate(0f), Is.EqualTo(.5f).Within(1e-5f));
            var merged = AnimationClipStudioService.Transform(source, new AnimationClipTransformSettings { Operation = AnimationClipTransformOperation.MergeSequential, AdditionalClips = new System.Collections.Generic.List<AnimationClip> { source }, OutputFolder = TempFolder }); Assert.That(merged.length, Is.EqualTo(2f).Within(.001f));
            var remapped = AnimationClipStudioService.Transform(source, new AnimationClipTransformSettings { Operation = AnimationClipTransformOperation.CopyRemapCurves, SourcePathPrefix = "Left", DestinationPathPrefix = "Custom", OutputFolder = TempFolder }); Assert.That(AnimationUtility.GetCurveBindings(remapped).Where(value => value.propertyName == "m_LocalPosition.x").All(value => value.path == "CustomArm"), Is.True);
            UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(reference);
        }

        [Test]
        public void ClipStudio_MirrorsHumanoidMusclesAndRootMotionAxes()
        {
            var source = new AnimationClip { name = "HumanoidMirror" };
            AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "Left Arm Down-Up"), AnimationCurve.Constant(0f, 1f, .5f));
            AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "Spine Left-Right"), AnimationCurve.Constant(0f, 1f, .25f));
            AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "RootT.x"), AnimationCurve.Constant(0f, 1f, 2f));
            AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "RootQ.y"), AnimationCurve.Constant(0f, 1f, .2f));
            var mirrored = AnimationClipStudioService.Transform(source, new AnimationClipTransformSettings { Operation = AnimationClipTransformOperation.Mirror, OutputFolder = TempFolder });
            var bindings = AnimationUtility.GetCurveBindings(mirrored);
            Assert.That(bindings.Any(value => value.propertyName == "Right Arm Down-Up"), Is.True);
            Assert.That(AnimationUtility.GetEditorCurve(mirrored, bindings.Single(value => value.propertyName == "Right Arm Down-Up")).Evaluate(.5f), Is.EqualTo(.5f).Within(1e-5f));
            Assert.That(AnimationUtility.GetEditorCurve(mirrored, bindings.Single(value => value.propertyName == "Spine Left-Right")).Evaluate(.5f), Is.EqualTo(-.25f).Within(1e-5f));
            Assert.That(AnimationUtility.GetEditorCurve(mirrored, bindings.Single(value => value.propertyName == "RootT.x")).Evaluate(.5f), Is.EqualTo(-2f).Within(1e-5f));
            Assert.That(AnimationUtility.GetEditorCurve(mirrored, bindings.Single(value => value.propertyName == "RootQ.y")).Evaluate(.5f), Is.EqualTo(-.2f).Within(1e-5f));
            UnityEngine.Object.DestroyImmediate(source);
        }

        [Test]
        public void ClipStudio_AdditiveQuaternionUsesRotationDeltaInsteadOfComponentSubtraction()
        {
            var source = new AnimationClip { name = "QuaternionSource" }; var reference = new AnimationClip { name = "QuaternionReference" };
            Quaternion referenceRotation = Quaternion.Euler(0f, 35f, 0f); Quaternion sourceRotation = Quaternion.Euler(20f, 70f, -10f);
            SetQuaternionCurves(source, "Bone", referenceRotation, sourceRotation); SetQuaternionCurves(reference, "Bone", referenceRotation, referenceRotation);
            var additive = AnimationClipStudioService.Transform(source, new AnimationClipTransformSettings { Operation = AnimationClipTransformOperation.AdditiveReference, ReferenceClip = reference, OutputFolder = TempFolder });
            var bindings = AnimationUtility.GetCurveBindings(additive).Where(value => value.path == "Bone" && value.propertyName.StartsWith("m_LocalRotation.", StringComparison.Ordinal)).ToArray();
            Quaternion actual = ReadQuaternion(additive, bindings, 1f); Quaternion expected = Quaternion.Inverse(referenceRotation) * sourceRotation;
            Assert.That(Quaternion.Angle(actual, expected), Is.LessThan(.1f));
            Assert.That(Quaternion.Angle(ReadQuaternion(additive, bindings, 0f), Quaternion.identity), Is.LessThan(.1f));
            UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(reference);
        }

        [Test]
        public void RetargetProfile_InvalidatesWhenSkinStateChanges()
        {
            var destination = CreateCharacter(out _); extraRoots.Add(destination); var analysis = RigRetargetAnalyzer.Analyze(root, destination, TempFolder);
            var renderer = destination.GetComponentInChildren<SkinnedMeshRenderer>(); RigAssetSafety.CreateEditableSkinCopy(renderer, TempFolder);
            SkinWeightEditorService.ApplyRadialBrush(renderer, 1, Vector3.zero, new SkinWeightBrushSettings { Mode = SkinWeightPaintMode.Flood, Strength = .2f });
            Assert.Throws<InvalidOperationException>(() => RigRetargetService.ApplyPose(root, destination, analysis.Profile));
        }

        [Test]
        public void IkPreview_DoesNotAccumulateDriftAndFabrikSupportsFullChain()
        {
            var chainRoot = root.transform.GetChild(0); var mid = chainRoot.GetChild(0); var third = mid.GetChild(0); var tip = third.GetChild(0);
            var binding = RigIkService.Create(root, chainRoot, mid, tip, RigIkSolverType.Fabrik); binding.Chain = new System.Collections.Generic.List<Transform> { chainRoot, mid, third, tip }; NativeRigIkSolver.CaptureRestPose(binding); binding.Target.position = tip.position + Vector3.right * .15f;
            NativeRigIkSolver.Solve(binding); var rotations = binding.Chain.Select(value => value.localRotation).ToArray(); NativeRigIkSolver.Solve(binding);
            for (int i = 0; i < rotations.Length; i++) Assert.That(Quaternion.Angle(rotations[i], binding.Chain[i].localRotation), Is.LessThan(.001f));
        }

        [TestCase(RigIkSolverType.TwoBone, "TwoBoneIKConstraint")]
        [TestCase(RigIkSolverType.Aim, "MultiAimConstraint")]
        [TestCase(RigIkSolverType.Fabrik, "ChainIKConstraint")]
        public void AnimationRiggingAdapter_CreatesValidatedEquivalentConstraint(RigIkSolverType solver, string expectedType)
        {
            if (!AnimationRiggingAdapter.IsAvailable) Assert.Ignore("com.unity.animation.rigging is optional and not installed in this test project.");
            var bones = root.GetComponentInChildren<SkinnedMeshRenderer>().bones;
            var binding = RigIkService.Create(root, bones[0], bones[1], bones[3], solver);
            Assert.That(AnimationRiggingAdapter.TryCreateConstraint(binding, out var constraint, out string message), Is.True, message);
            Assert.That(constraint, Is.Not.Null); Assert.That(constraint.GetType().Name, Is.EqualTo(expectedType));
            Assert.That((bool)constraint.GetType().GetMethod("IsValid").Invoke(constraint, null), Is.True);
            var builder = root.GetComponents<Component>().First(value => value != null && value.GetType().FullName == "UnityEngine.Animations.Rigging.RigBuilder");
            var layers = builder.GetType().GetProperty("layers").GetValue(builder) as System.Collections.IList;
            Assert.That(layers, Is.Not.Null); Assert.That(layers.Count, Is.GreaterThan(0));
        }

        [Test]
        public void AutoRig_BlocksIndividualLowConfidenceAndHashesConfidenceReview()
        {
            var candidate = GameObject.CreatePrimitive(PrimitiveType.Cube); extraRoots.Add(candidate); var proposal = HumanoidAutoRigAnalyzer.Analyze(candidate); string before = proposal.ProposalHash;
            var request = RigAiProposalService.BuildRequest(candidate, new[] { "Assets/Front.png" });
            Assert.That((string)request["meshHash"], Is.EqualTo(proposal.MeshHash)); Assert.That((bool)request["assetPayloadEmbedded"], Is.False);
            Assert.That(((JArray)request["requiredLandmarks"]).Count, Is.EqualTo(19)); Assert.That((bool)request["consentRequiredBeforeExternalSend"], Is.True);
            proposal.Landmarks.First(value => value.Id == "Head").Confidence = .1f; proposal.Rehash();
            Assert.That(proposal.CanApply, Is.False); Assert.That(proposal.BlockingLandmarks, Does.Contain("Head")); Assert.That(proposal.ProposalHash, Is.Not.EqualTo(before));
            Assert.Throws<InvalidOperationException>(() => HumanoidAutoRigService.Apply(proposal, proposal.ProposalHash, proposal.MeshHash, TempFolder));
        }

        private static void SetQuaternionCurves(AnimationClip clip, string path, Quaternion start, Quaternion end)
        {
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", AnimationCurve.Linear(0f, start.x, 1f, end.x));
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", AnimationCurve.Linear(0f, start.y, 1f, end.y));
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", AnimationCurve.Linear(0f, start.z, 1f, end.z));
            clip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", AnimationCurve.Linear(0f, start.w, 1f, end.w));
        }

        private static Quaternion ReadQuaternion(AnimationClip clip, EditorCurveBinding[] bindings, float time)
        {
            float Component(string suffix) => AnimationUtility.GetEditorCurve(clip, bindings.Single(value => value.propertyName.EndsWith(suffix, StringComparison.Ordinal))).Evaluate(time);
            var value = new Quaternion(Component(".x"), Component(".y"), Component(".z"), Component(".w"));
            float magnitude = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            return new Quaternion(value.x / magnitude, value.y / magnitude, value.z / magnitude, value.w / magnitude);
        }

        private static void CreateGrid(int resolution, out Vector3[] vertices, out int[] triangles)
        {
            int side = resolution + 1; vertices = new Vector3[side * side];
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) vertices[y * side + x] = new Vector3(x, y, 0f);
            triangles = new int[resolution * resolution * 6]; int index = 0;
            for (int y = 0; y < resolution; y++) for (int x = 0; x < resolution; x++)
            {
                int a = y * side + x, b = a + 1, c = a + side, d = c + 1;
                triangles[index++] = a; triangles[index++] = b; triangles[index++] = d;
                triangles[index++] = a; triangles[index++] = d; triangles[index++] = c;
            }
        }

        private static GameObject CreateCharacter(out Mesh mesh)
        {
            var root = new GameObject("RigTestCharacter");
            var bones = new Transform[5];
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = new GameObject("Bone" + i).transform;
                bone.SetParent(i == 0 ? root.transform : bones[i - 1], false);
                bone.localPosition = new Vector3(0f, i == 0 ? 0f : 0.2f, 0f);
                bones[i] = bone;
            }
            var skinObject = new GameObject("Skin");
            skinObject.transform.SetParent(root.transform, false);
            var renderer = skinObject.AddComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "RigTestMesh" };
            mesh.vertices = new[] { new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = bones.Select(bone => bone.worldToLocalMatrix * skinObject.transform.localToWorldMatrix).ToArray();
            var counts = new byte[] { 5, 1, 1 };
            var weights = new[]
            {
                new BoneWeight1 { boneIndex = 0, weight = .2f }, new BoneWeight1 { boneIndex = 1, weight = .2f },
                new BoneWeight1 { boneIndex = 2, weight = .2f }, new BoneWeight1 { boneIndex = 3, weight = .2f },
                new BoneWeight1 { boneIndex = 4, weight = .2f }, new BoneWeight1 { boneIndex = 0, weight = 1f },
                new BoneWeight1 { boneIndex = 0, weight = 1f },
            };
            using (var nativeCounts = new NativeArray<byte>(counts, Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(weights, Allocator.Temp)) mesh.SetBoneWeights(nativeCounts, nativeWeights);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            renderer.sharedMesh = mesh; renderer.bones = bones; renderer.rootBone = bones[0];
            return root;
        }
    }
}
