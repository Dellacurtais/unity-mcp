using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Operations.SurfaceFit
{
    public sealed class SurfaceFitPoseSetEvaluation
    {
        public int SampleCount { get; internal set; }
        public int EvaluatedVertexCount { get; internal set; }
        public int BelowClearanceCount { get; internal set; }
        public float MinimumSignedClearance { get; internal set; } = float.PositiveInfinity;
        public float MeanSignedClearance { get; internal set; }
        public bool IsValid => SampleCount > 0 && EvaluatedVertexCount > 0 && BelowClearanceCount == 0;
    }

    public static class SurfaceFitPoseEvaluator
    {
        public static SurfaceFitPoseSetEvaluation EvaluateCurrentPose(
            Renderer source,
            IReadOnlyList<Renderer> targets,
            float requiredClearance,
            float maxDistance)
            => EvaluateSamples(source, targets, requiredClearance, maxDistance, null, null, 1);

        public static SurfaceFitPoseSetEvaluation EvaluateAnimationClip(
            GameObject characterRoot,
            Renderer source,
            IReadOnlyList<Renderer> targets,
            AnimationClip clip,
            int sampleCount,
            float requiredClearance,
            float maxDistance)
        {
            if (characterRoot == null) throw new ArgumentNullException(nameof(characterRoot));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            return EvaluateSamples(source, targets, requiredClearance, maxDistance, characterRoot, clip, Mathf.Clamp(sampleCount, 2, 128));
        }

        private static SurfaceFitPoseSetEvaluation EvaluateSamples(
            Renderer source,
            IReadOnlyList<Renderer> targets,
            float requiredClearance,
            float maxDistance,
            GameObject characterRoot,
            AnimationClip clip,
            int sampleCount)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var combined = new SurfaceFitPoseSetEvaluation { SampleCount = sampleCount };
            double weightedClearance = 0d;
            bool animation = clip != null;
            bool started = animation && !AnimationMode.InAnimationMode();
            if (started) AnimationMode.StartAnimationMode();
            try
            {
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    if (animation)
                    {
                        float time = clip.length <= 0f ? 0f : clip.length * sample / (sampleCount - 1f);
                        AnimationMode.BeginSampling();
                        AnimationMode.SampleAnimationClip(characterRoot, clip, time);
                        AnimationMode.EndSampling();
                    }
                    var report = EvaluateRenderer(source, targets, requiredClearance, maxDistance);
                    combined.EvaluatedVertexCount += report.EvaluatedVertexCount;
                    combined.BelowClearanceCount += report.BelowClearanceCount;
                    combined.MinimumSignedClearance = Mathf.Min(combined.MinimumSignedClearance, report.MinimumSignedClearance);
                    if (report.EvaluatedVertexCount > 0)
                        weightedClearance += report.MeanSignedClearance * report.EvaluatedVertexCount;
                }
                combined.MeanSignedClearance = combined.EvaluatedVertexCount > 0
                    ? (float)(weightedClearance / combined.EvaluatedVertexCount)
                    : float.PositiveInfinity;
                return combined;
            }
            finally
            {
                if (started && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            }
        }

        private static SurfaceFitEvaluation EvaluateRenderer(
            Renderer source,
            IReadOnlyList<Renderer> targets,
            float requiredClearance,
            float maxDistance)
        {
            Mesh mesh = null;
            bool temporary = false;
            if (source is SkinnedMeshRenderer skinned)
            {
                mesh = new Mesh { name = source.name + "_SurfaceFitPose" };
                skinned.BakeMesh(mesh);
                temporary = true;
            }
            else mesh = source.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) throw new InvalidOperationException("SOURCE_MESH_NOT_FOUND: source renderer has no readable mesh.");
            try
            {
                return SmartSurfaceFitOperation.Evaluate(
                    mesh,
                    source.transform.localToWorldMatrix,
                    targets,
                    requiredClearance,
                    maxDistance);
            }
            finally { if (temporary) UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
