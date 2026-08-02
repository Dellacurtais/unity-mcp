using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit
{
    public sealed class SplitPoseFidelityReport
    {
        public int SampleCount { get; internal set; }
        public int ComparedVertexCount { get; internal set; }
        public int MissingVertexCount { get; internal set; }
        public int SeamGapCount { get; internal set; }
        public float MaximumPositionError { get; internal set; }
        public float RootMeanSquareError { get; internal set; }
        public bool IsValid { get; internal set; }
        public string Message { get; internal set; }
    }

    public static class SplitPoseFidelityValidator
    {
        public static SplitPoseFidelityReport ValidateCurrentPose(
            SkinnedMeshRenderer source,
            GameObject partsRoot,
            SmartBodySplitManifest manifest,
            float tolerance = 1e-5f)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (partsRoot == null) throw new ArgumentNullException(nameof(partsRoot));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var report = EvaluatePose(source, partsRoot, manifest, tolerance);
            report.SampleCount = 1;
            Finalize(report, tolerance);
            return report;
        }

        public static SplitPoseFidelityReport ValidateAnimationClip(
            GameObject characterRoot,
            SkinnedMeshRenderer source,
            GameObject partsRoot,
            SmartBodySplitManifest manifest,
            AnimationClip clip,
            int sampleCount = 8,
            float tolerance = 1e-5f)
        {
            if (characterRoot == null) throw new ArgumentNullException(nameof(characterRoot));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            sampleCount = Mathf.Clamp(sampleCount, 2, 128);
            var combined = new SplitPoseFidelityReport { SampleCount = sampleCount };
            bool started = !AnimationMode.InAnimationMode();
            if (started) AnimationMode.StartAnimationMode();
            try
            {
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    float time = clip.length <= 0f ? 0f : clip.length * sample / (sampleCount - 1f);
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(characterRoot, clip, time);
                    AnimationMode.EndSampling();
                    var report = EvaluatePose(source, partsRoot, manifest, tolerance);
                    combined.ComparedVertexCount += report.ComparedVertexCount;
                    combined.MissingVertexCount += report.MissingVertexCount;
                    combined.SeamGapCount += report.SeamGapCount;
                    combined.MaximumPositionError = Mathf.Max(combined.MaximumPositionError, report.MaximumPositionError);
                    combined.RootMeanSquareError += report.RootMeanSquareError * report.RootMeanSquareError;
                }
                combined.RootMeanSquareError = Mathf.Sqrt(combined.RootMeanSquareError / sampleCount);
                Finalize(combined, tolerance);
                return combined;
            }
            finally
            {
                if (started && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
            }
        }

        private static SplitPoseFidelityReport EvaluatePose(
            SkinnedMeshRenderer source,
            GameObject partsRoot,
            SmartBodySplitManifest manifest,
            float tolerance)
        {
            var sourceBake = new Mesh();
            var partBake = new Mesh();
            try
            {
                source.BakeMesh(sourceBake);
                var sourceVertices = sourceBake.vertices;
                var accumulated = new Vector3[sourceVertices.Length];
                var counts = new int[sourceVertices.Length];
                var partRenderers = partsRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var rendererByMesh = new Dictionary<Mesh, SkinnedMeshRenderer>();
                for (int i = 0; i < partRenderers.Length; i++)
                    if (partRenderers[i] != null && partRenderers[i].sharedMesh != null)
                        rendererByMesh[partRenderers[i].sharedMesh] = partRenderers[i];
                var report = new SplitPoseFidelityReport();
                double squaredError = 0d;
                for (int partIndex = 0; partIndex < manifest.Parts.Count; partIndex++)
                {
                    var part = manifest.Parts[partIndex];
                    if (!rendererByMesh.TryGetValue(part.Mesh, out var renderer)) continue;
                    partBake.Clear(false);
                    renderer.BakeMesh(partBake);
                    var vertices = partBake.vertices;
                    if (vertices.Length != part.SourceVertexIndices.Count) continue;
                    for (int local = 0; local < vertices.Length; local++)
                    {
                        int sourceIndex = part.SourceVertexIndices[local];
                        if (sourceIndex < 0 || sourceIndex >= sourceVertices.Length) continue;
                        var world = renderer.transform.TransformPoint(vertices[local]);
                        if (counts[sourceIndex] > 0)
                        {
                            var previous = accumulated[sourceIndex] / counts[sourceIndex];
                            if (Vector3.Distance(previous, world) > tolerance) report.SeamGapCount++;
                        }
                        accumulated[sourceIndex] += world;
                        counts[sourceIndex]++;
                    }
                }
                for (int sourceIndex = 0; sourceIndex < sourceVertices.Length; sourceIndex++)
                {
                    if (counts[sourceIndex] == 0)
                    {
                        report.MissingVertexCount++;
                        continue;
                    }
                    var expected = source.transform.TransformPoint(sourceVertices[sourceIndex]);
                    var actual = accumulated[sourceIndex] / counts[sourceIndex];
                    float error = Vector3.Distance(expected, actual);
                    report.MaximumPositionError = Mathf.Max(report.MaximumPositionError, error);
                    squaredError += error * error;
                    report.ComparedVertexCount++;
                }
                report.RootMeanSquareError = report.ComparedVertexCount > 0
                    ? Mathf.Sqrt((float)(squaredError / report.ComparedVertexCount))
                    : float.PositiveInfinity;
                return report;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sourceBake);
                UnityEngine.Object.DestroyImmediate(partBake);
            }
        }

        private static void Finalize(SplitPoseFidelityReport report, float tolerance)
        {
            report.IsValid = report.ComparedVertexCount > 0
                && report.MissingVertexCount == 0
                && report.SeamGapCount == 0
                && report.MaximumPositionError <= tolerance;
            report.Message = report.IsValid
                ? $"Split fidelity passed {report.SampleCount} pose sample(s); max error {report.MaximumPositionError:R}."
                : $"Split fidelity failed: missing={report.MissingVertexCount}, gaps={report.SeamGapCount}, maxError={report.MaximumPositionError:R}.";
        }
    }
}
