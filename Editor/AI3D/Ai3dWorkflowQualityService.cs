using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Modeling.Rigging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    internal sealed class Ai3dWorkflowQualityEvaluation
    {
        public JObject Report;
        public readonly List<Ai3dCandidateIssue> Issues = new List<Ai3dCandidateIssue>();
        public bool AcceptAllowed => Issues.All(value => value.Severity != Ai3dIssueSeverity.Error);
    }

    internal static class Ai3dWorkflowQualityService
    {
        public static Ai3dWorkflowQualityEvaluation Evaluate(
            Ai3dWorkflowRecord record, Ai3dStagedCandidate candidate)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            var result = new Ai3dWorkflowQualityEvaluation();
            var source = Capture(record.SourceAssetPath);
            var output = Capture(candidate.StagedAssetPath);
            result.Report = new JObject
            {
                ["workflowKind"] = record.WorkflowKind,
                ["source"] = source,
                ["candidate"] = output,
                ["planHash"] = record.PlanHash,
                ["candidateHash"] = candidate.SourceArtifactHash,
                ["validationHash"] = candidate.ValidationHash,
            };

            switch (record.WorkflowKind)
            {
                case "retopology": EvaluateRetopology(record, source, output, result); break;
                case "segment_complete": EvaluateSegmentation(source, output, result); break;
                case "rig_animation": EvaluateRigAndAnimation(record, candidate, output, result); break;
            }

            result.Report["acceptAllowed"] = result.AcceptAllowed;
            result.Report["issues"] = IssuesJson(result.Issues);
            result.Report["qualityHash"] = Ai3dGatewaySettings.Sha256(
                result.Report.ToString(Formatting.None));
            return result;
        }

        private static void EvaluateRetopology(
            Ai3dWorkflowRecord record, JObject source, JObject output,
            Ai3dWorkflowQualityEvaluation result)
        {
            var node = Ai3dWorkflowNodePlan.ParseMany(record.PipelineNodes)
                .FirstOrDefault(value => value.Operation == "retopology");
            var limit = (int?)node?.Payload?["face_limit"] ?? 0;
            var sourceAvailable = (bool?)source["available"] == true;
            if (!sourceAvailable)
                Error(result, "AI3D_AB_SOURCE_UNAVAILABLE",
                    "Retopology acceptance requires the source model to be importable under Assets for A/B comparison.");
            var triangles = (int?)output["triangleCount"] ?? 0;
            if (limit <= 0 || triangles > limit)
                Error(result, "AI3D_TRIANGLE_BUDGET_EXCEEDED",
                    $"Candidate has {triangles} triangles; reviewed limit is {limit}.");
            if (sourceAvailable)
            {
                var sourceTriangles = Math.Max(1, (int?)source["triangleCount"] ?? 1);
                result.Report["triangleRatio"] = triangles / (double)sourceTriangles;
                if (triangles >= sourceTriangles)
                    Warning(result, "AI3D_RETOPOLOGY_NO_REDUCTION",
                        "Candidate did not reduce the source triangle count.");
                var sourceMagnitude = Math.Max(1e-6f, (float?)source["boundsMagnitude"] ?? 0f);
                var candidateMagnitude = (float?)output["boundsMagnitude"] ?? 0f;
                var relative = Math.Abs(candidateMagnitude - sourceMagnitude) / sourceMagnitude;
                result.Report["boundsRelativeError"] = relative;
                if (relative > 0.25f)
                    Warning(result, "AI3D_BOUNDS_DRIFT",
                        "Candidate bounds changed by more than 25%; inspect scale and silhouette side by side.");
            }
            result.Report["silhouetteReviewRequired"] = true;
            result.Report["silhouetteReviewHash"] = null;
            result.Report["recommendedLocalTools"] = new JArray
            {
                "ai3d_preview_candidate", "screenshot_scene_view", "mesh_validate",
            };
        }

        private static void EvaluateSegmentation(
            JObject source, JObject output, Ai3dWorkflowQualityEvaluation result)
        {
            var rendererCount = (int?)output["rendererCount"] ?? 0;
            var meshCount = (int?)output["meshCount"] ?? 0;
            if (Math.Max(rendererCount, meshCount) < 2)
                Error(result, "AI3D_SEGMENTATION_NOT_SEPARATED",
                    "Segmented candidate does not expose at least two independently reviewable mesh/render parts.");
            if ((bool?)source["available"] != true)
            {
                Error(result, "AI3D_CHANNEL_SOURCE_UNAVAILABLE",
                    "Source is outside Unity Assets; rig/blend-shape preservation cannot be proven and acceptance is blocked.");
                result.Report["smartBodySplitRequired"] = true;
                return;
            }
            var sourceSkinned = (int?)source["skinnedRendererCount"] ?? 0;
            if (sourceSkinned <= 0) return;
            result.Report["sourceWasRigged"] = true;
            if ((bool?)source["meshChannelsReadable"] != true)
                Error(result, "AI3D_SOURCE_CHANNELS_NOT_READABLE",
                    "Source rig channels are not readable. Enable Read/Write on the source importer before proving preservation.");
            var outputSkinned = (int?)output["skinnedRendererCount"] ?? 0;
            if (outputSkinned < sourceSkinned)
                Error(result, "AI3D_SKIN_RENDERER_LOSS",
                    "Remote segmentation removed one or more skinned renderers. Use the local Smart Body Split path.");
            if (((int?)output["weightedVertexCount"] ?? 0) < ((int?)source["weightedVertexCount"] ?? 0))
                Error(result, "AI3D_SKIN_WEIGHT_LOSS",
                    "Remote segmentation reduced weighted vertices. The original rig cannot be accepted silently.");
            if (((int?)output["bindPoseCount"] ?? 0) < ((int?)source["bindPoseCount"] ?? 0))
                Error(result, "AI3D_BIND_POSE_LOSS",
                    "Remote segmentation lost bind poses. Use Smart Body Split to preserve the original rig.");
            if (((int?)output["blendShapeCount"] ?? 0) < ((int?)source["blendShapeCount"] ?? 0))
                Error(result, "AI3D_BLEND_SHAPE_LOSS",
                    "Remote segmentation lost blend shapes. Use the local exact split workflow.");
            result.Report["smartBodySplitRequired"] = !result.AcceptAllowed;
            result.Report["closedPartReviewRequired"] = true;
            result.Report["closedPartReviewHash"] = null;
            result.Report["recommendedLocalTools"] = new JArray
            {
                "mesh_analyze_body_parts", "mesh_preview_smart_split",
                "mesh_apply_smart_split", "mesh_validate_split",
            };
        }

        private static void EvaluateRigAndAnimation(
            Ai3dWorkflowRecord record, Ai3dStagedCandidate candidate, JObject output,
            Ai3dWorkflowQualityEvaluation result)
        {
            if (((int?)output["skinnedRendererCount"] ?? 0) <= 0)
                Error(result, "AI3D_RIG_SKIN_MISSING", "Rigged candidate contains no SkinnedMeshRenderer.");
            if (((int?)output["boneCount"] ?? 0) <= 0)
                Error(result, "AI3D_RIG_BONES_MISSING", "Rigged candidate contains no bound bones.");
            if (((int?)output["bindPoseCount"] ?? 0) <= 0)
                Error(result, "AI3D_RIG_BIND_POSES_MISSING", "Rigged candidate contains no bind poses.");
            var requiresClips = Ai3dWorkflowNodePlan.ParseMany(record.PipelineNodes)
                .Any(value => value.Operation == "retarget");
            if (requiresClips && ((int?)output["animationClipCount"] ?? 0) <= 0)
                Error(result, "AI3D_ANIMATION_CLIPS_MISSING", "Retarget completed but Unity imported no AnimationClip.");
            try
            {
                var context = RigTargetResolver.Resolve(candidate.Asset);
                if (!context.IsValid)
                {
                    Error(result, "AI3D_RIG_CONTEXT_INVALID", "Local Rig Inspector could not resolve the imported rig.");
                    return;
                }
                var diagnostics = RigInspectorService.Inspect(context);
                result.Report["rigDiagnostics"] = new JObject
                {
                    ["rigType"] = diagnostics.RigType,
                    ["rendererCount"] = diagnostics.RendererCount,
                    ["boneCount"] = diagnostics.BoneCount,
                    ["vertexCount"] = diagnostics.VertexCount,
                    ["weightedVertexCount"] = diagnostics.WeightedVertexCount,
                    ["unweightedVertexCount"] = diagnostics.UnweightedVertexCount,
                    ["invalidWeightCount"] = diagnostics.InvalidWeightCount,
                    ["maximumInfluences"] = diagnostics.MaximumInfluences,
                    ["stateHash"] = diagnostics.StateHash,
                };
                foreach (var issue in diagnostics.Issues)
                {
                    if (issue.Severity == RigDiagnosticSeverity.Error)
                        Error(result, "RIG_" + issue.Code, issue.Message);
                    else if (issue.Severity == RigDiagnosticSeverity.Warning)
                        Warning(result, "RIG_" + issue.Code, issue.Message);
                }
                if (requiresClips)
                {
                    var clips = AssetDatabase.LoadAllAssetsAtPath(candidate.StagedAssetPath)
                        .OfType<AnimationClip>()
                        .Where(value => value != null && !value.name.StartsWith("__preview__", StringComparison.Ordinal))
                        .ToArray();
                    if (clips.Length > 0)
                    {
                        var clipReport = RigClipValidationService.Validate(candidate.Asset, clips, 12);
                        result.Report["clipValidation"] = clipReport.ToJson();
                        if (!clipReport.IsValid)
                            Error(result, "AI3D_CLIP_DEFORMATION_INVALID",
                                "One or more imported clips failed local deformation validation.");
                    }
                }
            }
            catch (Exception exception)
            {
                Error(result, "AI3D_RIG_DIAGNOSTICS_FAILED", exception.Message);
            }
            result.Report["clipValidationRequired"] = true;
            result.Report["recommendedLocalTools"] = new JArray
            {
                "rig_inspect", "rig_validate_clips", "rig_analyze_retarget", "rig_create_ik",
            };
        }

        private static JObject Capture(string assetPath)
        {
            var result = new JObject
            {
                ["assetPath"] = assetPath,
                ["available"] = false,
                ["meshCount"] = 0,
                ["rendererCount"] = 0,
                ["skinnedRendererCount"] = 0,
                ["vertexCount"] = 0,
                ["triangleCount"] = 0,
                ["weightedVertexCount"] = 0,
                ["bindPoseCount"] = 0,
                ["blendShapeCount"] = 0,
                ["animationClipCount"] = 0,
                ["boundsMagnitude"] = 0f,
                ["meshChannelsReadable"] = true,
            };
            if (string.IsNullOrWhiteSpace(assetPath)) return result;
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null) return result;
            result["available"] = true;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            result["rendererCount"] = renderers.Length;
            result["skinnedRendererCount"] = skinned.Length;
            var uniqueBones = new HashSet<int>();
            foreach (var renderer in skinned)
                foreach (var bone in renderer.bones ?? Array.Empty<Transform>())
                    if (bone != null) uniqueBones.Add(bone.GetInstanceID());
            result["boneCount"] = uniqueBones.Count;
            var bounds = new Bounds();
            var initialized = false;
            var meshes = root.GetComponentsInChildren<MeshFilter>(true)
                .Select(value => value.sharedMesh)
                .Concat(skinned.Select(value => value.sharedMesh))
                .Where(value => value != null)
                .GroupBy(value => value.GetInstanceID())
                .Select(value => value.First())
                .ToArray();
            result["meshCount"] = meshes.Length;
            var vertices = 0;
            var triangles = 0;
            var weighted = 0;
            var bindPoses = 0;
            var blendShapes = 0;
            var channelsReadable = true;
            foreach (var mesh in meshes)
            {
                vertices += mesh.vertexCount;
                for (var sub = 0; sub < mesh.subMeshCount; sub++)
                    if (mesh.GetTopology(sub) == MeshTopology.Triangles)
                        triangles += checked((int)(mesh.GetIndexCount(sub) / 3));
                blendShapes += mesh.blendShapeCount;
                if (!mesh.isReadable)
                {
                    channelsReadable = false;
                    if (!initialized) { bounds = mesh.bounds; initialized = true; }
                    else bounds.Encapsulate(mesh.bounds);
                    continue;
                }
                bindPoses += mesh.bindposes?.Length ?? 0;
                var weights = mesh.boneWeights;
                for (var i = 0; i < weights.Length; i++)
                    if (weights[i].weight0 + weights[i].weight1 + weights[i].weight2 + weights[i].weight3 > 0.0001f)
                        weighted++;
                if (!initialized) { bounds = mesh.bounds; initialized = true; }
                else bounds.Encapsulate(mesh.bounds);
            }
            result["vertexCount"] = vertices;
            result["triangleCount"] = triangles;
            result["weightedVertexCount"] = weighted;
            result["bindPoseCount"] = bindPoses;
            result["blendShapeCount"] = blendShapes;
            result["meshChannelsReadable"] = channelsReadable;
            result["animationClipCount"] = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<AnimationClip>()
                .Count(value => value != null && !value.name.StartsWith("__preview__", StringComparison.Ordinal));
            result["boundsMagnitude"] = initialized ? bounds.size.magnitude : 0f;
            return result;
        }

        private static JArray IssuesJson(IEnumerable<Ai3dCandidateIssue> issues) => new JArray(
            issues.Select(value => new JObject
            {
                ["code"] = value.Code,
                ["severity"] = value.Severity.ToString(),
                ["message"] = value.Message,
            }));

        private static void Error(Ai3dWorkflowQualityEvaluation result, string code, string message) =>
            result.Issues.Add(new Ai3dCandidateIssue { Code = code, Message = message, Severity = Ai3dIssueSeverity.Error });

        private static void Warning(Ai3dWorkflowQualityEvaluation result, string code, string message) =>
            result.Issues.Add(new Ai3dCandidateIssue { Code = code, Message = message, Severity = Ai3dIssueSeverity.Warning });
    }
}
