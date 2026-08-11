using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    internal static class Ai3dMcpSchemas
    {
        public static JObject EmptyInput => Object(new JObject());

        public static JObject Hash => new JObject
        {
            ["type"] = "string",
            ["pattern"] = "^[0-9a-f]{64}$",
        };

        public static JObject WorkflowId => new JObject
        {
            ["type"] = "string",
            ["pattern"] = "^[0-9a-f]{32}$",
        };

        public static JObject WorkflowInput(bool includePlanHash = false)
        {
            var properties = new JObject { ["workflowId"] = WorkflowId };
            var required = new JArray { "workflowId" };
            if (includePlanHash)
            {
                properties["planHash"] = Hash;
                required.Add("planHash");
            }
            return Object(properties, required);
        }

        public static JObject WorkflowOutput => Object(
            new JObject
            {
                ["schemaVersion"] = Integer,
                ["workflowId"] = WorkflowId,
                ["planHash"] = Hash,
                ["provider"] = String,
                ["status"] = String,
                ["workflowKind"] = String,
                ["preset"] = String,
                ["sourceKind"] = String,
                ["inputHash"] = Hash,
                ["inputSummary"] = NullableString,
                ["modelVersion"] = String,
                ["autoConvertToFbx"] = Boolean,
                ["estimatedCredits"] = Integer,
                ["budgetCapCredits"] = Integer,
                ["creditsConsumed"] = Integer,
                ["currentNode"] = NullableString,
                ["currentNodeIndex"] = Integer,
                ["nodes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["nodeJobs"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["generationJobId"] = NullableString,
                ["conversionJobId"] = NullableString,
                ["currentJobId"] = NullableString,
                ["providerTaskId"] = NullableString,
                ["progress"] = Number,
                ["message"] = NullableString,
                ["candidateHash"] = NullableString,
                ["validationHash"] = NullableString,
                ["previewHash"] = NullableString,
                ["stagedAssetPath"] = NullableString,
                ["manifestAssetPath"] = NullableString,
                ["acceptedAssetPath"] = NullableString,
                ["sourceAssetPath"] = NullableString,
                ["qualityReport"] = new JObject { ["type"] = new JArray { "object", "null" } },
                ["validationIssues"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["error"] = new JObject { ["type"] = new JArray { "object", "null" } },
                ["candidateReady"] = Boolean,
                ["sceneMutated"] = Boolean,
                ["createdUtc"] = String,
                ["updatedUtc"] = String,
            },
            new JArray
            {
                "schemaVersion", "workflowId", "planHash", "provider", "status", "workflowKind", "preset",
                "sourceKind", "inputHash", "modelVersion", "autoConvertToFbx",
                "estimatedCredits", "budgetCapCredits", "creditsConsumed", "progress",
                "candidateReady", "sceneMutated", "createdUtc", "updatedUtc",
            });

        public static JObject PlanOutput => Object(
            new JObject
            {
                ["schemaVersion"] = Integer,
                ["workflowId"] = WorkflowId,
                ["planHash"] = Hash,
                ["provider"] = String,
                ["workflowKind"] = String,
                ["preset"] = String,
                ["source"] = new JObject { ["type"] = "object" },
                ["modelVersion"] = String,
                ["nodes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["requiredDisclosureKinds"] = new JObject { ["type"] = "array", ["items"] = String },
                ["estimatedCredits"] = Integer,
                ["budgetCapCredits"] = Integer,
                ["maximumPolicyCredits"] = Integer,
                ["requiresExternalSendApproval"] = Boolean,
                ["requiresConfirmation"] = Boolean,
                ["createsCandidateOnly"] = Boolean,
                ["createdUtc"] = String,
            },
            new JArray
            {
                "schemaVersion", "workflowId", "planHash", "provider", "workflowKind", "preset",
                "source", "modelVersion", "nodes", "requiredDisclosureKinds", "estimatedCredits",
                "budgetCapCredits", "maximumPolicyCredits", "requiresExternalSendApproval",
                "requiresConfirmation", "createsCandidateOnly", "createdUtc",
            });

        public static JObject ProvidersOutput => Object(
            new JObject
            {
                ["providers"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["localPolicy"] = new JObject { ["type"] = "object" },
            }, new JArray { "providers", "localPolicy" });

        public static JObject String => new JObject { ["type"] = "string" };
        public static JObject NullableString => new JObject { ["type"] = new JArray { "string", "null" } };
        public static JObject Integer => new JObject { ["type"] = "integer" };
        public static JObject Number => new JObject { ["type"] = "number" };
        public static JObject Boolean => new JObject { ["type"] = "boolean" };

        public static JObject Object(JObject properties, JArray required = null)
        {
            var result = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
            if (required != null && required.Count > 0) result["required"] = required;
            return result;
        }

        public static ToolDescriptor Descriptor(
            string name,
            JObject output,
            McpCapability capability,
            McpRiskLevel risk,
            McpConfirmationPolicy confirmation,
            bool idempotent,
            McpExecutionRequirement requirements = McpExecutionRequirement.None,
            bool headlessSafe = true) => new ToolDescriptor
        {
            Name = name,
            Category = "ai3d",
            Capabilities = capability,
            Risk = risk,
            ConfirmationPolicy = confirmation,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = idempotent,
            ExecutionRequirements = requirements,
            HeadlessSafe = headlessSafe,
            OutputSchema = output,
        };
    }

    internal static class Ai3dMcpToolUtility
    {
        public static ToolResult Error(Exception exception)
        {
            var root = exception.GetBaseException();
            if (root is OperationCanceledException)
                return ToolResult.Error("CANCELLED", "AI 3D operation was cancelled.");
            if (root is Ai3dWorkflowException workflow)
                return ToolResult.Error(workflow.Code, workflow.Message, workflow.DataToken);
            if (root is Ai3dGatewayException gateway)
                return ToolResult.Error(gateway.Code, gateway.Message, new JObject
                {
                    ["statusCode"] = gateway.StatusCode,
                    ["requestId"] = gateway.RequestId,
                });
            return ToolResult.Error("AI3D_OPERATION_FAILED", root.Message);
        }

        public static IEnumerable<string> Strings(JToken token)
        {
            if (!(token is JArray values)) yield break;
            for (int i = 0; i < values.Count; i++)
                if (values[i].Type == JTokenType.String) yield return (string)values[i];
        }
    }

    public sealed class Ai3dListProvidersTool : IMcpTool
    {
        public string Name => "ai3d_list_providers";
        public string Description => "Lists configured AI 3D providers, typed operations, preset availability and the local paid-work policy. It never returns credentials; optional balance lookup does not consume credits.";
        public JObject InputSchema => Ai3dMcpSchemas.Object(new JObject
        {
            ["includeBalance"] = new JObject { ["type"] = "boolean", ["default"] = false },
        });
        public JObject OutputSchema => Ai3dMcpSchemas.ProvidersOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low,
            McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments) => ToolResult.Error("ASYNC_REQUIRED", Name + " must use ExecuteAsync.");
        public async Task<ToolResult> ExecuteAsync(JObject arguments)
        {
            try
            {
                var context = ToolExecutionContext.Current;
                var json = await Ai3dWorkflowService.ProviderStatusAsync(
                    (bool?)arguments["includeBalance"] ?? false,
                    context != null ? context.CancellationToken : default);
                return ToolResult.Ok("AI 3D provider inventory loaded without exposing secrets.", json);
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dPlanWorkflowTool : IMcpTool
    {
        public string Name => "ai3d_plan_workflow";
        public string Description => "Builds a non-billable, hash-bound Tripo or Meshy generation, retopology, segmentation/completion or rig/animation workflow. Provider auto prefers Meshy except for Tripo-only segmentation. Local inputs are hash-checked; no data is sent and no provider job is started.";
        public JObject InputSchema => Ai3dMcpSchemas.Object(new JObject
        {
            ["provider"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray { "tripo", "meshy", "auto" },
                ["default"] = "tripo",
            },
            ["workflowKind"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray { "generate_asset", "retopology", "segment_complete", "rig_animation" },
                ["default"] = "generate_asset",
            },
            ["sourceKind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "text", "image", "mesh" } },
            ["prompt"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
            ["imagePath"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 },
            ["meshPath"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 },
            ["preset"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray
                {
                    "game-mobile", "game-pc", "hero-prop", "character-humanoid",
                    "character-creature", "modular-character", "wearable", "animation-pack",
                },
                ["default"] = "game-pc",
            },
            ["modelVersion"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
            ["autoConvertToFbx"] = new JObject { ["type"] = "boolean", ["default"] = true },
            ["faceLimit"] = new JObject { ["type"] = "integer", ["minimum"] = 500, ["maximum"] = 2000000 },
            ["smartLowPoly"] = new JObject { ["type"] = "boolean" },
            ["geometryQuality"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "standard", "detailed" } },
            ["texture"] = new JObject { ["type"] = "boolean" },
            ["pbr"] = new JObject { ["type"] = "boolean" },
            ["quad"] = new JObject { ["type"] = "boolean", ["default"] = false },
            ["bake"] = new JObject { ["type"] = "boolean", ["default"] = true },
            ["retopologyModel"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "v2.0", "v1.0" } },
            ["partNames"] = new JObject { ["type"] = "array", ["items"] = Ai3dMcpSchemas.String, ["maxItems"] = 64 },
            ["segmentationModel"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "v2.0-20260430", "v1.0-20250506" } },
            ["segmentationGranularity"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "simple", "balanced", "detailed" } },
            ["splitByConnectivity"] = new JObject { ["type"] = "boolean", ["default"] = true },
            ["completionMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "none", "quick_cap", "ai_completion" } },
            ["rigModel"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
            ["rigType"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "auto", "biped", "quadruped", "hexapod", "octopod", "avian", "serpentine", "aquatic" } },
            ["rigSpec"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "mixamo", "tripo" } },
            ["animations"] = new JObject { ["type"] = "array", ["items"] = Ai3dMcpSchemas.String, ["maxItems"] = 5 },
            ["animateInPlace"] = new JObject { ["type"] = "boolean", ["default"] = false },
            ["heightMeters"] = new JObject { ["type"] = "number", ["exclusiveMinimum"] = 0.0, ["default"] = 1.7 },
            ["animationActionIds"] = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                ["maxItems"] = 5,
            },
            ["animationFps"] = new JObject
            {
                ["type"] = "integer",
                ["enum"] = new JArray { 24, 25, 30, 60 },
                ["default"] = 30,
            },
            ["estimatedCredits"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100000 },
            ["budgetCapCredits"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100000 },
        }, new JArray { "sourceKind", "preset" });
        public JObject OutputSchema => Ai3dMcpSchemas.PlanOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low,
            McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments)
        {
            try
            {
                var plan = Ai3dWorkflowService.Plan(arguments);
                return ToolResult.Ok(
                    $"Workflow planned locally: estimate {plan.EstimatedCredits}, approved cap {plan.BudgetCapCredits}. No data was sent and no credits were consumed.",
                    plan.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dStartWorkflowTool : IMcpTool
    {
        public string Name => "ai3d_start_workflow";
        public string Description => "Starts exactly one reviewed ai3d plan. Paid external AI must be enabled; the plan hash, budget, disclosure consent and a single-use MCP confirmation are all mandatory.";
        public JObject InputSchema => Ai3dMcpSchemas.Object(new JObject
        {
            ["workflowId"] = Ai3dMcpSchemas.WorkflowId,
            ["planHash"] = Ai3dMcpSchemas.Hash,
            ["approvedBudgetCapCredits"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100000 },
            ["externalSendApproved"] = new JObject { ["type"] = "boolean" },
            ["approvedDisclosureKinds"] = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "text", "image", "mesh" } },
                ["minItems"] = 1,
                ["maxItems"] = 3,
            },
        }, new JArray
        {
            "workflowId", "planHash", "approvedBudgetCapCredits",
            "externalSendApproved", "approvedDisclosureKinds",
        });
        public JObject OutputSchema => Ai3dMcpSchemas.WorkflowOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.ExternalService, McpRiskLevel.Critical,
            McpConfirmationPolicy.Required, true,
            McpExecutionRequirement.EditorUpdates);
        public ToolResult Execute(JObject arguments) => ToolResult.Error("ASYNC_REQUIRED", Name + " must use ExecuteAsync.");
        public async Task<ToolResult> ExecuteAsync(JObject arguments)
        {
            try
            {
                var context = ToolExecutionContext.Current;
                var record = await Ai3dWorkflowService.StartAsync(
                    (string)arguments["workflowId"], (string)arguments["planHash"],
                    (int)arguments["approvedBudgetCapCredits"],
                    (bool)arguments["externalSendApproved"],
                    Ai3dMcpToolUtility.Strings(arguments["approvedDisclosureKinds"]),
                    context != null ? context.CancellationToken : default);
                return ToolResult.Ok("AI 3D workflow started idempotently; outputs remain outside the scene.", record.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dGetWorkflowTool : IMcpTool
    {
        public string Name => "ai3d_get_workflow";
        public string Description => "Returns the durable local workflow state without starting new paid nodes.";
        public JObject InputSchema => Ai3dMcpSchemas.WorkflowInput();
        public JObject OutputSchema => Ai3dMcpSchemas.WorkflowOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low,
            McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments)
        {
            try
            {
                var record = Ai3dWorkflowService.Get((string)arguments["workflowId"]);
                return ToolResult.Ok("AI 3D workflow state loaded.", record.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dWaitWorkflowTool : IMcpTool
    {
        public string Name => "ai3d_wait_workflow";
        public string Description => "Waits cooperatively for a workflow to leave its remote-running state, with a bounded timeout and without blocking Unity's main thread.";
        public JObject InputSchema => Ai3dMcpSchemas.Object(new JObject
        {
            ["workflowId"] = Ai3dMcpSchemas.WorkflowId,
            ["timeoutSeconds"] = new JObject { ["type"] = "number", ["minimum"] = 0.1, ["maximum"] = 300, ["default"] = 30 },
        }, new JArray { "workflowId" });
        public JObject OutputSchema => Ai3dMcpSchemas.WorkflowOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low,
            McpConfirmationPolicy.None, true,
            McpExecutionRequirement.EditorUpdates);
        public ToolResult Execute(JObject arguments) => ToolResult.Error("ASYNC_REQUIRED", Name + " must use ExecuteAsync.");
        public async Task<ToolResult> ExecuteAsync(JObject arguments)
        {
            try
            {
                var context = ToolExecutionContext.Current;
                var record = await Ai3dWorkflowService.WaitAsync(
                    (string)arguments["workflowId"], (double?)arguments["timeoutSeconds"] ?? 30d,
                    context != null ? context.CancellationToken : default);
                return ToolResult.Ok("AI 3D workflow wait completed with status " + record.Status + ".", record.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dCancelWorkflowTool : IMcpTool
    {
        public string Name => "ai3d_cancel_workflow";
        public string Description => "Requests cooperative cancellation. Tripo may still finish and charge an already submitted task; this tool never promises a refund or remote cancellation.";
        public JObject InputSchema => Ai3dMcpSchemas.WorkflowInput(true);
        public JObject OutputSchema => Ai3dMcpSchemas.WorkflowOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.ExternalService, McpRiskLevel.High,
            McpConfirmationPolicy.Required, true, McpExecutionRequirement.EditorUpdates);
        public ToolResult Execute(JObject arguments) => ToolResult.Error("ASYNC_REQUIRED", Name + " must use ExecuteAsync.");
        public async Task<ToolResult> ExecuteAsync(JObject arguments)
        {
            try
            {
                var context = ToolExecutionContext.Current;
                var record = await Ai3dWorkflowService.CancelAsync(
                    (string)arguments["workflowId"], (string)arguments["planHash"],
                    context != null ? context.CancellationToken : default);
                return ToolResult.Ok("AI 3D cancellation requested; provider reconciliation remains honest.", record.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dValidateCandidateTool : IMcpTool
    {
        public string Name => "ai3d_validate_candidate";
        public string Description => "Validates the authenticated vault artifact, copies only a verified FBX to Assets/AI3D/Quarantine, configures ModelImporter and runs local mesh validation. It never changes the scene.";
        public JObject InputSchema => Ai3dMcpSchemas.WorkflowInput(true);
        public JObject OutputSchema => Ai3dMcpSchemas.WorkflowOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.Medium,
            McpConfirmationPolicy.Required, true, McpExecutionRequirement.MainThread);
        public ToolResult Execute(JObject arguments) => ToolResult.Error("ASYNC_REQUIRED", Name + " must use ExecuteAsync.");
        public async Task<ToolResult> ExecuteAsync(JObject arguments)
        {
            try
            {
                var context = ToolExecutionContext.Current;
                var record = await Ai3dWorkflowService.ValidateCandidateAsync(
                    (string)arguments["workflowId"], (string)arguments["planHash"],
                    context != null ? context.CancellationToken : default);
                return ToolResult.Ok("Candidate validated and staged in quarantine; scene remains unchanged.", record.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dPreviewCandidateTool : IMcpTool
    {
        public string Name => "ai3d_preview_candidate";
        public string Description => "Displays the current validated candidate directly in Scene View without creating a GameObject, frames it, and binds previewHash to the current validationHash.";
        public JObject InputSchema => Ai3dMcpSchemas.WorkflowInput(true);
        public JObject OutputSchema => Ai3dMcpSchemas.WorkflowOutput;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low,
            McpConfirmationPolicy.None, true,
            McpExecutionRequirement.MainThread | McpExecutionRequirement.EditorUpdates | McpExecutionRequirement.SceneView,
            false);
        public ToolResult Execute(JObject arguments)
        {
            try
            {
                var record = Ai3dWorkflowService.PreviewCandidate(
                    (string)arguments["workflowId"], (string)arguments["planHash"]);
                return ToolResult.Ok(
                    "Candidate is framed in Scene View. Review it visually (or call screenshot_scene_view) before acceptance.",
                    record.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dAcceptCandidateTool : IMcpTool
    {
        public string Name => "ai3d_accept_candidate";
        public string Description => "Promotes only the currently reviewed candidate when planHash, candidateHash, validationHash and previewHash all match. It creates an accepted asset copy but never instantiates it in the scene.";
        public JObject InputSchema => Ai3dMcpSchemas.Object(new JObject
        {
            ["workflowId"] = Ai3dMcpSchemas.WorkflowId,
            ["planHash"] = Ai3dMcpSchemas.Hash,
            ["candidateHash"] = Ai3dMcpSchemas.Hash,
            ["validationHash"] = Ai3dMcpSchemas.Hash,
            ["previewHash"] = Ai3dMcpSchemas.Hash,
            ["acceptedRoot"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets(?:/|$)" },
        }, new JArray { "workflowId", "planHash", "candidateHash", "validationHash", "previewHash" });
        public JObject OutputSchema => Ai3dMcpSchemas.WorkflowOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.High,
            McpConfirmationPolicy.Required, false, McpExecutionRequirement.MainThread);
        public ToolResult Execute(JObject arguments)
        {
            try
            {
                var record = Ai3dWorkflowService.AcceptCandidate(
                    (string)arguments["workflowId"], (string)arguments["planHash"],
                    (string)arguments["candidateHash"], (string)arguments["validationHash"],
                    (string)arguments["previewHash"], (string)arguments["acceptedRoot"]);
                return ToolResult.Ok("Candidate accepted as a project asset; no scene object was created.", record.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    public sealed class Ai3dRejectCandidateTool : IMcpTool
    {
        public string Name => "ai3d_reject_candidate";
        public string Description => "Rejects the current hash-bound candidate and clears its Scene View preview. Quarantine files are retained for recovery and nothing is deleted silently.";
        public JObject InputSchema => Ai3dMcpSchemas.Object(new JObject
        {
            ["workflowId"] = Ai3dMcpSchemas.WorkflowId,
            ["planHash"] = Ai3dMcpSchemas.Hash,
            ["candidateHash"] = Ai3dMcpSchemas.Hash,
        }, new JArray { "workflowId", "planHash", "candidateHash" });
        public JObject OutputSchema => Ai3dMcpSchemas.WorkflowOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => Ai3dMcpSchemas.Descriptor(
            Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.Medium,
            McpConfirmationPolicy.Required, true, McpExecutionRequirement.MainThread);
        public ToolResult Execute(JObject arguments)
        {
            try
            {
                var record = Ai3dWorkflowService.RejectCandidate(
                    (string)arguments["workflowId"], (string)arguments["planHash"],
                    (string)arguments["candidateHash"]);
                return ToolResult.Ok("Candidate rejected; quarantine files were preserved.", record.ToPublicJson());
            }
            catch (Exception exception) { return Ai3dMcpToolUtility.Error(exception); }
        }
    }

    [InitializeOnLoad]
    internal static class Ai3dMcpBootstrap
    {
        static Ai3dMcpBootstrap() => EditorApplication.delayCall += Initialize;

        internal static void Initialize()
        {
            try
            {
                if (Bootstrap.ToolRegistry == null)
                {
                    EditorApplication.delayCall += Initialize;
                    return;
                }
                if (!Bootstrap.ToolRegistry.TryGet("ai3d_list_providers", out _))
                {
                    Bootstrap.ToolRegistry.RegisterRange(new IMcpTool[]
                    {
                        new Ai3dListProvidersTool(),
                        new Ai3dPlanWorkflowTool(),
                        new Ai3dStartWorkflowTool(),
                        new Ai3dGetWorkflowTool(),
                        new Ai3dWaitWorkflowTool(),
                        new Ai3dCancelWorkflowTool(),
                        new Ai3dPreviewCandidateTool(),
                        new Ai3dValidateCandidateTool(),
                        new Ai3dAcceptCandidateTool(),
                        new Ai3dRejectCandidateTool(),
                    });
                    Bootstrap.RecordEntry("[ai3d] registered 10 workflow tools");
                }
                Ai3dWorkflowService.ResumeActiveWorkflows();
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
