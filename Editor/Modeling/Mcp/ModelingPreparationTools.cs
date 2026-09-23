using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Mcp
{
    internal static class ModelingPreparationSchemas
    {
        internal static JObject Hash => new JObject { ["type"] = "string", ["pattern"] = "^[0-9a-f]{64}$" };
        internal static JObject Integer(int minimum = 0) => new JObject { ["type"] = "integer", ["minimum"] = minimum };
        internal static JObject Vector => new JObject
        {
            ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3,
        };
        internal static JObject Bounds => Object(new JObject { ["center"] = Vector, ["size"] = Vector });
        internal static JObject Object(JObject properties) => new JObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JArray(System.Linq.Enumerable.Select(properties.Properties(), p => p.Name)),
            ["additionalProperties"] = false,
        };
        internal static JArray VectorJson(Vector3 value) => new JArray(value.x, value.y, value.z);
        internal static JObject BoundsJson(UnityEngine.Bounds bounds) => new JObject
        {
            ["center"] = VectorJson(bounds.center), ["size"] = VectorJson(bounds.size),
        };

        internal static JObject CleanupInput(bool apply)
        {
            var schema = ModelingSessionToolSchemas.DocumentInput("expectedTopologyRevision", "expectedGeometryRevision");
            var p = (JObject)schema["properties"];
            p["expectedTopologyRevision"] = Integer();
            p["expectedGeometryRevision"] = Integer();
            p["areaEpsilon"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 0,
                ["description"] = "Triangle area in mesh-local units squared. Zero removes exact degeneracies only; positive values may remove visible thin faces." };
            p["removeUnused"] = new JObject { ["type"] = "boolean", ["default"] = true };
            p["removeLooseLines"] = new JObject { ["type"] = "boolean", ["default"] = false };
            p["removeLoosePoints"] = new JObject { ["type"] = "boolean", ["default"] = false };
            p["removeComponentIds"] = new JObject { ["type"] = "array", ["items"] = Integer(), ["maxItems"] = 10000,
                ["description"] = "Explicit component ids from mesh_analyze_cleanup at this revision and expectedMeshHash. Disconnected parts are never removed automatically." };
            p["expectedMeshHash"] = Hash;
            p["expectedMeshHash"]["description"] = "Mesh hash returned by mesh_analyze_cleanup. Required when removeComponentIds is nonempty; checked whenever supplied.";
            schema["anyOf"] = new JArray
            {
                new JObject { ["properties"] = new JObject
                {
                    ["removeComponentIds"] = new JObject { ["type"] = "array", ["items"] = Integer(), ["maxItems"] = 0 },
                } },
                new JObject { ["required"] = new JArray("expectedMeshHash") },
            };
            if (apply) { p["previewHash"] = Hash; ((JArray)schema["required"]).Add("previewHash"); }
            return schema;
        }

        internal static ToolResult ValidateCleanupMeshHash(MeshEditDocument document, JObject arguments)
        {
            var expectedHash = arguments["expectedMeshHash"];
            if (arguments["removeComponentIds"] is JArray ids && ids.Count > 0
                && (expectedHash?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)expectedHash)))
                return ToolResult.Error("EXPECTED_MESH_HASH_REQUIRED", "Removing selected components requires expectedMeshHash from mesh_analyze_cleanup; analyze and review the selection again.");
            if (expectedHash != null
                && !string.Equals((string)expectedHash, MeshFingerprint.Compute(document.WorkingMesh), StringComparison.Ordinal))
                return ToolResult.Error("STALE_MESH_HASH", "Mesh changed since cleanup diagnosis; analyze and review component ids again.");
            return null;
        }

        internal static CleanupMeshOperation Cleanup(JObject a)
            => new CleanupMeshOperation((float?)a["areaEpsilon"] ?? 0, (bool?)a["removeUnused"] ?? true,
                (bool?)a["removeLooseLines"] ?? false, (bool?)a["removeLoosePoints"] ?? false,
                a["removeComponentIds"]?.ToObject<int[]>());

        internal static JObject ScaleInput(bool apply)
        {
            var p = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("apply_current_scale", "target_dimension") },
                ["axis"] = new JObject { ["type"] = "string", ["enum"] = new JArray("x", "y", "z"), ["default"] = "y" },
                ["targetDimension"] = new JObject { ["type"] = "number", ["minimum"] = 0.000001, ["default"] = 1.8 },
                ["outputFolder"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets(?:/|$)" },
            };
            var required = new JArray { "mode" };
            if (apply)
            {
                p["expectedSourceHash"] = Hash; p["previewHash"] = Hash;
                required.Add("expectedSourceHash"); required.Add("previewHash");
            }
            return new JObject
            {
                ["type"] = "object", ["properties"] = p, ["required"] = required,
                ["oneOf"] = new JArray(new JObject { ["required"] = new JArray("target") }, new JObject { ["required"] = new JArray("instanceId") }),
                ["additionalProperties"] = false,
            };
        }

        internal static ModelScaleSettings ScaleSettings(JObject a)
        {
            var mode = (string)a["mode"];
            if (mode != "apply_current_scale" && mode != "target_dimension") throw new ArgumentException("Invalid scale mode.");
            var axis = (string)a["axis"] ?? "y";
            if (axis != "x" && axis != "y" && axis != "z") throw new ArgumentException("Invalid size axis.");
            return new ModelScaleSettings
            {
                Mode = mode == "apply_current_scale" ? ModelScaleMode.BakeCurrentScale : ModelScaleMode.TargetDimension,
                Axis = axis == "x" ? ModelScaleAxis.X : axis == "z" ? ModelScaleAxis.Z : ModelScaleAxis.Y,
                TargetDimension = (float?)a["targetDimension"] ?? 1.8f,
                OutputFolder = (string)a["outputFolder"],
            };
        }

        internal static JObject ScaleOutput(bool apply)
        {
            var p = new JObject
            {
                ["sourceHash"] = Hash, ["previewHash"] = Hash,
                ["boundsBefore"] = Bounds, ["boundsAfter"] = Bounds,
                ["scaleFactor"] = new JObject { ["type"] = "number" }, ["sourceRootScale"] = Vector,
                ["resultRootScale"] = Vector, ["rendererCount"] = Integer(), ["skinnedRendererCount"] = Integer(),
                ["warnings"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            };
            if (apply)
            {
                p["editableRoot"] = SchemaHelpers.TargetRefSchema;
                p["createdAssetPaths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } };
            }
            return Object(p);
        }
        internal static JObject ScaleJson(ModelScalePreview preview) => new JObject
        {
            ["sourceHash"] = preview.SourceHash, ["previewHash"] = preview.PreviewHash,
            ["boundsBefore"] = BoundsJson(preview.BoundsBefore), ["boundsAfter"] = BoundsJson(preview.BoundsAfter),
            ["scaleFactor"] = preview.ScaleFactor, ["sourceRootScale"] = VectorJson(preview.SourceRootScale),
            ["resultRootScale"] = VectorJson(Vector3.one), ["rendererCount"] = preview.RendererCount,
            ["skinnedRendererCount"] = preview.SkinnedRendererCount, ["warnings"] = new JArray(preview.Warnings),
        };
    }

    public sealed class AnalyzeMeshCleanupTool : IMcpTool
    {
        public string Name => "mesh_analyze_cleanup";
        public string Description => "Read-only cleanup diagnosis with paginated disconnected components. Ids identify parts, not proof they are debris; UV seams can separate components.";
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);
        public JObject InputSchema
        {
            get
            {
                var schema = ModelingSessionToolSchemas.DocumentInput();
                var p = (JObject)schema["properties"];
                p["offset"] = ModelingPreparationSchemas.Integer();
                p["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200, ["default"] = 50 };
                p["expectedMeshHash"] = ModelingPreparationSchemas.Hash;
                return schema;
            }
        }
        public JObject OutputSchema => ModelingPreparationSchemas.Object(new JObject
        {
            ["meshHash"] = ModelingPreparationSchemas.Hash,
            ["topologyRevision"] = ModelingPreparationSchemas.Integer(), ["geometryRevision"] = ModelingPreparationSchemas.Integer(),
            ["vertexCount"] = ModelingPreparationSchemas.Integer(), ["faceCount"] = ModelingPreparationSchemas.Integer(),
            ["unusedVertices"] = ModelingPreparationSchemas.Integer(), ["degenerateFaces"] = ModelingPreparationSchemas.Integer(),
            ["looseLines"] = ModelingPreparationSchemas.Integer(), ["loosePoints"] = ModelingPreparationSchemas.Integer(),
            ["componentCount"] = ModelingPreparationSchemas.Integer(), ["nextOffset"] = ModelingPreparationSchemas.Integer(),
            ["hasMore"] = new JObject { ["type"] = "boolean" },
            ["components"] = new JObject { ["type"] = "array", ["items"] = ModelingPreparationSchemas.Object(new JObject
            {
                ["id"] = ModelingPreparationSchemas.Integer(), ["faceCount"] = ModelingPreparationSchemas.Integer(),
                ["vertexCount"] = ModelingPreparationSchemas.Integer(), ["bounds"] = ModelingPreparationSchemas.Bounds,
                ["submeshes"] = new JObject { ["type"] = "array", ["items"] = ModelingPreparationSchemas.Integer() },
            }) },
        });
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            try
            {
                var hash = MeshFingerprint.Compute(document.WorkingMesh);
                if (arguments["expectedMeshHash"] != null && (string)arguments["expectedMeshHash"] != hash)
                    return ToolResult.Error("STALE_MESH_HASH", "Mesh changed since the previous diagnostic page; analyze again.");
                int offset = (int?)arguments["offset"] ?? 0, limit = (int?)arguments["limit"] ?? 50;
                if (offset < 0 || limit < 1 || limit > 200) return ToolResult.Error("INVALID_PAGE", "Use offset >= 0 and limit from 1 to 200.");
                var analysis = MeshCleanupAnalysis.Analyze(document.WorkingMesh);
                int end = (int)Math.Min(analysis.Components.Count, (long)offset + limit);
                var components = new JArray();
                for (int i = offset; i < end; i++)
                {
                    var component = analysis.Components[i];
                    components.Add(new JObject { ["id"] = component.Id, ["faceCount"] = component.FaceCount,
                        ["vertexCount"] = component.VertexCount, ["bounds"] = ModelingPreparationSchemas.BoundsJson(component.Bounds),
                        ["submeshes"] = new JArray(component.SubMeshIndices) });
                }
                return ToolResult.Ok("Cleanup diagnosis generated; no geometry changed.", new JObject
                {
                    ["meshHash"] = hash, ["topologyRevision"] = document.Revision.Topology, ["geometryRevision"] = document.Revision.Geometry,
                    ["vertexCount"] = analysis.VertexCount, ["faceCount"] = analysis.FaceCount,
                    ["unusedVertices"] = analysis.UnreferencedVertexCount, ["degenerateFaces"] = analysis.DegenerateFaceCount,
                    ["looseLines"] = analysis.LooseLineCount, ["loosePoints"] = analysis.LoosePointCount,
                    ["componentCount"] = analysis.Components.Count, ["nextOffset"] = end,
                    ["hasMore"] = end < analysis.Components.Count, ["components"] = components,
                });
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class PreviewMeshCleanupTool : IMcpTool
    {
        public string Name => "mesh_preview_cleanup";
        public string Description => "Preview conservative cleanup; defaults remove unused vertices and exact degenerate faces only. Optional loose data and explicitly selected fragments have typed parameters.";
        public JObject InputSchema => ModelingPreparationSchemas.CleanupInput(false);
        public JObject OutputSchema => ModelingSessionToolSchemas.OperationOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            try
            {
                failure = ModelingPreparationSchemas.ValidateCleanupMeshHash(document, arguments);
                if (failure != null) return failure;
                using (var preview = ModelingCommandService.Preview(document, ModelingPreparationSchemas.Cleanup(arguments), ModelingSessionToolUtility.Revision(arguments)))
                    return ToolResult.Ok("Cleanup preview generated.", ModelingSessionToolUtility.OperationJson(preview.Result, preview.PreviewHash));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class ApplyMeshCleanupTool : IMcpTool
    {
        public string Name => "mesh_apply_cleanup";
        public string Description => "Apply cleanup to the working mesh only after matching revision and previewHash. Preserves surviving vertex positions, rig channels and material slots.";
        public JObject InputSchema => ModelingPreparationSchemas.CleanupInput(true);
        public JObject OutputSchema => ModelingSessionToolSchemas.OperationOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite | McpCapability.Destructive, McpRiskLevel.High, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            try
            {
                failure = ModelingPreparationSchemas.ValidateCleanupMeshHash(document, arguments);
                if (failure != null) return failure;
                var result = ModelingCommandService.Apply(document, ModelingPreparationSchemas.Cleanup(arguments), ModelingSessionToolUtility.Revision(arguments), (string)arguments["previewHash"]);
                return ToolResult.Ok("Cleanup applied to editable mesh.", ModelingSessionToolUtility.OperationJson(result, result.ResultHash));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class PreviewModelScaleTool : IMcpTool
    {
        public string Name => "model_preview_scale";
        public string Description => "Preflight a complete model root for uniform scale normalization; preview local model dimensions and preserved rig dependencies without creating assets. Result root scale is 1,1,1.";
        public JObject InputSchema => ModelingPreparationSchemas.ScaleInput(false);
        public JObject OutputSchema => ModelingPreparationSchemas.ScaleOutput(false);
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                return ToolResult.Ok("Model scale preview generated.", ModelingPreparationSchemas.ScaleJson(ModelScaleService.Preview(target, ModelingPreparationSchemas.ScaleSettings(arguments))));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class ApplyModelScaleTool : IMcpTool
    {
        public string Name => "model_apply_scale";
        public string Description => "Create an independent normalized model copy after matching source and preview hashes. Preserves original; supports preflight-approved static and rigged hierarchies.";
        public JObject InputSchema => ModelingPreparationSchemas.ScaleInput(true);
        public JObject OutputSchema => ModelingPreparationSchemas.ScaleOutput(true);
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite | McpCapability.SceneWrite, McpRiskLevel.Medium, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var result = ModelScaleService.Apply(target, ModelingPreparationSchemas.ScaleSettings(arguments), (string)arguments["expectedSourceHash"], (string)arguments["previewHash"]);
                var json = ModelingPreparationSchemas.ScaleJson(result.Preview);
                json["editableRoot"] = ToolTargetRef.Capture(result.Root).ToJson();
                json["createdAssetPaths"] = new JArray(result.CreatedAssetPaths);
                return ToolResult.Ok("Normalized model copy created with root scale 1,1,1.", json);
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }
}
