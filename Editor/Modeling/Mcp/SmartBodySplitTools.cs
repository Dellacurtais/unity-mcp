using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit;
using Maicon.SceneAnalyzer.Editor.Modeling.UI;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Mcp
{
    internal static class SmartBodySplitToolSchemas
    {
        public static JObject TargetInput => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["preset"] = Preset(),
            },
            ["oneOf"] = new JArray
            {
                Required("target", SchemaHelpers.TargetRefSchema),
                Required("instanceId", new JObject { ["type"] = "integer" }),
            },
            ["additionalProperties"] = false,
        };

        public static JObject ApplyInput => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["preset"] = Preset(),
                ["outputFolder"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets(?:/|$)" },
                ["expectedSourceHash"] = Hash(),
                ["previewHash"] = Hash(),
            },
            ["required"] = new JArray { "expectedSourceHash", "previewHash" },
            ["oneOf"] = new JArray
            {
                Required("target", SchemaHelpers.TargetRefSchema),
                Required("instanceId", new JObject { ["type"] = "integer" }),
            },
            ["additionalProperties"] = false,
        };

        public static JObject ValidateInput => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["manifestAssetPath"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets/.*\\.asset$" },
            },
            ["required"] = new JArray { "manifestAssetPath" },
            ["additionalProperties"] = false,
        };

        public static JObject ProposalOutput => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["meshName"] = new JObject { ["type"] = "string" },
                ["rigType"] = new JObject { ["type"] = "string" },
                ["preset"] = Preset(),
                ["sourceHash"] = Hash(),
                ["previewHash"] = Hash(),
                ["triangleCount"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                ["averageConfidence"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["parts"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["slotId"] = new JObject { ["type"] = "string" },
                            ["label"] = new JObject { ["type"] = "string" },
                            ["triangleCount"] = new JObject { ["type"] = "integer" },
                            ["confidence"] = new JObject { ["type"] = "number" },
                        },
                        ["additionalProperties"] = false,
                    },
                },
                ["warnings"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            },
            ["required"] = new JArray
            {
                "target", "meshName", "rigType", "preset", "sourceHash", "previewHash",
                "triangleCount", "averageConfidence", "parts", "warnings",
            },
            ["additionalProperties"] = false,
        };

        private static JObject Hash() => new JObject
        {
            ["type"] = "string",
            ["pattern"] = "^[0-9a-f]{64}$",
        };

        private static JObject Preset() => new JObject
        {
            ["type"] = "string",
            ["enum"] = new JArray("core", "hands_feet"),
            ["default"] = "core",
        };

        private static JObject Required(string name, JObject schema) => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { [name] = schema },
            ["required"] = new JArray { name },
        };
    }

    public sealed class AnalyzeBodyPartsTool : IMcpTool
    {
        public string Name => "mesh_analyze_body_parts";
        public string Description =>
            "Analyzes a humanoid/static mesh with the core six-part or detailed hands-and-feet preset. " +
            "Returns sourceHash and previewHash required by mesh_apply_smart_split. Does not modify assets.";
        public JObject InputSchema => SmartBodySplitToolSchemas.TargetInput;
        public JObject OutputSchema => SmartBodySplitToolSchemas.ProposalOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ReadDescriptor(Name, OutputSchema, false);

        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var proposal = SmartBodySplitAnalyzer.Analyze(target, ReadPreset(arguments));
                return ToolResult.Ok(
                    $"Analyzed '{proposal.SourceMesh.name}' into {proposal.Parts.Count} body parts.",
                    ProposalJson(proposal));
            }
            catch (Exception exception)
            {
                return ExceptionResult(exception);
            }
        }

        internal static ToolDescriptor ReadDescriptor(string name, JObject outputSchema, bool needsSceneView) => new ToolDescriptor
        {
            Name = name,
            Category = "mesh",
            Capabilities = McpCapability.Read,
            Risk = McpRiskLevel.Low,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = true,
            ExecutionRequirements = McpExecutionRequirement.MainThread
                | (needsSceneView ? McpExecutionRequirement.SceneView | McpExecutionRequirement.EditorUpdates : McpExecutionRequirement.None),
            HeadlessSafe = !needsSceneView,
            OutputSchema = outputSchema,
        };

        internal static JObject ProposalJson(SmartBodySplitProposal proposal)
        {
            var parts = new JArray();
            for (int i = 0; i < proposal.Parts.Count; i++)
            {
                var part = proposal.Parts[i];
                parts.Add(new JObject
                {
                    ["slotId"] = part.SlotId,
                    ["label"] = part.Label,
                    ["triangleCount"] = part.Triangles.Count,
                    ["confidence"] = part.AverageConfidence,
                });
            }
            return new JObject
            {
                ["target"] = ToolTargetRef.Capture(proposal.Target).ToJson(),
                ["meshName"] = proposal.SourceMesh.name,
                ["rigType"] = proposal.RigType,
                ["preset"] = SmartBodySplitPresets.Id(proposal.Preset),
                ["sourceHash"] = proposal.SourceHash,
                ["previewHash"] = proposal.PreviewHash,
                ["triangleCount"] = proposal.SourceTriangleCount,
                ["averageConfidence"] = proposal.AverageConfidence,
                ["parts"] = parts,
                ["warnings"] = new JArray(proposal.Warnings),
            };
        }

        internal static ToolResult ExceptionResult(Exception exception)
        {
            var message = exception?.Message ?? "Unknown Smart Body Split error.";
            int separator = message.IndexOf(':');
            if (separator > 0)
            {
                var code = message.Substring(0, separator);
                bool valid = true;
                for (int i = 0; i < code.Length; i++)
                    if (!(char.IsUpper(code[i]) || char.IsDigit(code[i]) || code[i] == '_')) valid = false;
                if (valid) return ToolResult.Error(code, message.Substring(separator + 1).Trim());
            }
            return ToolResult.Error("SMART_SPLIT_FAILED", message);
        }

        internal static SmartBodySplitPreset ReadPreset(JObject arguments)
            => SmartBodySplitPresets.Parse((string)arguments?["preset"]);
    }

    public sealed class PreviewSmartBodySplitTool : IMcpTool
    {
        public string Name => "mesh_preview_smart_split";
        public string Description => "Analyzes a body and opens the Asset Modeler preview with semantic colors. Does not create assets.";
        public JObject InputSchema => SmartBodySplitToolSchemas.TargetInput;
        public JObject OutputSchema => SmartBodySplitToolSchemas.ProposalOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => AnalyzeBodyPartsTool.ReadDescriptor(Name, OutputSchema, true);

        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var preset = AnalyzeBodyPartsTool.ReadPreset(arguments);
                var proposal = SmartBodySplitAnalyzer.Analyze(target, preset);
                SmartBodySplitPreviewController.Show(proposal);
                SmartBodySplitWindow.Open(target, preset);
                return ToolResult.Ok("Smart Body Split preview is active in the Scene View.", AnalyzeBodyPartsTool.ProposalJson(proposal));
            }
            catch (Exception exception)
            {
                return AnalyzeBodyPartsTool.ExceptionResult(exception);
            }
        }
    }

    public sealed class ApplySmartBodySplitTool : IMcpTool
    {
        public string Name => "mesh_apply_smart_split";
        public string Description =>
            "Creates validated body-part mesh assets and scene renderers from an approved preview. " +
            "Requires exact sourceHash/previewHash and high-risk confirmation; the imported source mesh is never modified.";
        public JObject InputSchema => SmartBodySplitToolSchemas.ApplyInput;
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["splitId"] = new JObject { ["type"] = "string" },
                ["outputFolder"] = new JObject { ["type"] = "string" },
                ["preset"] = new JObject { ["type"] = "string" },
                ["partsRoot"] = SchemaHelpers.TargetRefSchema,
                ["bodyPartSetPath"] = new JObject { ["type"] = "string" },
                ["manifestPath"] = new JObject { ["type"] = "string" },
                ["partCount"] = new JObject { ["type"] = "integer" },
                ["createdAssets"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                ["valid"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray
            {
                "splitId", "outputFolder", "preset", "partsRoot", "bodyPartSetPath",
                "manifestPath", "partCount", "createdAssets", "valid",
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "mesh",
            Capabilities = McpCapability.SceneWrite | McpCapability.AssetWrite | McpCapability.Destructive,
            Risk = McpRiskLevel.High,
            ConfirmationPolicy = McpConfirmationPolicy.Required,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = false,
            ExecutionRequirements = McpExecutionRequirement.MainThread | McpExecutionRequirement.EditorUpdates,
            HeadlessSafe = true,
            OutputSchema = OutputSchema,
        };

        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var proposal = SmartBodySplitAnalyzer.Analyze(target, AnalyzeBodyPartsTool.ReadPreset(arguments));
                var result = SmartBodySplitService.Apply(
                    proposal,
                    (string)arguments["outputFolder"],
                    (string)arguments["expectedSourceHash"],
                    (string)arguments["previewHash"]);
                return ToolResult.Ok(
                    $"Created and validated {result.Manifest.Parts.Count} body parts.",
                    new JObject
                    {
                        ["splitId"] = result.SplitId,
                        ["outputFolder"] = result.OutputFolder,
                        ["preset"] = SmartBodySplitPresets.Id(proposal.Preset),
                        ["partsRoot"] = ToolTargetRef.Capture(result.PartsRoot).ToJson(),
                        ["bodyPartSetPath"] = AssetDatabase.GetAssetPath(result.BodyPartSet),
                        ["manifestPath"] = AssetDatabase.GetAssetPath(result.Manifest),
                        ["partCount"] = result.Manifest.Parts.Count,
                        ["createdAssets"] = new JArray(result.CreatedAssetPaths),
                        ["valid"] = result.Validation.IsValid,
                    });
            }
            catch (Exception exception)
            {
                return AnalyzeBodyPartsTool.ExceptionResult(exception);
            }
        }
    }

    public sealed class ValidateSmartBodySplitTool : IMcpTool
    {
        public string Name => "mesh_validate_split";
        public string Description => "Reads a Smart Body Split manifest and reports its persisted validation status and part inventory.";
        public JObject InputSchema => SmartBodySplitToolSchemas.ValidateInput;
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["manifestAssetPath"] = new JObject { ["type"] = "string" },
                ["splitId"] = new JObject { ["type"] = "string" },
                ["sourceHash"] = new JObject { ["type"] = "string" },
                ["previewHash"] = new JObject { ["type"] = "string" },
                ["preset"] = new JObject { ["type"] = "string" },
                ["valid"] = new JObject { ["type"] = "boolean" },
                ["triangleCount"] = new JObject { ["type"] = "integer" },
                ["parts"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["slotId"] = new JObject { ["type"] = "string" },
                            ["assetPath"] = new JObject { ["type"] = "string" },
                            ["triangleCount"] = new JObject { ["type"] = "integer" },
                            ["vertexCount"] = new JObject { ["type"] = "integer" },
                            ["confidence"] = new JObject { ["type"] = "number" },
                        },
                        ["additionalProperties"] = false,
                    },
                },
                ["messages"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            },
            ["required"] = new JArray
            {
                "manifestAssetPath", "splitId", "sourceHash", "previewHash", "preset",
                "valid", "triangleCount", "parts", "messages",
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => AnalyzeBodyPartsTool.ReadDescriptor(Name, OutputSchema, false);

        public ToolResult Execute(JObject arguments)
        {
            var path = (string)arguments["manifestAssetPath"];
            var manifest = AssetDatabase.LoadAssetAtPath<SmartBodySplitManifest>(path);
            if (manifest == null) return ToolResult.Error("MANIFEST_NOT_FOUND", $"No Smart Body Split manifest exists at '{path}'.");
            var parts = new JArray();
            for (int i = 0; i < manifest.Parts.Count; i++)
            {
                var part = manifest.Parts[i];
                parts.Add(new JObject
                {
                    ["slotId"] = part.SlotId,
                    ["assetPath"] = part.AssetPath,
                    ["triangleCount"] = part.SourceTriangleCount,
                    ["vertexCount"] = part.Mesh != null ? part.Mesh.vertexCount : 0,
                    ["confidence"] = part.Confidence,
                });
            }
            return ToolResult.Ok(
                manifest.IsValid ? "Split manifest is valid." : "Split manifest contains validation failures.",
                new JObject
                {
                    ["manifestAssetPath"] = path,
                    ["splitId"] = manifest.SplitId,
                    ["sourceHash"] = manifest.SourceHash,
                    ["previewHash"] = manifest.PreviewHash,
                    ["preset"] = SmartBodySplitPresets.Id(manifest.Preset),
                    ["valid"] = manifest.IsValid,
                    ["triangleCount"] = manifest.SourceTriangleCount,
                    ["parts"] = parts,
                    ["messages"] = new JArray(manifest.ValidationMessages),
                });
        }
    }
}
