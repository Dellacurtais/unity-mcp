using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    [Serializable]
    public sealed class RigClipValidationResult
    {
        public AnimationClip Clip;
        public int SampleCount;
        public int InvalidVertexCount;
        public int InvalidBoundsCount;
        public int BoundsContainmentFailureCount;
        public int InvalidBlendShapeWeightCount;
        public float MaximumBoundsMagnitude;
        public SplitPoseFidelityReport SplitFidelity;
        public readonly List<RigDiagnosticIssue> Issues = new List<RigDiagnosticIssue>();
        public bool IsValid => InvalidVertexCount == 0 && InvalidBoundsCount == 0 && BoundsContainmentFailureCount == 0 && InvalidBlendShapeWeightCount == 0
            && (SplitFidelity == null || SplitFidelity.IsValid)
            && Issues.All(issue => issue.Severity != RigDiagnosticSeverity.Error);

        internal JObject ToJson() => new JObject
        {
            ["clip"] = Clip != null ? Clip.name : string.Empty,
            ["length"] = Clip != null ? Clip.length : 0f,
            ["sampleCount"] = SampleCount,
            ["invalidVertexCount"] = InvalidVertexCount,
            ["invalidBoundsCount"] = InvalidBoundsCount,
            ["boundsContainmentFailureCount"] = BoundsContainmentFailureCount,
            ["invalidBlendShapeWeightCount"] = InvalidBlendShapeWeightCount,
            ["maximumBoundsMagnitude"] = MaximumBoundsMagnitude,
            ["valid"] = IsValid,
            ["splitFidelity"] = SplitFidelity == null ? null : new JObject
            {
                ["valid"] = SplitFidelity.IsValid,
                ["comparedVertexCount"] = SplitFidelity.ComparedVertexCount,
                ["missingVertexCount"] = SplitFidelity.MissingVertexCount,
                ["seamGapCount"] = SplitFidelity.SeamGapCount,
                ["maximumPositionError"] = SplitFidelity.MaximumPositionError,
                ["rootMeanSquareError"] = SplitFidelity.RootMeanSquareError,
            },
            ["issues"] = new JArray(Issues.Select(issue => issue.ToJson())),
        };
    }

    [Serializable]
    public sealed class RigClipValidationReport
    {
        public string TargetName;
        public string HierarchyHash;
        public string StateHash;
        public readonly List<RigClipValidationResult> Clips = new List<RigClipValidationResult>();
        public bool IsValid => Clips.Count > 0 && Clips.All(clip => clip.IsValid);
        public int ErrorCount => Clips.Sum(clip => clip.Issues.Count(issue => issue.Severity == RigDiagnosticSeverity.Error));

        public JObject ToJson() => new JObject
        {
            ["targetName"] = TargetName,
            ["hierarchyHash"] = HierarchyHash,
            ["stateHash"] = StateHash,
            ["valid"] = IsValid,
            ["clipCount"] = Clips.Count,
            ["errorCount"] = ErrorCount,
            ["clips"] = new JArray(Clips.Select(clip => clip.ToJson())),
        };
    }

    public static class RigClipValidationService
    {
        public static RigClipValidationReport Validate(
            GameObject selected,
            IReadOnlyList<AnimationClip> clips,
            int sampleCount = 12,
            GameObject splitPartsRoot = null,
            SmartBodySplitManifest splitManifest = null,
            float splitTolerance = 1e-5f)
        {
            var context = RigTargetResolver.Resolve(selected);
            if (!context.IsValid) throw new InvalidOperationException("RIG_TARGET_INVALID: no skinned character could be resolved.");
            if (clips == null || clips.Count == 0) throw new ArgumentException("CLIPS_REQUIRED: provide at least one AnimationClip.", nameof(clips));
            sampleCount = Mathf.Clamp(sampleCount, 2, 128);
            var report = new RigClipValidationReport
            {
                TargetName = context.Root.name,
                HierarchyHash = context.HierarchyHash,
                StateHash = context.StateHash,
            };
            var pose = RigPoseSnapshot.Capture(context.Root);
            bool started = !AnimationMode.InAnimationMode();
            if (started) AnimationMode.StartAnimationMode();
            try
            {
                for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
                {
                    var clip = clips[clipIndex];
                    var result = new RigClipValidationResult { Clip = clip, SampleCount = sampleCount };
                    report.Clips.Add(result);
                    if (clip == null)
                    {
                        result.Issues.Add(new RigDiagnosticIssue("CLIP_NULL", RigDiagnosticSeverity.Error, "A lista contém um clip nulo."));
                        continue;
                    }
                    try
                    {
                        var rigReport = RigInspectorService.Inspect(context);
                        foreach (var issue in rigReport.Issues.Where(value => value.Severity == RigDiagnosticSeverity.Error)) result.Issues.Add(issue);
                        ValidateClipSamples(context, clip, sampleCount, result);
                        if (splitPartsRoot != null && splitManifest != null)
                            result.SplitFidelity = SplitPoseFidelityValidator.ValidateAnimationClip(
                                context.Root,
                                context.Renderers[0],
                                splitPartsRoot,
                                splitManifest,
                                clip,
                                sampleCount,
                                splitTolerance);
                    }
                    catch (Exception exception)
                    {
                        result.Issues.Add(new RigDiagnosticIssue("CLIP_SAMPLE_FAILED", RigDiagnosticSeverity.Error, exception.Message, clip.name));
                    }
                    finally
                    {
                        pose.Restore();
                    }
                }
                return report;
            }
            finally
            {
                pose.Restore();
                if (started && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            }
        }

        public static void Export(RigClipValidationReport report, string path)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("REPORT_PATH_REQUIRED", nameof(path));
            string fullPath = Path.GetFullPath(path); string folder = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            File.WriteAllText(fullPath, report.ToJson().ToString(Newtonsoft.Json.Formatting.Indented));
        }

        private static void ValidateClipSamples(RigTargetContext context, AnimationClip clip, int sampleCount, RigClipValidationResult result)
        {
            var bake = new Mesh { name = clip.name + "_RigValidation" };
            try
            {
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    float time = clip.length <= 0f ? 0f : clip.length * sample / (sampleCount - 1f);
                    AnimationMode.BeginSampling();
                    try { AnimationMode.SampleAnimationClip(context.Root, clip, time); }
                    finally { AnimationMode.EndSampling(); }
                    for (int rendererIndex = 0; rendererIndex < context.Renderers.Length; rendererIndex++)
                    {
                        bake.Clear(false);
                        var renderer = context.Renderers[rendererIndex];
                        renderer.BakeMesh(bake);
                        var vertices = bake.vertices;
                        for (int vertex = 0; vertex < vertices.Length; vertex++)
                        {
                            if (!Finite(vertices[vertex])) { result.InvalidVertexCount++; continue; }
                            var allowed = renderer.localBounds; allowed.Expand(Mathf.Max(1e-5f, allowed.size.magnitude * 1e-4f));
                            if (!allowed.Contains(vertices[vertex])) result.BoundsContainmentFailureCount++;
                        }
                        var bounds = bake.bounds;
                        if (!Finite(bounds.center) || !Finite(bounds.size)) result.InvalidBoundsCount++;
                        else result.MaximumBoundsMagnitude = Mathf.Max(result.MaximumBoundsMagnitude, bounds.extents.magnitude);
                        var sourceMesh = renderer.sharedMesh;
                        for (int shape = 0; sourceMesh != null && shape < sourceMesh.blendShapeCount; shape++)
                            if (!Finite(renderer.GetBlendShapeWeight(shape))) result.InvalidBlendShapeWeightCount++;
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(bake); }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }

    internal sealed class RigPoseSnapshot
    {
        private readonly TransformState[] transforms;
        private readonly BlendShapeState[] blendShapes;

        private RigPoseSnapshot(TransformState[] transforms, BlendShapeState[] blendShapes)
        {
            this.transforms = transforms;
            this.blendShapes = blendShapes;
        }

        public static RigPoseSnapshot Capture(GameObject root)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            var transforms = new TransformState[all.Length];
            for (int i = 0; i < all.Length; i++) transforms[i] = new TransformState(all[i]);
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var shapes = new List<BlendShapeState>();
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                int count = renderer.sharedMesh != null ? renderer.sharedMesh.blendShapeCount : 0;
                for (int shape = 0; shape < count; shape++) shapes.Add(new BlendShapeState(renderer, shape, renderer.GetBlendShapeWeight(shape)));
            }
            return new RigPoseSnapshot(transforms, shapes.ToArray());
        }

        public void Restore()
        {
            for (int i = 0; i < transforms.Length; i++) transforms[i].Restore();
            for (int i = 0; i < blendShapes.Length; i++) blendShapes[i].Restore();
        }

        private readonly struct TransformState
        {
            private readonly Transform transform;
            private readonly Vector3 position;
            private readonly Quaternion rotation;
            private readonly Vector3 scale;
            public TransformState(Transform value)
            { transform = value; position = value.localPosition; rotation = value.localRotation; scale = value.localScale; }
            public void Restore()
            { if (transform != null) { transform.localPosition = position; transform.localRotation = rotation; transform.localScale = scale; } }
        }

        private readonly struct BlendShapeState
        {
            private readonly SkinnedMeshRenderer renderer;
            private readonly int index;
            private readonly float weight;
            public BlendShapeState(SkinnedMeshRenderer renderer, int index, float weight)
            { this.renderer = renderer; this.index = index; this.weight = weight; }
            public void Restore()
            { if (renderer != null && renderer.sharedMesh != null && index < renderer.sharedMesh.blendShapeCount) renderer.SetBlendShapeWeight(index, weight); }
        }
    }

    public sealed class RigClipPreviewSession : IDisposable
    {
        private readonly GameObject root;
        private readonly RigPoseSnapshot pose;
        private readonly bool started;
        public RigClipPreviewSession(GameObject root)
        {
            this.root = root != null ? root : throw new ArgumentNullException(nameof(root));
            pose = RigPoseSnapshot.Capture(root);
            started = !AnimationMode.InAnimationMode();
            if (started) AnimationMode.StartAnimationMode();
        }
        public void Sample(AnimationClip clip, float normalizedTime)
        {
            if (clip == null) return;
            pose.Restore();
            AnimationMode.BeginSampling();
            try { AnimationMode.SampleAnimationClip(root, clip, Mathf.Clamp01(normalizedTime) * clip.length); }
            finally { AnimationMode.EndSampling(); }
            SceneView.RepaintAll();
        }
        public void Dispose()
        {
            pose.Restore();
            if (started && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            SceneView.RepaintAll();
        }
    }
}
