using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Validation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Mcp
{
    internal static class ModelingSessionToolSchemas
    {
        public static JObject DocumentPathProperty => new JObject
        {
            ["type"] = "string",
            ["pattern"] = "^Assets/.*\\.asset$",
        };

        public static JObject DocumentInput(params string[] extraRequired)
        {
            var required = new JArray { "documentAssetPath" };
            if (extraRequired != null) for (int i = 0; i < extraRequired.Length; i++) required.Add(extraRequired[i]);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["documentAssetPath"] = DocumentPathProperty },
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        public static JObject OperationInput(bool apply)
        {
            var properties = new JObject
            {
                ["documentAssetPath"] = DocumentPathProperty,
                ["operationId"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(
                        "mesh.transform_vertices", "mesh.mirror", "mesh.cleanup", "mesh.weld",
                        "mesh.subdivide", "mesh.extrude_faces", "mesh.extrude_edges", "mesh.bevel_faces",
                        "mesh.bridge_loops", "mesh.fill_hole", "mesh.adaptive_refine", "mesh.deform", "mesh.surface_fit"),
                },
                ["parameters"] = new JObject { ["type"] = "object" },
                ["expectedTopologyRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["expectedGeometryRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            };
            var required = new JArray
            {
                "documentAssetPath", "operationId", "parameters",
                "expectedTopologyRevision", "expectedGeometryRevision",
            };
            if (apply)
            {
                properties["previewHash"] = new JObject { ["type"] = "string", ["pattern"] = "^[0-9a-f]{64}$" };
                required.Add("previewHash");
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        public static JObject OperationOutput => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["operationId"] = new JObject { ["type"] = "string" },
                ["previewHash"] = new JObject { ["type"] = "string" },
                ["topologyRevision"] = new JObject { ["type"] = "integer" },
                ["geometryRevision"] = new JObject { ["type"] = "integer" },
                ["createdVertices"] = new JObject { ["type"] = "integer" },
                ["removedVertices"] = new JObject { ["type"] = "integer" },
                ["createdFaces"] = new JObject { ["type"] = "integer" },
                ["removedFaces"] = new JObject { ["type"] = "integer" },
                ["valid"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray
            {
                "operationId", "previewHash", "topologyRevision", "geometryRevision",
                "createdVertices", "removedVertices", "createdFaces", "removedFaces", "valid",
            },
            ["additionalProperties"] = false,
        };
    }

    internal static class ModelingSessionToolUtility
    {
        public static MeshEditDocument LoadDocument(JObject arguments, out ToolResult failure)
        {
            failure = null;
            var path = (string)arguments?["documentAssetPath"];
            var document = AssetDatabase.LoadAssetAtPath<MeshEditDocument>(path);
            if (document == null) failure = ToolResult.Error("DOCUMENT_NOT_FOUND", $"No MeshEditDocument exists at '{path}'.");
            return document;
        }

        public static MeshRevision Revision(JObject arguments)
            => new MeshRevision(
                (uint)((long?)arguments?["expectedTopologyRevision"] ?? 0),
                (uint)((long?)arguments?["expectedGeometryRevision"] ?? 0));

        public static ToolResult Exception(Exception exception)
            => AnalyzeBodyPartsTool.ExceptionResult(exception);

        public static JObject OperationJson(MeshOperationResult result, string previewHash)
            => new JObject
            {
                ["operationId"] = result.OperationId,
                ["previewHash"] = previewHash ?? result.ResultHash,
                ["topologyRevision"] = result.RevisionAfter.Topology,
                ["geometryRevision"] = result.RevisionAfter.Geometry,
                ["createdVertices"] = result.CreatedVertexCount,
                ["removedVertices"] = result.RemovedVertexCount,
                ["createdFaces"] = result.CreatedFaceCount,
                ["removedFaces"] = result.RemovedFaceCount,
                ["valid"] = result.Validation?.IsValid ?? false,
            };

        public static ToolDescriptor Descriptor(
            string name,
            JObject output,
            McpCapability capability,
            McpRiskLevel risk,
            McpConfirmationPolicy confirmation,
            bool idempotent)
            => new ToolDescriptor
            {
                Name = name,
                Category = "mesh",
                Capabilities = capability,
                Risk = risk,
                ConfirmationPolicy = confirmation,
                EditorMode = McpEditorMode.EditModeOnly,
                IsIdempotent = idempotent,
                ExecutionRequirements = McpExecutionRequirement.MainThread,
                HeadlessSafe = true,
                OutputSchema = output,
            };
    }

    public sealed class BeginMeshEditSessionTool : IMcpTool
    {
        public string Name => "mesh_begin_edit_session";
        public string Description => "Creates an independent editable mesh asset and MeshEditDocument without modifying the imported source.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["outputFolder"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets(?:/|$)" },
            },
            ["oneOf"] = new JArray
            {
                new JObject { ["required"] = new JArray { "target" } },
                new JObject { ["required"] = new JArray { "instanceId" } },
            },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => ContextOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name, OutputSchema, McpCapability.AssetWrite | McpCapability.SceneWrite,
            McpRiskLevel.Medium, McpConfirmationPolicy.Required, false);

        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var result = EditableMeshFactory.Create(target, (string)arguments["outputFolder"]);
                return ToolResult.Ok("Editable mesh session created.", ContextJson(result.Document, result.EditableObject));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }

        internal static JObject ContextOutput => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["documentAssetPath"] = new JObject { ["type"] = "string" },
                ["documentId"] = new JObject { ["type"] = "string" },
                ["sessionId"] = new JObject { ["type"] = "string" },
                ["sourceHash"] = new JObject { ["type"] = "string" },
                ["workingMeshPath"] = new JObject { ["type"] = "string" },
                ["topologyRevision"] = new JObject { ["type"] = "integer" },
                ["geometryRevision"] = new JObject { ["type"] = "integer" },
                ["state"] = new JObject { ["type"] = "string" },
                ["sourceDrifted"] = new JObject { ["type"] = "boolean" },
                ["modifierCount"] = new JObject { ["type"] = "integer" },
                ["editableObject"] = SchemaHelpers.TargetRefSchema,
            },
            ["required"] = new JArray
            {
                "documentAssetPath", "documentId", "sessionId", "sourceHash", "workingMeshPath",
                "topologyRevision", "geometryRevision", "state", "sourceDrifted", "modifierCount",
            },
            ["additionalProperties"] = false,
        };

        internal static JObject ContextJson(MeshEditDocument document, UnityEngine.GameObject editableObject = null)
        {
            var value = new JObject
            {
                ["documentAssetPath"] = AssetDatabase.GetAssetPath(document),
                ["documentId"] = document.DocumentId,
                ["sessionId"] = document.SessionId,
                ["sourceHash"] = document.SourceHash,
                ["workingMeshPath"] = AssetDatabase.GetAssetPath(document.WorkingMesh),
                ["topologyRevision"] = document.Revision.Topology,
                ["geometryRevision"] = document.Revision.Geometry,
                ["state"] = document.State.ToString(),
                ["sourceDrifted"] = document.HasSourceDrift(),
                ["modifierCount"] = document.ModifierStack.Count,
            };
            if (editableObject != null) value["editableObject"] = ToolTargetRef.Capture(editableObject).ToJson();
            return value;
        }
    }

    public sealed class GetMeshEditContextTool : IMcpTool
    {
        public string Name => "mesh_get_edit_context";
        public string Description => "Returns the current revision, source hash, state and modifier count for an edit document.";
        public JObject InputSchema => ModelingSessionToolSchemas.DocumentInput();
        public JObject OutputSchema => BeginMeshEditSessionTool.ContextOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            return failure ?? ToolResult.Ok("Mesh edit context loaded.", BeginMeshEditSessionTool.ContextJson(document));
        }
    }

    public sealed class PreviewMeshOperationTool : IMcpTool
    {
        public string Name => "mesh_preview_operation";
        public string Description => "Runs a registered modeling operation on a temporary mesh and returns its approval hash without writing assets.";
        public JObject InputSchema => ModelingSessionToolSchemas.OperationInput(false);
        public JObject OutputSchema => ModelingSessionToolSchemas.OperationOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);

        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            ModelingOperationCatalog.RegisterAll();
            if (!MeshOperationRegistry.TryCreate(
                    (string)arguments["operationId"],
                    ((JObject)arguments["parameters"]).ToString(Formatting.None),
                    out var operation))
                return ToolResult.Error("OPERATION_NOT_REGISTERED", $"Unknown modeling operation '{(string)arguments["operationId"]}'.");
            try
            {
                using (var preview = ModelingCommandService.Preview(document, operation, ModelingSessionToolUtility.Revision(arguments)))
                    return ToolResult.Ok("Operation preview generated.", ModelingSessionToolUtility.OperationJson(preview.Result, preview.PreviewHash));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class ApplyMeshOperationTool : IMcpTool
    {
        public string Name => "mesh_apply_operation";
        public string Description => "Applies a registered operation only when document revision and preview hash match the approved preview.";
        public JObject InputSchema => ModelingSessionToolSchemas.OperationInput(true);
        public JObject OutputSchema => ModelingSessionToolSchemas.OperationOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name, OutputSchema, McpCapability.AssetWrite | McpCapability.Destructive,
            McpRiskLevel.High, McpConfirmationPolicy.Required, false);

        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            ModelingOperationCatalog.RegisterAll();
            if (!MeshOperationRegistry.TryCreate(
                    (string)arguments["operationId"],
                    ((JObject)arguments["parameters"]).ToString(Formatting.None),
                    out var operation))
                return ToolResult.Error("OPERATION_NOT_REGISTERED", $"Unknown modeling operation '{(string)arguments["operationId"]}'.");
            try
            {
                var result = ModelingCommandService.Apply(
                    document,
                    operation,
                    ModelingSessionToolUtility.Revision(arguments),
                    (string)arguments["previewHash"]);
                return ToolResult.Ok("Operation applied to editable mesh.", ModelingSessionToolUtility.OperationJson(result, result.ResultHash));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class CommitMeshEditSessionTool : IMcpTool
    {
        public string Name => "mesh_commit_edit_session";
        public string Description => "Validates and commits the current editable mesh asset; the imported source remains untouched.";
        public JObject InputSchema => ModelingSessionToolSchemas.DocumentInput();
        public JObject OutputSchema => BeginMeshEditSessionTool.ContextOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.Medium, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            try
            {
                var report = ModelingCommandService.Commit(document);
                if (!report.IsValid) return ToolResult.Error("COMMIT_VALIDATION_FAILED", "Editable mesh failed validation.");
                return ToolResult.Ok("Mesh edit session committed.", BeginMeshEditSessionTool.ContextJson(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class RebaseMeshEditSessionTool : IMcpTool
    {
        public string Name => "mesh_rebase_edit_session";
        public string Description => "Resets the editable mesh to the current source and clears the modifier stack after explicit confirmation.";
        public JObject InputSchema => ModelingSessionToolSchemas.DocumentInput();
        public JObject OutputSchema => BeginMeshEditSessionTool.ContextOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name, OutputSchema, McpCapability.AssetWrite | McpCapability.Destructive,
            McpRiskLevel.High, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            try
            {
                ModelingCommandService.Rebase(document);
                return ToolResult.Ok("Mesh edit session rebased.", BeginMeshEditSessionTool.ContextJson(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class DiscardMeshEditSessionTool : IMcpTool
    {
        public string Name => "mesh_discard_edit_session";
        public string Description => "Discards modifiers and restores the editable mesh from its source; created assets are retained for recovery.";
        public JObject InputSchema => ModelingSessionToolSchemas.DocumentInput();
        public JObject OutputSchema => BeginMeshEditSessionTool.ContextOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name, OutputSchema, McpCapability.AssetWrite | McpCapability.Destructive,
            McpRiskLevel.High, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            try
            {
                ModelingCommandService.Discard(document);
                return ToolResult.Ok("Mesh edit session discarded and restored.", BeginMeshEditSessionTool.ContextJson(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class ValidateEditableMeshTool : IMcpTool
    {
        public string Name => "mesh_validate";
        public string Description => "Validates indices, finite positions, channel counts, bone indices/weights and bounds for an editable mesh.";
        public JObject InputSchema => ModelingSessionToolSchemas.DocumentInput();
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["valid"] = new JObject { ["type"] = "boolean" },
                ["issues"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            },
            ["required"] = new JArray { "valid", "issues" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            var report = MeshValidator.Validate(document.WorkingMesh);
            var issues = new JArray();
            for (int i = 0; i < report.Issues.Count; i++)
                issues.Add($"{(report.Issues[i].IsError ? "ERROR" : "WARNING")} {report.Issues[i].Code}: {report.Issues[i].Message}");
            return ToolResult.Ok(report.IsValid ? "Editable mesh is valid." : "Editable mesh has validation failures.", new JObject
            {
                ["valid"] = report.IsValid,
                ["issues"] = issues,
            });
        }
    }
}
