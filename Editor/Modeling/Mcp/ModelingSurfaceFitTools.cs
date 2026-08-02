using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.SurfaceFit;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Mcp
{
    internal static class ModelingSurfaceFitSchemas
    {
        public static JObject Operation(bool apply)
        {
            var schema = ModelingSessionToolSchemas.OperationInput(apply);
            var properties = (JObject)schema["properties"];
            properties.Remove("operationId");
            var required = (JArray)schema["required"];
            for (int i = required.Count - 1; i >= 0; i--)
                if ((string)required[i] == "operationId") required.RemoveAt(i);
            return schema;
        }

        public static readonly JObject EvaluationOutput = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["valid"] = new JObject { ["type"] = "boolean" },
                ["vertexCount"] = new JObject { ["type"] = "integer" },
                ["evaluatedVertexCount"] = new JObject { ["type"] = "integer" },
                ["belowClearanceCount"] = new JObject { ["type"] = "integer" },
                ["minimumSignedClearance"] = new JObject { ["type"] = "number" },
                ["meanSignedClearance"] = new JObject { ["type"] = "number" },
                ["sampleCount"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "valid", "vertexCount", "evaluatedVertexCount", "belowClearanceCount", "minimumSignedClearance", "meanSignedClearance", "sampleCount" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class PreviewSurfaceFitTool : IMcpTool
    {
        public string Name => "mesh_preview_surface_fit";
        public string Description => "Previews Smart Surface Fit and returns the approval hash without modifying the editable asset.";
        public JObject InputSchema => ModelingSurfaceFitSchemas.Operation(false);
        public JObject OutputSchema => ModelingSessionToolSchemas.OperationOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments)
        {
            var forwarded = arguments != null ? (JObject)arguments.DeepClone() : new JObject();
            forwarded["operationId"] = "mesh.surface_fit";
            return new PreviewMeshOperationTool().Execute(forwarded);
        }
    }

    public sealed class ApplySurfaceFitTool : IMcpTool
    {
        public string Name => "mesh_apply_surface_fit";
        public string Description => "Applies an approved Smart Surface Fit preview using the shared modeling command service.";
        public JObject InputSchema => ModelingSurfaceFitSchemas.Operation(true);
        public JObject OutputSchema => ModelingSessionToolSchemas.OperationOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite | McpCapability.Destructive, McpRiskLevel.High, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            var forwarded = arguments != null ? (JObject)arguments.DeepClone() : new JObject();
            forwarded["operationId"] = "mesh.surface_fit";
            return new ApplyMeshOperationTool().Execute(forwarded);
        }
    }

    public sealed class EvaluatePoseClearanceTool : IMcpTool
    {
        public string Name => "mesh_evaluate_pose_clearance";
        public string Description => "Evaluates current-pose signed clearance between a source renderer and one or more target renderers.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["source"] = SchemaHelpers.TargetRefSchema,
                ["targets"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["items"] = SchemaHelpers.TargetRefSchema },
                ["requiredClearance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["maxDistance"] = new JObject { ["type"] = "number", ["exclusiveMinimum"] = 0 },
                ["characterRoot"] = SchemaHelpers.TargetRefSchema,
                ["animationClipAssetPath"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets/.*" },
                ["sampleCount"] = new JObject { ["type"] = "integer", ["minimum"] = 2, ["maximum"] = 128, ["default"] = 8 },
            },
            ["required"] = new JArray { "source", "targets", "requiredClearance", "maxDistance" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => ModelingSurfaceFitSchemas.EvaluationOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);

        public ToolResult Execute(JObject arguments)
        {
            try
            {
                var source = ResolveRenderer(arguments?["source"], out var error);
                if (source == null) return ToolResult.Error("SOURCE_RENDERER_NOT_FOUND", error);
                var targets = new List<Renderer>();
                var values = (JArray)arguments["targets"];
                for (int i = 0; i < values.Count; i++)
                {
                    var renderer = ResolveRenderer(values[i], out error);
                    if (renderer == null) return ToolResult.Error("TARGET_RENDERER_NOT_FOUND", $"Target {i}: {error}");
                    targets.Add(renderer);
                }

                SurfaceFitPoseSetEvaluation report;
                string clipPath = (string)arguments["animationClipAssetPath"];
                if (!string.IsNullOrWhiteSpace(clipPath))
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                    if (clip == null) return ToolResult.Error("ANIMATION_CLIP_NOT_FOUND", $"No AnimationClip exists at '{clipPath}'.");
                    var rootObject = ToolTargetResolver.Resolve(ToolTargetRef.FromJson(arguments["characterRoot"]), out error);
                    var characterRoot = rootObject as GameObject ?? (rootObject as Component)?.gameObject;
                    if (characterRoot == null) return ToolResult.Error("CHARACTER_ROOT_NOT_FOUND", error ?? "characterRoot is required for clip sampling.");
                    report = SurfaceFitPoseEvaluator.EvaluateAnimationClip(
                        characterRoot, source, targets, clip, (int?)arguments["sampleCount"] ?? 8,
                        (float)arguments["requiredClearance"], (float)arguments["maxDistance"]);
                }
                else report = SurfaceFitPoseEvaluator.EvaluateCurrentPose(
                    source, targets, (float)arguments["requiredClearance"], (float)arguments["maxDistance"]);

                int vertexCount = report.SampleCount > 0 ? report.EvaluatedVertexCount / report.SampleCount : 0;
                return ToolResult.Ok(report.IsValid ? "Pose clearance passed." : "Pose clearance has violations.", new JObject
                {
                    ["valid"] = report.IsValid,
                    ["vertexCount"] = vertexCount,
                    ["evaluatedVertexCount"] = report.EvaluatedVertexCount,
                    ["belowClearanceCount"] = report.BelowClearanceCount,
                    ["minimumSignedClearance"] = Finite(report.MinimumSignedClearance) ? report.MinimumSignedClearance : -1f,
                    ["meanSignedClearance"] = Finite(report.MeanSignedClearance) ? report.MeanSignedClearance : -1f,
                    ["sampleCount"] = report.SampleCount,
                });
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }

        private static Renderer ResolveRenderer(JToken token, out string error)
        {
            var target = ToolTargetResolver.Resolve(ToolTargetRef.FromJson(token), out error);
            if (target is Renderer renderer) return renderer;
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            renderer = gameObject != null ? gameObject.GetComponent<Renderer>() : null;
            if (renderer == null && error == null) error = "Resolved object has no Renderer.";
            return renderer;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
