using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    internal sealed class Ai3dWorkflowPresetDefinition
    {
        public string Id;
        public string Label;
        public bool Available;
        public string UnavailableReason;
        public int FaceLimit;
        public bool SmartLowPoly;
        public string GeometryQuality;
        public int GenerationEstimate;
        public int GenerationCap;
        public bool Texture = true;
        public bool Pbr = true;

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["label"] = Label,
            ["available"] = Available,
            ["unavailableReason"] = UnavailableReason,
            ["faceLimit"] = FaceLimit,
            ["smartLowPoly"] = SmartLowPoly,
            ["geometryQuality"] = GeometryQuality,
            ["estimatedCredits"] = GenerationEstimate + 10,
            ["defaultBudgetCapCredits"] = GenerationCap + 15,
        };
    }

    internal static class Ai3dWorkflowCatalog
    {
        private static readonly Ai3dWorkflowPresetDefinition[] Definitions =
        {
            Supported("game-mobile", "Game Mobile", 15000, true, "standard", 100, 150),
            Supported("game-pc", "Game PC", 50000, false, "standard", 120, 180),
            Supported("hero-prop", "Hero Prop", 80000, false, "detailed", 160, 240),
            Supported("character-humanoid", "Character Humanoid", 50000, false, "detailed", 160, 240),
            Supported("character-creature", "Character Creature", 20000, true, "detailed", 160, 240),
            Supported("modular-character", "Modular Character", 50000, false, "detailed", 160, 240),
            Future("wearable", "Wearable", "Pendente da integração automática de Surface Fit multipose e transferência de pesos."),
            Supported("animation-pack", "Animation Pack", 50000, false, "detailed", 160, 240),
        };

        public static IEnumerable<Ai3dWorkflowPresetDefinition> All => Definitions;

        public static bool TryGet(string id, out Ai3dWorkflowPresetDefinition definition)
        {
            for (int i = 0; i < Definitions.Length; i++)
            {
                if (!string.Equals(Definitions[i].Id, id, StringComparison.Ordinal)) continue;
                definition = Definitions[i];
                return true;
            }
            definition = null;
            return false;
        }

        private static Ai3dWorkflowPresetDefinition Supported(
            string id, string label, int faceLimit, bool smartLowPoly, string quality,
            int estimate, int cap) => new Ai3dWorkflowPresetDefinition
        {
            Id = id,
            Label = label,
            Available = true,
            FaceLimit = faceLimit,
            SmartLowPoly = smartLowPoly,
            GeometryQuality = quality,
            GenerationEstimate = estimate,
            GenerationCap = cap,
        };

        private static Ai3dWorkflowPresetDefinition Future(string id, string label, string reason)
            => new Ai3dWorkflowPresetDefinition
            {
                Id = id,
                Label = label,
                Available = false,
                UnavailableReason = reason,
            };
    }

    public sealed class Ai3dWorkflowPlan
    {
        public const int SchemaVersion = 1;

        public string WorkflowId;
        public string PlanHash;
        public string WorkflowKind = "generate_asset";
        public string Preset;
        public string SourceKind;
        public string InputHash;
        public string InputSummary;
        public string ModelVersion;
        public int FaceLimit;
        public bool Texture;
        public bool Pbr;
        public bool SmartLowPoly;
        public string GeometryQuality;
        public bool AutoConvertToFbx;
        public int GenerationEstimatedCredits;
        public int GenerationCapCredits;
        public int ConvertEstimatedCredits;
        public int ConvertCapCredits;
        public int EstimatedCredits;
        public int BudgetCapCredits;
        public string CreatedUtc;
        public string[] RequiredDisclosureKinds = Array.Empty<string>();
        public string SourceAssetPath;
        internal readonly List<Ai3dWorkflowNodePlan> Nodes = new List<Ai3dWorkflowNodePlan>();

        internal string Prompt;
        internal string SourcePath;

        public JObject ToPublicJson()
        {
            var disclosures = new JArray();
            for (int i = 0; i < RequiredDisclosureKinds.Length; i++) disclosures.Add(RequiredDisclosureKinds[i]);
            return new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["workflowId"] = WorkflowId,
                ["planHash"] = PlanHash,
                ["provider"] = "tripo",
                ["workflowKind"] = WorkflowKind,
                ["preset"] = Preset,
                ["source"] = new JObject
                {
                    ["kind"] = SourceKind,
                    ["hash"] = InputHash,
                    ["summary"] = InputSummary,
                },
                ["modelVersion"] = ModelVersion,
                ["nodes"] = BuildNodes(false),
                ["qualityGate"] = BuildQualityGate(),
                ["requiredDisclosureKinds"] = disclosures,
                ["estimatedCredits"] = EstimatedCredits,
                ["budgetCapCredits"] = BudgetCapCredits,
                ["maximumPolicyCredits"] = Ai3dGatewaySettings.MaxCreditsPerWorkflow,
                ["requiresExternalSendApproval"] = true,
                ["requiresConfirmation"] = true,
                ["createsCandidateOnly"] = true,
                ["createdUtc"] = CreatedUtc,
            };
        }

        internal JObject CanonicalJson()
        {
            var disclosures = new JArray();
            for (int i = 0; i < RequiredDisclosureKinds.Length; i++) disclosures.Add(RequiredDisclosureKinds[i]);
            return new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["projectKey"] = Ai3dGatewaySettings.ProjectKey,
                ["provider"] = "tripo",
                ["workflowKind"] = WorkflowKind,
                ["preset"] = Preset,
                ["sourceKind"] = SourceKind,
                ["inputHash"] = InputHash,
                ["prompt"] = SourceKind == "text" ? Prompt : null,
                ["modelVersion"] = ModelVersion,
                ["faceLimit"] = FaceLimit,
                ["texture"] = Texture,
                ["pbr"] = Pbr,
                ["smartLowPoly"] = SmartLowPoly,
                ["geometryQuality"] = GeometryQuality,
                ["autoConvertToFbx"] = AutoConvertToFbx,
                ["generationEstimatedCredits"] = GenerationEstimatedCredits,
                ["generationCapCredits"] = GenerationCapCredits,
                ["convertEstimatedCredits"] = ConvertEstimatedCredits,
                ["convertCapCredits"] = ConvertCapCredits,
                ["estimatedCredits"] = EstimatedCredits,
                ["budgetCapCredits"] = BudgetCapCredits,
                ["requiredDisclosureKinds"] = disclosures,
                ["nodes"] = BuildNodes(true),
            };
        }

        internal JArray BuildNodes(bool includePayload = false)
        {
            if (Nodes.Count > 0) return Ai3dWorkflowNodePlan.ToJson(Nodes, includePayload);
            var result = new JArray
            {
                new JObject
                {
                    ["id"] = "generate",
                    ["operation"] = SourceKind == "image" ? "image_to_model" : "text_to_model",
                    ["estimatedCredits"] = GenerationEstimatedCredits,
                    ["budgetCapCredits"] = GenerationCapCredits,
                },
            };
            if (AutoConvertToFbx)
            {
                result.Add(new JObject
                {
                    ["id"] = "convert_fbx",
                    ["operation"] = "convert",
                    ["estimatedCredits"] = ConvertEstimatedCredits,
                    ["budgetCapCredits"] = ConvertCapCredits,
                });
            }
            return result;
        }

        private JObject BuildQualityGate()
        {
            switch (WorkflowKind)
            {
                case "retopology":
                    return new JObject
                    {
                        ["triangleBudget"] = FaceLimit,
                        ["requiresSourceComparison"] = true,
                        ["requiresSilhouetteReview"] = true,
                    };
                case "segment_complete":
                    return new JObject
                    {
                        ["requiresRigChannelAudit"] = true,
                        ["requiresClosedPartReview"] = true,
                    };
                case "rig_animation":
                    return new JObject
                    {
                        ["requiresRigDiagnostics"] = true,
                        ["requiresClipValidation"] = true,
                    };
                default:
                    return new JObject { ["requiresMeshValidation"] = true };
            }
        }
    }

    public sealed class Ai3dWorkflowRecord
    {
        public const int SchemaVersion = 1;

        public string WorkflowId;
        public string PlanHash;
        public string WorkflowKind = "generate_asset";
        public string Status;
        public string Preset;
        public string SourceKind;
        public string InputHash;
        public string InputSummary;
        public string ModelVersion;
        public bool AutoConvertToFbx;
        public int EstimatedCredits;
        public int BudgetCapCredits;
        public int CreditsConsumed;
        public string CurrentNode;
        public int CurrentNodeIndex;
        public JArray PipelineNodes = new JArray();
        public JArray NodeJobs = new JArray();
        public string GenerationJobId;
        public string ConversionJobId;
        public string CurrentJobId;
        public string ProviderTaskId;
        public string SourceProviderReference;
        public float Progress;
        public string Message;
        public string CandidateHash;
        public string ValidationHash;
        public string PreviewHash;
        public string StagedAssetPath;
        public string ManifestAssetPath;
        public string AcceptedAssetPath;
        public string SourceAssetPath;
        public JObject QualityReport;
        public JArray ValidationIssues = new JArray();
        public string ErrorCode;
        public string ErrorMessage;
        public string CreatedUtc;
        public string UpdatedUtc;

        internal Ai3dWorkflowPlan ResumePlan;
        internal string ResumeUploadFileToken;

        public bool IsTerminal => Status == "accepted" || Status == "rejected" ||
                                  Status == "cancelled" || Status == "failed";

        public JObject ToPublicJson() => ToJson(false);

        internal JObject ToJson(bool includeResume)
        {
            var json = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["workflowId"] = WorkflowId,
                ["planHash"] = PlanHash,
                ["provider"] = "tripo",
                ["status"] = Status,
                ["workflowKind"] = WorkflowKind,
                ["preset"] = Preset,
                ["sourceKind"] = SourceKind,
                ["inputHash"] = InputHash,
                ["inputSummary"] = InputSummary,
                ["modelVersion"] = ModelVersion,
                ["autoConvertToFbx"] = AutoConvertToFbx,
                ["estimatedCredits"] = EstimatedCredits,
                ["budgetCapCredits"] = BudgetCapCredits,
                ["creditsConsumed"] = CreditsConsumed,
                ["currentNode"] = CurrentNode,
                ["currentNodeIndex"] = CurrentNodeIndex,
                ["nodes"] = PipelineNodes?.DeepClone() ?? new JArray(),
                ["nodeJobs"] = NodeJobs?.DeepClone() ?? new JArray(),
                ["generationJobId"] = GenerationJobId,
                ["conversionJobId"] = ConversionJobId,
                ["currentJobId"] = CurrentJobId,
                ["providerTaskId"] = ProviderTaskId,
                ["progress"] = Progress,
                ["message"] = Message,
                ["candidateHash"] = CandidateHash,
                ["validationHash"] = ValidationHash,
                ["previewHash"] = PreviewHash,
                ["stagedAssetPath"] = StagedAssetPath,
                ["manifestAssetPath"] = ManifestAssetPath,
                ["acceptedAssetPath"] = AcceptedAssetPath,
                ["sourceAssetPath"] = SourceAssetPath,
                ["qualityReport"] = QualityReport?.DeepClone(),
                ["validationIssues"] = ValidationIssues?.DeepClone() ?? new JArray(),
                ["error"] = string.IsNullOrEmpty(ErrorCode) ? null : new JObject
                {
                    ["code"] = ErrorCode,
                    ["message"] = ErrorMessage,
                },
                ["candidateReady"] = Status == "awaiting_candidate" || Status == "candidate_validated" ||
                                     Status == "preview_ready" || Status == "quality_gate_failed" ||
                                     Status == "accepted" || Status == "rejected",
                ["sceneMutated"] = false,
                ["createdUtc"] = CreatedUtc,
                ["updatedUtc"] = UpdatedUtc,
            };
            if (includeResume && !string.IsNullOrEmpty(SourceProviderReference))
                json["sourceProviderReference"] = SourceProviderReference;
            if (includeResume && ResumePlan != null)
            {
                json["resume"] = new JObject
                {
                    ["prompt"] = ResumePlan.Prompt,
                    ["sourcePath"] = ResumePlan.SourcePath,
                    ["faceLimit"] = ResumePlan.FaceLimit,
                    ["texture"] = ResumePlan.Texture,
                    ["pbr"] = ResumePlan.Pbr,
                    ["smartLowPoly"] = ResumePlan.SmartLowPoly,
                    ["geometryQuality"] = ResumePlan.GeometryQuality,
                    ["generationEstimatedCredits"] = ResumePlan.GenerationEstimatedCredits,
                    ["generationCapCredits"] = ResumePlan.GenerationCapCredits,
                    ["convertEstimatedCredits"] = ResumePlan.ConvertEstimatedCredits,
                    ["convertCapCredits"] = ResumePlan.ConvertCapCredits,
                    ["uploadFileToken"] = ResumeUploadFileToken,
                    ["workflowKind"] = ResumePlan.WorkflowKind,
                    ["sourceAssetPath"] = ResumePlan.SourceAssetPath,
                    ["nodes"] = ResumePlan.BuildNodes(true),
                };
            }
            return json;
        }

        internal static Ai3dWorkflowRecord Parse(JObject json)
        {
            if (json == null || (int?)json["schemaVersion"] != SchemaVersion) return null;
            var record = new Ai3dWorkflowRecord
            {
                WorkflowId = (string)json["workflowId"],
                PlanHash = (string)json["planHash"],
                WorkflowKind = (string)json["workflowKind"] ?? "generate_asset",
                Status = (string)json["status"],
                Preset = (string)json["preset"],
                SourceKind = (string)json["sourceKind"],
                InputHash = (string)json["inputHash"],
                InputSummary = (string)json["inputSummary"],
                ModelVersion = (string)json["modelVersion"],
                AutoConvertToFbx = (bool?)json["autoConvertToFbx"] ?? true,
                EstimatedCredits = (int?)json["estimatedCredits"] ?? 0,
                BudgetCapCredits = (int?)json["budgetCapCredits"] ?? 0,
                CreditsConsumed = (int?)json["creditsConsumed"] ?? 0,
                CurrentNode = (string)json["currentNode"],
                CurrentNodeIndex = (int?)json["currentNodeIndex"] ?? 0,
                PipelineNodes = (json["nodes"] as JArray)?.DeepClone() as JArray ?? new JArray(),
                NodeJobs = (json["nodeJobs"] as JArray)?.DeepClone() as JArray ?? new JArray(),
                GenerationJobId = (string)json["generationJobId"],
                ConversionJobId = (string)json["conversionJobId"],
                CurrentJobId = (string)json["currentJobId"],
                ProviderTaskId = (string)json["providerTaskId"],
                SourceProviderReference = (string)json["sourceProviderReference"],
                Progress = (float?)json["progress"] ?? 0f,
                Message = (string)json["message"],
                CandidateHash = (string)json["candidateHash"],
                ValidationHash = (string)json["validationHash"],
                PreviewHash = (string)json["previewHash"],
                StagedAssetPath = (string)json["stagedAssetPath"],
                ManifestAssetPath = (string)json["manifestAssetPath"],
                AcceptedAssetPath = (string)json["acceptedAssetPath"],
                SourceAssetPath = (string)json["sourceAssetPath"],
                QualityReport = (json["qualityReport"] as JObject)?.DeepClone() as JObject,
                ValidationIssues = (json["validationIssues"] as JArray)?.DeepClone() as JArray ?? new JArray(),
                ErrorCode = (string)json["error"]?["code"],
                ErrorMessage = (string)json["error"]?["message"],
                CreatedUtc = (string)json["createdUtc"],
                UpdatedUtc = (string)json["updatedUtc"],
            };
            if (json["resume"] is JObject resume)
            {
                record.ResumePlan = new Ai3dWorkflowPlan
                {
                    WorkflowId = record.WorkflowId,
                    PlanHash = record.PlanHash,
                    WorkflowKind = (string)resume["workflowKind"] ?? record.WorkflowKind,
                    Preset = record.Preset,
                    SourceKind = record.SourceKind,
                    InputHash = record.InputHash,
                    InputSummary = record.InputSummary,
                    ModelVersion = record.ModelVersion,
                    AutoConvertToFbx = record.AutoConvertToFbx,
                    EstimatedCredits = record.EstimatedCredits,
                    BudgetCapCredits = record.BudgetCapCredits,
                    RequiredDisclosureKinds = new[] { record.SourceKind },
                    CreatedUtc = record.CreatedUtc,
                    SourceAssetPath = (string)resume["sourceAssetPath"] ?? record.SourceAssetPath,
                    Prompt = (string)resume["prompt"],
                    SourcePath = (string)resume["sourcePath"],
                    FaceLimit = (int?)resume["faceLimit"] ?? 50000,
                    Texture = (bool?)resume["texture"] ?? true,
                    Pbr = (bool?)resume["pbr"] ?? true,
                    SmartLowPoly = (bool?)resume["smartLowPoly"] ?? false,
                    GeometryQuality = (string)resume["geometryQuality"] ?? "standard",
                    GenerationEstimatedCredits = (int?)resume["generationEstimatedCredits"] ?? 0,
                    GenerationCapCredits = (int?)resume["generationCapCredits"] ?? 0,
                    ConvertEstimatedCredits = (int?)resume["convertEstimatedCredits"] ?? 10,
                    ConvertCapCredits = (int?)resume["convertCapCredits"] ?? 15,
                };
                foreach (var node in Ai3dWorkflowNodePlan.ParseMany(resume["nodes"] as JArray))
                    record.ResumePlan.Nodes.Add(node);
                if (record.ResumePlan.Nodes.Count == 0)
                {
                    record.ResumePlan.Nodes.Add(new Ai3dWorkflowNodePlan
                    {
                        Id = "generate",
                        Operation = record.SourceKind == "image" ? "image_to_model" : "text_to_model",
                        EstimatedCredits = record.ResumePlan.GenerationEstimatedCredits,
                        BudgetCapCredits = record.ResumePlan.GenerationCapCredits,
                        Payload = new JObject
                        {
                            ["model"] = record.ModelVersion,
                            ["face_limit"] = record.ResumePlan.FaceLimit,
                            ["texture"] = record.ResumePlan.Texture,
                            ["pbr"] = record.ResumePlan.Pbr,
                            ["geometry_quality"] = record.ResumePlan.GeometryQuality,
                        },
                    });
                    if (record.ResumePlan.SmartLowPoly)
                        record.ResumePlan.Nodes[0].Payload["smart_low_poly"] = true;
                    if (record.AutoConvertToFbx)
                        record.ResumePlan.Nodes.Add(LegacyConvertNode());
                }
                record.ResumeUploadFileToken = (string)resume["uploadFileToken"];
            }
            if (record.PipelineNodes.Count == 0)
            {
                var generationEstimate = Math.Max(0, record.EstimatedCredits - (record.AutoConvertToFbx ? 10 : 0));
                var generationCap = Math.Max(generationEstimate, record.BudgetCapCredits - (record.AutoConvertToFbx ? 15 : 0));
                var nodes = new List<Ai3dWorkflowNodePlan>
                {
                    new Ai3dWorkflowNodePlan
                    {
                        Id = "generate",
                        Operation = record.SourceKind == "image" ? "image_to_model" : "text_to_model",
                        EstimatedCredits = generationEstimate,
                        BudgetCapCredits = generationCap,
                    },
                };
                if (record.AutoConvertToFbx) nodes.Add(LegacyConvertNode());
                record.PipelineNodes = Ai3dWorkflowNodePlan.ToJson(nodes, true);
                record.CurrentNodeIndex = record.CurrentNode == "convert_fbx" || record.CurrentNode == "convert"
                    ? Math.Min(1, nodes.Count - 1) : 0;
            }
            return record;
        }

        private static Ai3dWorkflowNodePlan LegacyConvertNode() => new Ai3dWorkflowNodePlan
        {
            Id = "convert_fbx",
            Operation = "convert",
            EstimatedCredits = 10,
            BudgetCapCredits = 15,
            Payload = new JObject
            {
                ["format"] = "FBX",
                ["texture_size"] = 2048,
                ["texture_format"] = "PNG",
                ["bake"] = true,
                ["with_animation"] = true,
                ["fbx_preset"] = "blender",
            },
        };
    }
}
