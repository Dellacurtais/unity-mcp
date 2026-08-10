using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    internal interface IAi3dWorkflowGateway
    {
        Task<Ai3dGatewayHealth> HealthAsync(CancellationToken token);
        Task<JObject> CapabilitiesAsync(CancellationToken token);
        Task<Ai3dCredentialStatus> CredentialStatusAsync(CancellationToken token);
        Task<JObject> BalanceAsync(CancellationToken token);
        Task<Ai3dUploadSnapshot> UploadAsync(string path, CancellationToken token);
        Task<Ai3dJobSnapshot> CreateJobAsync(
            string operation, JObject payload, int estimate, int cap,
            IEnumerable<string> disclosureKinds, IEnumerable<string> sourceHashes,
            string idempotencyKey, string planHash, CancellationToken token);
        Task<Ai3dJobSnapshot> GetJobAsync(string jobId, CancellationToken token);
        Task<Ai3dJobSnapshot> CancelJobAsync(string jobId, CancellationToken token);
    }

    internal sealed class Ai3dWorkflowGateway : IAi3dWorkflowGateway
    {
        public Task<Ai3dGatewayHealth> HealthAsync(CancellationToken token)
            => Ai3dGatewayClient.HealthAsync(token);
        public Task<JObject> CapabilitiesAsync(CancellationToken token)
            => Ai3dGatewayClient.GetCapabilitiesAsync(token);
        public Task<Ai3dCredentialStatus> CredentialStatusAsync(CancellationToken token)
            => Ai3dGatewayClient.GetCredentialStatusAsync(token);
        public Task<JObject> BalanceAsync(CancellationToken token)
            => Ai3dGatewayClient.GetBalanceAsync(token);
        public Task<Ai3dUploadSnapshot> UploadAsync(string path, CancellationToken token)
            => Ai3dGatewayClient.UploadAsync(path, token);
        public Task<Ai3dJobSnapshot> CreateJobAsync(
            string operation, JObject payload, int estimate, int cap,
            IEnumerable<string> disclosureKinds, IEnumerable<string> sourceHashes,
            string idempotencyKey, string planHash, CancellationToken token)
            => Ai3dGatewayClient.CreateWorkflowJobAsync(
                operation, payload, estimate, cap, disclosureKinds, sourceHashes,
                idempotencyKey, planHash, token);
        public Task<Ai3dJobSnapshot> GetJobAsync(string jobId, CancellationToken token)
            => Ai3dGatewayClient.GetJobAsync(jobId, token);
        public Task<Ai3dJobSnapshot> CancelJobAsync(string jobId, CancellationToken token)
            => Ai3dGatewayClient.CancelJobAsync(jobId, token);
    }

    /// <summary>
    /// Shared workflow domain used by MCP and the AI 3D Studio. Planning is local
    /// and non-billable; starting is separately gated and creates only quarantined
    /// candidates. Runtime records live below Library, never in Assets or EditorPrefs.
    /// </summary>
    public static class Ai3dWorkflowService
    {
        private const int MaxPromptLength = 1024;
        private const long MaxImageBytes = 20L * 1024L * 1024L;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Ai3dWorkflowPlan> Plans =
            new Dictionary<string, Ai3dWorkflowPlan>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Ai3dWorkflowRecord> Records =
            new Dictionary<string, Ai3dWorkflowRecord>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Task> Runners =
            new Dictionary<string, Task>(StringComparer.Ordinal);
        private static readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        private static bool _resumed;

        internal static IAi3dWorkflowGateway Gateway { get; set; } = new Ai3dWorkflowGateway();
        internal static string StoreRootOverride { get; set; }

        public static Ai3dWorkflowPlan Plan(JObject arguments)
        {
            arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            var workflowKind = ((string)arguments["workflowKind"] ?? "generate_asset").Trim().ToLowerInvariant();
            if (workflowKind != "generate_asset" && workflowKind != "retopology" &&
                workflowKind != "segment_complete" && workflowKind != "rig_animation")
                throw new Ai3dWorkflowException(
                    "AI3D_WORKFLOW_KIND_INVALID",
                    "workflowKind must be generate_asset, retopology, segment_complete or rig_animation.");
            var presetId = ((string)arguments["preset"] ?? "game-pc").Trim();
            if (!Ai3dWorkflowCatalog.TryGet(presetId, out var preset))
                throw new Ai3dWorkflowException("AI3D_PRESET_UNKNOWN", "Unknown AI 3D preset: " + presetId);
            if (!preset.Available)
                throw new Ai3dWorkflowException("AI3D_PRESET_NOT_AVAILABLE", preset.UnavailableReason);

            var defaultSourceKind = workflowKind == "generate_asset" ? "text" : "mesh";
            var sourceKind = ((string)arguments["sourceKind"] ?? defaultSourceKind).Trim().ToLowerInvariant();
            if (workflowKind == "generate_asset" && sourceKind != "text" && sourceKind != "image")
                throw new Ai3dWorkflowException("AI3D_SOURCE_KIND_INVALID", "Generation sourceKind must be text or image.");
            if (workflowKind != "generate_asset" && sourceKind != "mesh")
                throw new Ai3dWorkflowException("AI3D_SOURCE_KIND_INVALID", "Processing workflows require sourceKind mesh.");

            string prompt = null;
            string sourcePath = null;
            string inputHash;
            string inputSummary;
            if (sourceKind == "text")
            {
                prompt = ((string)arguments["prompt"] ?? string.Empty).Trim();
                if (prompt.Length == 0 || prompt.Length > MaxPromptLength)
                    throw new Ai3dWorkflowException(
                        "AI3D_PROMPT_INVALID", $"prompt must contain 1-{MaxPromptLength} characters.");
                inputHash = Ai3dGatewaySettings.Sha256(prompt);
                inputSummary = prompt.Length + " characters; sha256 " + inputHash.Substring(0, 12);
            }
            else if (sourceKind == "image")
            {
                var requestedPath = (string)arguments["imagePath"];
                if (string.IsNullOrWhiteSpace(requestedPath))
                    throw new Ai3dWorkflowException("AI3D_IMAGE_REQUIRED", "imagePath is required for image workflows.");
                sourcePath = Path.GetFullPath(requestedPath);
                if (!File.Exists(sourcePath))
                    throw new Ai3dWorkflowException("AI3D_IMAGE_NOT_FOUND", "Image file was not found.");
                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
                    throw new Ai3dWorkflowException("AI3D_IMAGE_FORMAT_REJECTED", "Only PNG and JPEG images are allowed.");
                var info = new FileInfo(sourcePath);
                if (info.Length <= 0 || info.Length > MaxImageBytes)
                    throw new Ai3dWorkflowException("AI3D_IMAGE_SIZE_REJECTED", "Image must contain 1 byte to 20 MB.");
                inputHash = Ai3dGatewayClient.ComputeFileSha256(sourcePath);
                inputSummary = Path.GetFileName(sourcePath) + "; " + info.Length + " bytes; sha256 " +
                               inputHash.Substring(0, 12);
            }
            else
            {
                var requestedPath = (string)arguments["meshPath"];
                if (string.IsNullOrWhiteSpace(requestedPath))
                    throw new Ai3dWorkflowException("AI3D_MESH_REQUIRED", "meshPath is required for mesh workflows.");
                sourcePath = Path.GetFullPath(requestedPath);
                if (!File.Exists(sourcePath))
                    throw new Ai3dWorkflowException("AI3D_MESH_NOT_FOUND", "Mesh file was not found.");
                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (extension != ".glb" && extension != ".gltf" && extension != ".fbx" &&
                    extension != ".obj" && extension != ".stl")
                    throw new Ai3dWorkflowException(
                        "AI3D_MESH_FORMAT_REJECTED", "Only GLB, GLTF, FBX, OBJ and STL model files are allowed.");
                var info = new FileInfo(sourcePath);
                if (info.Length <= 0 || info.Length > 150L * 1024L * 1024L)
                    throw new Ai3dWorkflowException("AI3D_MESH_SIZE_REJECTED", "Mesh must contain 1 byte to 150 MB.");
                inputHash = Ai3dGatewayClient.ComputeFileSha256(sourcePath);
                inputSummary = Path.GetFileName(sourcePath) + "; " + info.Length + " bytes; sha256 " +
                               inputHash.Substring(0, 12);
            }

            var autoConvert = (bool?)arguments["autoConvertToFbx"] ?? true;
            var smartLowPoly = (bool?)arguments["smartLowPoly"] ?? preset.SmartLowPoly;
            var faceLimit = Math.Max(500, (int?)arguments["faceLimit"] ?? preset.FaceLimit);
            if (smartLowPoly || workflowKind == "retopology")
            {
                var quad = (bool?)arguments["quad"] ?? false;
                faceLimit = Math.Min(quad ? 10000 : 20000, faceLimit);
            }
            var geometryQuality = ((string)arguments["geometryQuality"] ?? preset.GeometryQuality).Trim().ToLowerInvariant();
            if (geometryQuality != "standard" && geometryQuality != "detailed")
                throw new Ai3dWorkflowException(
                    "AI3D_GEOMETRY_QUALITY_INVALID", "geometryQuality must be standard or detailed.");
            var texture = (bool?)arguments["texture"] ?? preset.Texture;
            var pbr = texture && ((bool?)arguments["pbr"] ?? preset.Pbr);
            var plan = new Ai3dWorkflowPlan
            {
                WorkflowId = Guid.NewGuid().ToString("N"),
                WorkflowKind = workflowKind,
                Preset = preset.Id,
                SourceKind = sourceKind,
                InputHash = inputHash,
                InputSummary = inputSummary,
                ModelVersion = string.IsNullOrWhiteSpace((string)arguments["modelVersion"])
                    ? Ai3dGatewaySettings.ModelVersion
                    : ((string)arguments["modelVersion"]).Trim(),
                FaceLimit = faceLimit,
                Texture = texture,
                Pbr = pbr,
                SmartLowPoly = smartLowPoly,
                GeometryQuality = geometryQuality,
                AutoConvertToFbx = autoConvert,
                RequiredDisclosureKinds = new[] { sourceKind },
                Prompt = prompt,
                SourcePath = sourcePath,
                SourceAssetPath = TryGetUnityAssetPath(sourcePath),
                CreatedUtc = DateTime.UtcNow.ToString("O"),
            };
            if (string.IsNullOrEmpty(plan.ModelVersion) || plan.ModelVersion.Length > 128)
                throw new Ai3dWorkflowException("AI3D_MODEL_VERSION_INVALID", "modelVersion must contain 1-128 characters.");
            BuildPipeline(plan, arguments, preset);
            ApplyWorkflowBudget(plan, arguments, preset);
            plan.PlanHash = Ai3dGatewaySettings.Sha256(plan.CanonicalJson().ToString(Formatting.None));
            lock (Gate) Plans[plan.WorkflowId] = plan;
            return plan;
        }

        private static void BuildPipeline(
            Ai3dWorkflowPlan plan, JObject arguments, Ai3dWorkflowPresetDefinition preset)
        {
            switch (plan.WorkflowKind)
            {
                case "generate_asset":
                {
                    var estimate = Math.Max(1, Math.Min(100000,
                        (int?)arguments["estimatedCredits"] ?? preset.GenerationEstimate));
                    plan.Nodes.Add(new Ai3dWorkflowNodePlan
                    {
                        Id = "generate",
                        Operation = plan.SourceKind == "image" ? "image_to_model" : "text_to_model",
                        EstimatedCredits = estimate,
                        BudgetCapCredits = Math.Max(preset.GenerationCap, estimate),
                        Payload = GenerationPayload(plan, null),
                    });
                    if (plan.AutoConvertToFbx) plan.Nodes.Add(ConvertNode("convert_fbx"));
                    break;
                }
                case "retopology":
                {
                    var quad = (bool?)arguments["quad"] ?? false;
                    var model = ((string)arguments["retopologyModel"] ?? "v2.0").Trim();
                    if (model != "v1.0" && model != "v2.0")
                        throw new Ai3dWorkflowException("AI3D_RETOPOLOGY_MODEL_INVALID", "retopologyModel must be v1.0 or v2.0.");
                    var payload = new JObject
                    {
                        ["model"] = model,
                        ["face_limit"] = plan.FaceLimit,
                        ["quad"] = quad,
                    };
                    if (model == "v2.0") payload["bake"] = (bool?)arguments["bake"] ?? true;
                    AddStringArray(payload, "part_names", arguments["partNames"] as JArray, 64);
                    var estimate = model == "v2.0" ? 30 : 10;
                    plan.Nodes.Add(Node("retopology", "retopology", estimate, estimate + 15, payload));
                    if (plan.AutoConvertToFbx) plan.Nodes.Add(ConvertNode("convert_fbx"));
                    break;
                }
                case "segment_complete":
                {
                    var model = ((string)arguments["segmentationModel"] ?? "v2.0-20260430").Trim();
                    if (model != "v1.0-20250506" && model != "v2.0-20260430")
                        throw new Ai3dWorkflowException("AI3D_SEGMENTATION_MODEL_INVALID", "Unsupported segmentation model.");
                    var payload = new JObject { ["model"] = model };
                    if (model == "v2.0-20260430")
                    {
                        var granularity = ((string)arguments["segmentationGranularity"] ?? "balanced").Trim();
                        if (granularity != "simple" && granularity != "balanced" && granularity != "detailed")
                            throw new Ai3dWorkflowException("AI3D_SEGMENTATION_GRANULARITY_INVALID", "Granularity must be simple, balanced or detailed.");
                        payload["segmentation_granularity"] = granularity;
                        payload["split_by_connectivity"] = (bool?)arguments["splitByConnectivity"] ?? true;
                    }
                    plan.Nodes.Add(Node("segment", "segment", 40, 60, payload));
                    var completion = ((string)arguments["completionMode"] ?? "quick_cap").Trim();
                    if (completion != "none" && completion != "quick_cap" && completion != "ai_completion")
                        throw new Ai3dWorkflowException("AI3D_COMPLETION_MODE_INVALID", "completionMode must be none, quick_cap or ai_completion.");
                    if (completion != "none")
                    {
                        var completePayload = new JObject { ["completion_mode"] = completion };
                        AddStringArray(completePayload, "part_names", arguments["partNames"] as JArray, 64);
                        var estimate = completion == "quick_cap" ? 10 : 40;
                        plan.Nodes.Add(Node("complete", "complete", estimate, estimate + 20, completePayload));
                    }
                    if (plan.AutoConvertToFbx) plan.Nodes.Add(ConvertNode("convert_fbx"));
                    break;
                }
                case "rig_animation":
                {
                    var extension = Path.GetExtension(plan.SourcePath ?? string.Empty).ToLowerInvariant();
                    if (extension != ".glb") plan.Nodes.Add(ConvertNode("convert_glb", "GLB"));
                    plan.Nodes.Add(Node("rig_check", "rig_check", 0, 0, new JObject()));
                    var requestedRigType = ((string)arguments["rigType"] ?? "auto").Trim();
                    var validRigTypes = new HashSet<string>(new[]
                    {
                        "auto", "biped", "quadruped", "hexapod", "octopod", "avian", "serpentine", "aquatic",
                    }, StringComparer.Ordinal);
                    if (!validRigTypes.Contains(requestedRigType))
                        throw new Ai3dWorkflowException("AI3D_RIG_TYPE_INVALID", "Unsupported rigType.");
                    var rigSpec = ((string)arguments["rigSpec"] ?? "mixamo").Trim();
                    if (rigSpec != "mixamo" && rigSpec != "tripo")
                        throw new Ai3dWorkflowException("AI3D_RIG_SPEC_INVALID", "rigSpec must be mixamo or tripo.");
                    var rigModel = ((string)arguments["rigModel"] ??
                                    (plan.Preset == "character-creature" ? "v2.5-20260210" : "v1.0-20240301")).Trim();
                    plan.Nodes.Add(Node("rig", "rig", 30, 45, new JObject
                    {
                        ["model"] = rigModel,
                        ["rig_type"] = requestedRigType,
                        ["spec"] = rigSpec,
                        ["out_format"] = "glb",
                    }));
                    var animations = NormalizeAnimations(arguments["animations"] as JArray);
                    if (animations.Count > 0)
                    {
                        plan.Nodes.Add(Node("retarget", "retarget", animations.Count * 10,
                            animations.Count * 15, new JObject
                            {
                                ["animations"] = animations,
                                ["out_format"] = "fbx",
                                ["export_with_geometry"] = true,
                                ["animate_in_place"] = (bool?)arguments["animateInPlace"] ?? false,
                            }));
                    }
                    else plan.Nodes.Add(ConvertNode("convert_fbx"));
                    break;
                }
            }
        }

        private static void ApplyWorkflowBudget(
            Ai3dWorkflowPlan plan, JObject arguments, Ai3dWorkflowPresetDefinition preset)
        {
            var estimated = plan.Nodes.Sum(node => node.EstimatedCredits);
            var defaultCap = plan.Nodes.Sum(node => node.BudgetCapCredits);
            if (plan.WorkflowKind == "generate_asset")
                defaultCap = Math.Max(defaultCap, preset.GenerationCap + (plan.AutoConvertToFbx ? 15 : 0));
            var requestedCap = (int?)arguments["budgetCapCredits"] ?? defaultCap;
            if (requestedCap < estimated)
                throw new Ai3dWorkflowException(
                    "AI3D_BUDGET_BELOW_PLAN", $"budgetCapCredits must be at least {estimated} for this workflow.");
            if (requestedCap > Ai3dGatewaySettings.MaxCreditsPerWorkflow)
                throw new Ai3dWorkflowException(
                    "AI3D_BUDGET_POLICY_EXCEEDED",
                    $"Requested cap {requestedCap} exceeds the local policy limit {Ai3dGatewaySettings.MaxCreditsPerWorkflow}.");
            var assigned = plan.Nodes.Sum(node => node.BudgetCapCredits);
            if (assigned != requestedCap)
            {
                var billable = plan.Nodes.FirstOrDefault(node => node.EstimatedCredits > 0);
                if (billable == null && requestedCap != 0)
                    throw new Ai3dWorkflowException("AI3D_BUDGET_INVALID", "A zero-cost workflow cannot approve a positive cap.");
                if (billable != null)
                {
                    var otherCaps = assigned - billable.BudgetCapCredits;
                    billable.BudgetCapCredits = requestedCap - otherCaps;
                    if (billable.BudgetCapCredits < billable.EstimatedCredits)
                    {
                        var deficit = billable.EstimatedCredits - billable.BudgetCapCredits;
                        for (int i = plan.Nodes.Count - 1; i >= 0 && deficit > 0; i--)
                        {
                            var node = plan.Nodes[i];
                            if (node == billable) continue;
                            var spare = node.BudgetCapCredits - node.EstimatedCredits;
                            var moved = Math.Min(spare, deficit);
                            node.BudgetCapCredits -= moved;
                            billable.BudgetCapCredits += moved;
                            deficit -= moved;
                        }
                        if (deficit > 0)
                            throw new Ai3dWorkflowException("AI3D_BUDGET_BELOW_PLAN", "Budget cannot cover per-node estimates.");
                    }
                }
            }
            plan.EstimatedCredits = estimated;
            plan.BudgetCapCredits = requestedCap;
            var generation = plan.Nodes.FirstOrDefault(node => node.Id == "generate");
            var conversion = plan.Nodes.FirstOrDefault(node => node.Id == "convert_fbx");
            plan.GenerationEstimatedCredits = generation?.EstimatedCredits ?? 0;
            plan.GenerationCapCredits = generation?.BudgetCapCredits ?? 0;
            plan.ConvertEstimatedCredits = conversion?.EstimatedCredits ?? 0;
            plan.ConvertCapCredits = conversion?.BudgetCapCredits ?? 0;
        }

        private static Ai3dWorkflowNodePlan Node(
            string id, string operation, int estimate, int cap, JObject payload) => new Ai3dWorkflowNodePlan
        {
            Id = id,
            Operation = operation,
            EstimatedCredits = estimate,
            BudgetCapCredits = cap,
            Payload = payload ?? new JObject(),
        };

        private static Ai3dWorkflowNodePlan ConvertNode(string id, string format = "FBX") =>
            Node(id, "convert", 10, 15, new JObject
            {
                ["format"] = format,
                ["texture_size"] = 2048,
                ["texture_format"] = "PNG",
                ["bake"] = true,
                ["with_animation"] = true,
                ["fbx_preset"] = "blender",
            });

        private static void AddStringArray(JObject target, string key, JArray source, int maximum)
        {
            if (source == null || source.Count == 0) return;
            if (source.Count > maximum)
                throw new Ai3dWorkflowException("AI3D_ARRAY_TOO_LARGE", key + " contains too many values.");
            var values = new JArray();
            foreach (var token in source)
            {
                var value = ((string)token ?? string.Empty).Trim();
                if (value.Length == 0 || value.Length > 128)
                    throw new Ai3dWorkflowException("AI3D_ARRAY_VALUE_INVALID", key + " contains an invalid value.");
                values.Add(value);
            }
            target[key] = values;
        }

        private static JArray NormalizeAnimations(JArray source)
        {
            if (source == null || source.Count == 0) return new JArray();
            if (source.Count > 5)
                throw new Ai3dWorkflowException("AI3D_ANIMATION_BATCH_TOO_LARGE", "Tripo retarget accepts at most 5 animations per workflow.");
            var values = new JArray();
            foreach (var token in source)
            {
                var value = ((string)token ?? string.Empty).Trim();
                if (!value.StartsWith("preset:", StringComparison.Ordinal) || value.Length > 128)
                    throw new Ai3dWorkflowException("AI3D_ANIMATION_PRESET_INVALID", "Animation values must be typed preset identifiers.");
                values.Add(value);
            }
            return values;
        }

        private static string TryGetUnityAssetPath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) return null;
            var assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(absolutePath);
            if (!full.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets/" + full.Substring(assetsRoot.Length + 1).Replace('\\', '/');
        }

        public static async Task<Ai3dWorkflowRecord> StartAsync(
            string workflowId,
            string planHash,
            int approvedBudgetCapCredits,
            bool externalSendApproved,
            IEnumerable<string> approvedDisclosureKinds,
            CancellationToken cancellationToken = default)
        {
            ValidateId(workflowId, "workflowId");
            Ai3dWorkflowPlan plan;
            Ai3dWorkflowRecord existing;
            var alreadyStarted = false;
            lock (Gate)
            {
                Plans.TryGetValue(workflowId, out plan);
                Records.TryGetValue(workflowId, out existing);
            }
            existing = existing ?? LoadRecord(workflowId);
            if (existing != null)
            {
                if (!string.Equals(existing.PlanHash, planHash, StringComparison.Ordinal))
                    throw new Ai3dWorkflowException("AI3D_PLAN_STALE", "workflowId already belongs to a different planHash.");
                alreadyStarted = existing.Status != "starting";
                plan = existing.ResumePlan ?? plan ?? ApprovalPlan(existing);
            }
            if (plan == null || !string.Equals(plan.PlanHash, planHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_PLAN_STALE", "Plan is missing or stale. Plan the workflow again.");
            if (!externalSendApproved)
                throw new Ai3dWorkflowException(
                    "AI3D_EXTERNAL_SEND_APPROVAL_REQUIRED", "externalSendApproved must be true before starting.");
            if (approvedBudgetCapCredits != plan.BudgetCapCredits)
                throw new Ai3dWorkflowException(
                    "AI3D_BUDGET_APPROVAL_MISMATCH", "Approved budget must exactly match the reviewed plan.");
            var approved = new HashSet<string>(approvedDisclosureKinds ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (!approved.SetEquals(plan.RequiredDisclosureKinds))
                throw new Ai3dWorkflowException(
                    "AI3D_DISCLOSURE_APPROVAL_REQUIRED",
                    "approvedDisclosureKinds must exactly match the reviewed plan.");
            if (plan.BudgetCapCredits > Ai3dGatewaySettings.MaxCreditsPerWorkflow)
                throw new Ai3dWorkflowException("AI3D_BUDGET_POLICY_EXCEEDED", "Local workflow budget policy changed; plan again.");

            if (alreadyStarted) return existing;

            var balance = await Gateway.BalanceAsync(cancellationToken);
            if (((int?)balance?["balance"] ?? 0) < plan.EstimatedCredits)
                throw new Ai3dWorkflowException(
                    "AI3D_BALANCE_INSUFFICIENT",
                    $"Available Tripo balance is below the estimate of {plan.EstimatedCredits} credits.");

            var record = existing ?? new Ai3dWorkflowRecord
            {
                WorkflowId = plan.WorkflowId,
                PlanHash = plan.PlanHash,
                Status = "starting",
                WorkflowKind = plan.WorkflowKind,
                Preset = plan.Preset,
                SourceKind = plan.SourceKind,
                InputHash = plan.InputHash,
                InputSummary = plan.InputSummary,
                ModelVersion = plan.ModelVersion,
                AutoConvertToFbx = plan.AutoConvertToFbx,
                EstimatedCredits = plan.EstimatedCredits,
                BudgetCapCredits = plan.BudgetCapCredits,
                CurrentNode = plan.Nodes.Count > 0 ? plan.Nodes[0].Id : null,
                CurrentNodeIndex = 0,
                PipelineNodes = Ai3dWorkflowNodePlan.ToJson(plan.Nodes, true),
                SourceAssetPath = plan.SourceAssetPath,
                Message = "submitting first reviewed workflow node",
                CreatedUtc = DateTime.UtcNow.ToString("O"),
            };
            record.ResumePlan = plan;
            Touch(record);
            SaveRecord(record);
            lock (Gate) Records[record.WorkflowId] = record;

            await SubmitInitialNodeAsync(record, plan, cancellationToken);
            LaunchRunner(record.WorkflowId);
            return record;
        }

        public static Ai3dWorkflowRecord Get(string workflowId)
        {
            ValidateId(workflowId, "workflowId");
            lock (Gate)
                if (Records.TryGetValue(workflowId, out var cached)) return cached;
            var loaded = LoadRecord(workflowId);
            if (loaded == null)
                throw new Ai3dWorkflowException("AI3D_WORKFLOW_NOT_FOUND", "Workflow was not found: " + workflowId);
            lock (Gate) Records[workflowId] = loaded;
            return loaded;
        }

        public static async Task<Ai3dWorkflowRecord> WaitAsync(
            string workflowId, double timeoutSeconds, CancellationToken cancellationToken = default)
        {
            timeoutSeconds = Math.Max(0.1d, Math.Min(300d, timeoutSeconds));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
            while (true)
            {
                var record = Get(workflowId);
                if (!IsRemoteActive(record.Status) || DateTime.UtcNow >= deadline) return record;
                await Task.Delay(200, cancellationToken);
            }
        }

        public static async Task<Ai3dWorkflowRecord> CancelAsync(
            string workflowId, string planHash, CancellationToken cancellationToken = default)
        {
            var record = Get(workflowId);
            RequirePlan(record, planHash);
            if (record.IsTerminal || !IsRemoteActive(record.Status) || string.IsNullOrEmpty(record.CurrentJobId))
                return record;
            var job = await Gateway.CancelJobAsync(record.CurrentJobId, cancellationToken);
            ApplyJobSnapshot(record, job);
            record.Status = job.IsTerminal ? "cancelled" : "cancel_requested";
            record.Message = "local cancellation requested; provider reconciliation continues";
            Touch(record);
            SaveRecord(record);
            LaunchRunner(record.WorkflowId);
            return record;
        }

        public static async Task<Ai3dWorkflowRecord> ValidateCandidateAsync(
            string workflowId, string planHash, CancellationToken cancellationToken = default,
            string quarantineRoot = null)
        {
            var record = Get(workflowId);
            RequirePlan(record, planHash);
            if (record.Status != "awaiting_candidate" && record.Status != "candidate_validated" &&
                record.Status != "preview_ready")
                throw new Ai3dWorkflowException(
                    "AI3D_CANDIDATE_NOT_READY", "Workflow has no completed candidate ready for validation.");

            if (!string.IsNullOrEmpty(record.StagedAssetPath))
            {
                var current = await MainThreadDispatcher.RunAsync(() => Ai3dCandidateService.RevalidateStaged(
                    record.StagedAssetPath, record.CurrentJobId, record.ProviderTaskId, record.PlanHash,
                    record.CandidateHash));
                if (!string.Equals(current.ValidationHash, record.ValidationHash, StringComparison.Ordinal))
                    throw new Ai3dWorkflowException(
                        "AI3D_VALIDATION_STALE", "Staged candidate changed; reject it and validate from the vault again.");
                return record;
            }

            var job = await Gateway.GetJobAsync(record.CurrentJobId, cancellationToken);
            if (!string.Equals(job.PlanHash, record.PlanHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_PLAN_HASH_MISMATCH", "Provider job does not belong to this plan.");
            var capabilities = await Gateway.CapabilitiesAsync(cancellationToken);
            Ai3dArtifactValidation validation = null;
            Ai3dStagedCandidate staged = null;
            await MainThreadDispatcher.RunAsync(() =>
            {
                validation = Ai3dCandidateService.ValidateArtifact(
                    job, job.FindFbxArtifact(), (string)capabilities["artifactVaultRoot"]);
                if (validation.IsValid) staged = Ai3dCandidateService.Stage(validation, quarantineRoot);
            }, cancellationToken);
            if (validation == null || !validation.IsValid)
            {
                var issues = validation == null ? null : IssuesJson(validation.Issues);
                throw new Ai3dWorkflowException(
                    "AI3D_CANDIDATE_VALIDATION_FAILED", "Provider candidate failed vault validation.", issues);
            }
            if (staged == null || !staged.IsValid)
                throw new Ai3dWorkflowException(
                    "AI3D_CANDIDATE_IMPORT_FAILED", "Candidate failed Unity quarantine validation.",
                    staged == null ? null : IssuesJson(staged.Issues));
            Ai3dWorkflowQualityEvaluation quality = null;
            await MainThreadDispatcher.RunAsync(() =>
                quality = Ai3dWorkflowQualityService.Evaluate(record, staged), cancellationToken);
            record.CandidateHash = staged.SourceArtifactHash;
            record.ValidationHash = staged.ValidationHash;
            record.StagedAssetPath = staged.StagedAssetPath;
            record.ManifestAssetPath = staged.ManifestAssetPath;
            record.QualityReport = quality?.Report;
            record.ValidationIssues = MergeIssues(staged.Issues, quality?.Issues);
            if (quality != null && !quality.AcceptAllowed)
            {
                record.Status = "quality_gate_failed";
                record.Progress = 1f;
                record.Message = "candidate staged but blocked by the workflow quality gate";
                Touch(record);
                SaveRecord(record);
                throw new Ai3dWorkflowException(
                    "AI3D_QUALITY_GATE_FAILED",
                    "Candidate was quarantined but cannot be accepted until the quality issues are resolved.",
                    record.ValidationIssues);
            }
            record.Status = "candidate_validated";
            record.Progress = 1f;
            record.Message = "candidate validated and staged in quarantine";
            Touch(record);
            SaveRecord(record);
            return record;
        }

        public static Ai3dWorkflowRecord PreviewCandidate(string workflowId, string planHash)
        {
            var record = Get(workflowId);
            RequirePlan(record, planHash);
            if (string.IsNullOrEmpty(record.StagedAssetPath) || string.IsNullOrEmpty(record.ValidationHash))
                throw new Ai3dWorkflowException("AI3D_CANDIDATE_NOT_VALIDATED", "Validate the candidate first.");
            var staged = Ai3dCandidateService.RevalidateStaged(
                record.StagedAssetPath, record.CurrentJobId, record.ProviderTaskId, record.PlanHash,
                record.CandidateHash);
            if (!staged.IsValid || !string.Equals(staged.ValidationHash, record.ValidationHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_VALIDATION_STALE", "Candidate changed after validation.");
            var source = string.IsNullOrEmpty(record.SourceAssetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(record.SourceAssetPath);
            if (record.WorkflowKind == "retopology" && source != null)
                Ai3dScenePreviewController.ShowComparison(source, staged.Asset, record.WorkflowId.Substring(0, 10));
            else Ai3dScenePreviewController.Show(staged.Asset, record.WorkflowId.Substring(0, 10));
            Ai3dScenePreviewController.Frame();
            record.PreviewHash = Ai3dCandidateService.ComputePreviewHash(staged.Asset, staged.ValidationHash);
            if (record.QualityReport != null && record.WorkflowKind == "retopology")
                record.QualityReport["silhouetteReviewHash"] = record.PreviewHash;
            if (record.QualityReport != null && record.WorkflowKind == "segment_complete")
                record.QualityReport["closedPartReviewHash"] = record.PreviewHash;
            record.Status = "preview_ready";
            record.Message = "candidate displayed in Scene View; visual review is required before acceptance";
            Touch(record);
            SaveRecord(record);
            return record;
        }

        public static Ai3dWorkflowRecord AcceptCandidate(
            string workflowId, string planHash, string candidateHash,
            string validationHash, string previewHash, string acceptedRoot = null)
        {
            var record = Get(workflowId);
            RequirePlan(record, planHash);
            RequireHash(record.CandidateHash, candidateHash, "candidateHash");
            RequireHash(record.ValidationHash, validationHash, "validationHash");
            RequireHash(record.PreviewHash, previewHash, "previewHash");
            if (record.QualityReport != null && (bool?)record.QualityReport["acceptAllowed"] == false)
                throw new Ai3dWorkflowException(
                    "AI3D_QUALITY_GATE_FAILED", "Candidate is blocked by its current workflow quality report.");
            if (record.WorkflowKind == "retopology" &&
                !string.Equals((string)record.QualityReport?["silhouetteReviewHash"], previewHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException(
                    "AI3D_SILHOUETTE_REVIEW_REQUIRED", "Open the current A/B comparison in Scene View before acceptance.");
            if (record.WorkflowKind == "segment_complete" &&
                !string.Equals((string)record.QualityReport?["closedPartReviewHash"], previewHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException(
                    "AI3D_CLOSED_PART_REVIEW_REQUIRED", "Review the current segmented/closed parts in Scene View before acceptance.");
            var staged = Ai3dCandidateService.RevalidateStaged(
                record.StagedAssetPath, record.CurrentJobId, record.ProviderTaskId, record.PlanHash,
                record.CandidateHash);
            if (!string.Equals(staged.ValidationHash, record.ValidationHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_VALIDATION_STALE", "Candidate changed after preview.");
            record.AcceptedAssetPath = Ai3dCandidateService.Accept(staged, previewHash, acceptedRoot);
            record.Status = "accepted";
            record.Message = "candidate promoted to accepted assets; scene remains unchanged";
            Touch(record);
            SaveRecord(record);
            return record;
        }

        public static Ai3dWorkflowRecord RejectCandidate(
            string workflowId, string planHash, string candidateHash)
        {
            var record = Get(workflowId);
            RequirePlan(record, planHash);
            RequireHash(record.CandidateHash, candidateHash, "candidateHash");
            if (Ai3dScenePreviewController.ActiveAsset != null &&
                AssetDatabase.GetAssetPath(Ai3dScenePreviewController.ActiveAsset) == record.StagedAssetPath)
                Ai3dScenePreviewController.Clear();
            record.Status = "rejected";
            record.Message = "candidate rejected; quarantine files retained for recovery";
            Touch(record);
            SaveRecord(record);
            return record;
        }

        public static async Task<JObject> ProviderStatusAsync(
            bool includeBalance, CancellationToken cancellationToken = default)
        {
            var health = await Gateway.HealthAsync(cancellationToken);
            var result = new JObject
            {
                ["providers"] = new JArray(),
                ["localPolicy"] = new JObject
                {
                    ["paidExternalToolsEnabled"] = Settings.AllowExternalServiceTools,
                    ["maximumCreditsPerWorkflow"] = Ai3dGatewaySettings.MaxCreditsPerWorkflow,
                    ["alwaysRequiresConfirmation"] = true,
                    ["acceptedSceneMutation"] = false,
                },
            };
            var provider = new JObject
            {
                ["id"] = "tripo",
                ["service"] = health.Service,
                ["gatewayVersion"] = health.Version,
                ["ready"] = health.Ready,
                ["configured"] = health.Configured,
                ["presets"] = new JArray(Ai3dWorkflowCatalog.All.Select(item => item.ToJson())),
            };
            if (health.Ready && !string.IsNullOrWhiteSpace(Ai3dGatewaySettings.GatewayToken))
            {
                var capabilities = await Gateway.CapabilitiesAsync(cancellationToken);
                var credential = await Gateway.CredentialStatusAsync(cancellationToken);
                provider["contractVersion"] = capabilities["contractVersion"];
                provider["operations"] = capabilities["operations"]?.DeepClone();
                provider["durableJobs"] = capabilities["durableJobs"];
                provider["remoteCancellation"] = capabilities["remoteCancellation"];
                provider["credential"] = new JObject
                {
                    ["configured"] = credential.Configured,
                    ["source"] = credential.Source,
                    ["persistent"] = credential.Persistent,
                };
                if (includeBalance && credential.Configured)
                {
                    var balance = await Gateway.BalanceAsync(cancellationToken);
                    provider["balance"] = new JObject
                    {
                        ["available"] = (int?)balance["balance"] ?? 0,
                        ["frozen"] = (int?)balance["frozen"] ?? 0,
                    };
                }
            }
            ((JArray)result["providers"]).Add(provider);
            return result;
        }

        internal static void ResumeActiveWorkflows()
        {
            lock (Gate)
            {
                if (_resumed) return;
                _resumed = true;
            }
            Directory.CreateDirectory(StoreRoot);
            foreach (var path in Directory.GetFiles(StoreRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var record = Ai3dWorkflowRecord.Parse(JObject.Parse(File.ReadAllText(path)));
                    if (record == null || !IsId(record.WorkflowId)) continue;
                    lock (Gate) Records[record.WorkflowId] = record;
                    if (IsRemoteActive(record.Status)) LaunchRunner(record.WorkflowId);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[ai3d] ignored invalid workflow record: " + exception.Message);
                }
            }
        }

        private static async Task SubmitInitialNodeAsync(
            Ai3dWorkflowRecord record, Ai3dWorkflowPlan plan, CancellationToken token)
        {
            if (plan.Nodes.Count == 0)
                throw new Ai3dWorkflowException("AI3D_PIPELINE_EMPTY", "Workflow has no executable nodes.");
            var node = plan.Nodes[0];
            var payload = node.Payload?.DeepClone() as JObject ?? new JObject();
            string[] disclosures;
            string[] sourceHashes;
            if (plan.SourceKind == "image" || plan.SourceKind == "mesh")
            {
                if (string.IsNullOrEmpty(plan.SourcePath) || !File.Exists(plan.SourcePath))
                    throw new Ai3dWorkflowException("AI3D_INPUT_NOT_FOUND", "Planned local input is no longer available.");
                var currentHash = Ai3dGatewayClient.ComputeFileSha256(plan.SourcePath);
                if (!string.Equals(currentHash, plan.InputHash, StringComparison.Ordinal))
                    throw new Ai3dWorkflowException("AI3D_INPUT_CHANGED", "Local input changed after planning; plan again.");
                if (string.IsNullOrEmpty(record.ResumeUploadFileToken))
                {
                    var upload = await Gateway.UploadAsync(plan.SourcePath, token);
                    if (!string.Equals(upload.Sha256, plan.InputHash, StringComparison.OrdinalIgnoreCase))
                        throw new Ai3dWorkflowException("AI3D_UPLOAD_HASH_MISMATCH", "Gateway upload hash differs from the reviewed plan.");
                    record.ResumeUploadFileToken = upload.FileToken;
                    Touch(record);
                    SaveRecord(record);
                }
                payload["input"] = record.ResumeUploadFileToken;
                record.SourceProviderReference = record.ResumeUploadFileToken;
                disclosures = new[] { plan.SourceKind };
                sourceHashes = new[] { plan.InputHash };
            }
            else
            {
                payload["prompt"] = plan.Prompt;
                disclosures = new[] { "text" };
                sourceHashes = Array.Empty<string>();
            }
            var job = await Gateway.CreateJobAsync(
                node.Operation, payload, node.EstimatedCredits, node.BudgetCapCredits,
                disclosures, sourceHashes, NodeIdempotencyKey(plan.WorkflowId, node.Id),
                plan.PlanHash, token);
            if (!string.Equals(job.PlanHash, plan.PlanHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_PLAN_HASH_MISMATCH", "Gateway returned a job for a different plan.");
            if (node.Id == "generate") record.GenerationJobId = job.JobId;
            record.CurrentJobId = job.JobId;
            record.CurrentNode = node.Id;
            record.CurrentNodeIndex = 0;
            RecordNodeJob(record, node, job);
            record.Status = "running_" + node.Id;
            record.ResumePlan = null;
            record.ResumeUploadFileToken = null;
            plan.Prompt = null;
            plan.SourcePath = null;
            lock (Gate) Plans.Remove(plan.WorkflowId);
            ApplyJobSnapshot(record, job);
            Touch(record);
            SaveRecord(record);
        }

        private static JObject GenerationPayload(Ai3dWorkflowPlan plan, string input)
        {
            var payload = new JObject
            {
                [plan.SourceKind == "image" ? "input" : "prompt"] = input,
                ["model"] = plan.ModelVersion,
                ["face_limit"] = plan.SmartLowPoly
                    ? Mathf.Clamp(plan.FaceLimit, 500, 20000)
                    : Mathf.Max(500, plan.FaceLimit),
                ["texture"] = plan.Texture,
                ["pbr"] = plan.Pbr,
                ["geometry_quality"] = plan.GeometryQuality,
            };
            if (plan.SmartLowPoly) payload["smart_low_poly"] = true;
            return payload;
        }

        private static void LaunchRunner(string workflowId)
        {
            lock (Gate)
            {
                if (Runners.TryGetValue(workflowId, out var running) && !running.IsCompleted) return;
                var task = RunWorkflowAsync(workflowId, Lifetime.Token);
                Runners[workflowId] = task;
                _ = task.ContinueWith(_ =>
                {
                    lock (Gate) Runners.Remove(workflowId);
                }, TaskScheduler.Default);
            }
        }

        private static async Task RunWorkflowAsync(string workflowId, CancellationToken token)
        {
            var gatewayRetry = 0;
        retry_after_gateway_recovery:
            try
            {
                var record = Get(workflowId);
                if (record.Status == "starting")
                {
                    if (record.ResumePlan == null)
                        throw new Ai3dWorkflowException(
                            "AI3D_START_INTERRUPTED", "Workflow was interrupted before its first durable provider job.");
                    await SubmitInitialNodeAsync(record, record.ResumePlan, token);
                }
                while (IsRemoteActive(record.Status))
                {
                    if (string.IsNullOrEmpty(record.CurrentJobId))
                        throw new Ai3dWorkflowException("AI3D_JOB_MISSING", "Active workflow has no current provider job.");
                    var job = await Gateway.GetJobAsync(record.CurrentJobId, token);
                    if (!string.Equals(job.PlanHash, record.PlanHash, StringComparison.Ordinal))
                        throw new Ai3dWorkflowException("AI3D_PLAN_HASH_MISMATCH", "Provider job does not belong to this workflow.");
                    ApplyJobSnapshot(record, job);
                    if (!job.IsTerminal)
                    {
                        Touch(record);
                        SaveRecord(record);
                        await Task.Delay(1000, token);
                        continue;
                    }
                    if (!job.IsSuccessful)
                    {
                        record.Status = job.Status == "cancelled" ? "cancelled" : "failed";
                        record.ErrorCode = job.Error?.Code ?? "AI3D_PROVIDER_JOB_FAILED";
                        record.ErrorMessage = job.Error?.Message ?? "Provider job did not complete successfully.";
                        Touch(record);
                        SaveRecord(record);
                        return;
                    }
                    record.CreditsConsumed += job.CreditsConsumed ?? job.EstimatedCredits;
                    if (record.Status == "cancel_requested" || job.Status == "completed_after_cancel")
                    {
                        record.Status = "cancelled";
                        record.Message = "provider completed after local cancellation; artifacts were not accepted";
                        Touch(record);
                        SaveRecord(record);
                        return;
                    }
                    if (record.CurrentNode == "convert_glb")
                        record.SourceProviderReference = job.ProviderTaskId;
                    if (record.CurrentNode == "rig_check") ApplyRigCheck(record, job);
                    var nodes = Ai3dWorkflowNodePlan.ParseMany(record.PipelineNodes);
                    var nextIndex = record.CurrentNodeIndex + 1;
                    while (nextIndex < nodes.Count && nodes[nextIndex].Id == "convert_fbx" &&
                           job.FindFbxArtifact() != null)
                        nextIndex++;
                    if (nextIndex < nodes.Count)
                    {
                        await SubmitNextNodeAsync(record, nodes[nextIndex], nextIndex, job, token);
                        continue;
                    }
                    record.Status = "awaiting_candidate";
                    record.Progress = 1f;
                    record.Message = "provider workflow completed; candidate remains in the authenticated vault";
                    Touch(record);
                    SaveRecord(record);
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Ai3dGatewayException exception) when (IsRecoverableGatewayError(exception))
            {
                var record = Get(workflowId);
                record.ErrorCode = null;
                record.ErrorMessage = null;
                record.Message = "local gateway unavailable; durable workflow will retry without resubmitting paid nodes";
                Touch(record);
                SaveRecord(record);
                var delaySeconds = Math.Min(30, Math.Max(1, 1 << Math.Min(5, gatewayRetry++)));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                goto retry_after_gateway_recovery;
            }
            catch (Exception exception)
            {
                try
                {
                    var record = Get(workflowId);
                    record.Status = "failed";
                    record.ErrorCode = exception is Ai3dWorkflowException workflow
                        ? workflow.Code
                        : exception is Ai3dGatewayException gateway ? gateway.Code : "AI3D_WORKFLOW_FAILED";
                    record.ErrorMessage = exception.GetBaseException().Message;
                    record.Message = "workflow failed";
                    Touch(record);
                    SaveRecord(record);
                }
                catch { }
            }
        }

        private static async Task SubmitNextNodeAsync(
            Ai3dWorkflowRecord record,
            Ai3dWorkflowNodePlan node,
            int nodeIndex,
            Ai3dJobSnapshot previous,
            CancellationToken token)
        {
            if (record.BudgetCapCredits - record.CreditsConsumed < node.BudgetCapCredits)
                throw new Ai3dWorkflowException(
                    "AI3D_WORKFLOW_BUDGET_EXHAUSTED",
                    "Remaining approved workflow budget cannot fund node " + node.Id + ".");
            var payload = node.Payload?.DeepClone() as JObject ?? new JObject();
            var input = node.Operation == "rig" && previous.Operation == "rig_check"
                ? record.SourceProviderReference
                : previous.ProviderTaskId;
            if (string.IsNullOrEmpty(input))
                throw new Ai3dWorkflowException(
                    "AI3D_PROVIDER_TASK_MISSING", "Node " + node.Id + " has no reviewed provider input reference.");
            payload["input"] = input;
            if (node.Operation == "rig" && string.Equals((string)payload["rig_type"], "auto", StringComparison.Ordinal))
            {
                var recommended = (string)previous.ProviderResult?["output"]?["rig_type"];
                if (string.IsNullOrWhiteSpace(recommended))
                    throw new Ai3dWorkflowException("AI3D_RIG_TYPE_MISSING", "rig-check returned no recommended rig type.");
                payload["rig_type"] = recommended;
            }
            var next = await Gateway.CreateJobAsync(
                node.Operation, payload, node.EstimatedCredits, node.BudgetCapCredits,
                new[] { "task_reference" }, Array.Empty<string>(),
                NodeIdempotencyKey(record.WorkflowId, node.Id), record.PlanHash, token);
            if (!string.Equals(next.PlanHash, record.PlanHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_PLAN_HASH_MISMATCH", "Gateway returned a job for a different plan.");
            record.CurrentNodeIndex = nodeIndex;
            record.CurrentNode = node.Id;
            record.CurrentJobId = next.JobId;
            if (node.Id == "convert_fbx") record.ConversionJobId = next.JobId;
            RecordNodeJob(record, node, next);
            record.Status = "running_" + node.Id;
            ApplyJobSnapshot(record, next);
            Touch(record);
            SaveRecord(record);
        }

        private static void ApplyRigCheck(Ai3dWorkflowRecord record, Ai3dJobSnapshot job)
        {
            var output = job.ProviderResult?["output"] as JObject;
            if ((bool?)output?["riggable"] != true)
                throw new Ai3dWorkflowException(
                    "AI3D_MODEL_NOT_RIGGABLE", "Tripo rig-check rejected this model; no rig credits were submitted.");
            var rigType = (string)output["rig_type"];
            if (string.IsNullOrWhiteSpace(rigType))
                throw new Ai3dWorkflowException("AI3D_RIG_TYPE_MISSING", "Tripo rig-check returned no rig type.");
            record.QualityReport = record.QualityReport ?? new JObject();
            record.QualityReport["rigCheck"] = new JObject
            {
                ["riggable"] = true,
                ["recommendedRigType"] = rigType,
                ["providerTaskId"] = job.ProviderTaskId,
            };
        }

        private static void RecordNodeJob(
            Ai3dWorkflowRecord record, Ai3dWorkflowNodePlan node, Ai3dJobSnapshot job)
        {
            record.NodeJobs = record.NodeJobs ?? new JArray();
            record.NodeJobs.Add(new JObject
            {
                ["nodeId"] = node.Id,
                ["operation"] = node.Operation,
                ["jobId"] = job.JobId,
                ["providerTaskId"] = job.ProviderTaskId,
            });
        }

        private static Ai3dWorkflowPlan RecordPlan(Ai3dWorkflowRecord record)
        {
            var plan = new Ai3dWorkflowPlan
            {
                WorkflowId = record.WorkflowId,
                PlanHash = record.PlanHash,
                WorkflowKind = record.WorkflowKind,
                Preset = record.Preset,
                SourceKind = record.SourceKind,
                InputHash = record.InputHash,
                ModelVersion = record.ModelVersion,
                AutoConvertToFbx = record.AutoConvertToFbx,
                EstimatedCredits = record.EstimatedCredits,
                BudgetCapCredits = record.BudgetCapCredits,
                SourceAssetPath = record.SourceAssetPath,
            };
            foreach (var node in Ai3dWorkflowNodePlan.ParseMany(record.PipelineNodes)) plan.Nodes.Add(node);
            return plan;
        }

        private static Ai3dWorkflowPlan ApprovalPlan(Ai3dWorkflowRecord record) => new Ai3dWorkflowPlan
        {
            WorkflowId = record.WorkflowId,
            PlanHash = record.PlanHash,
            WorkflowKind = record.WorkflowKind,
            Preset = record.Preset,
            SourceKind = record.SourceKind,
            InputHash = record.InputHash,
            ModelVersion = record.ModelVersion,
            AutoConvertToFbx = record.AutoConvertToFbx,
            EstimatedCredits = record.EstimatedCredits,
            BudgetCapCredits = record.BudgetCapCredits,
            SourceAssetPath = record.SourceAssetPath,
            RequiredDisclosureKinds = new[] { record.SourceKind },
            CreatedUtc = record.CreatedUtc,
        };

        private static void ApplyJobSnapshot(Ai3dWorkflowRecord record, Ai3dJobSnapshot job)
        {
            record.ProviderTaskId = job.ProviderTaskId ?? record.ProviderTaskId;
            record.Message = job.Message;
            var nodeCount = Math.Max(1, record.PipelineNodes?.Count ?? 0);
            record.Progress = Mathf.Clamp01((record.CurrentNodeIndex + Mathf.Clamp01(job.Progress)) / nodeCount);
        }

        private static void RequirePlan(Ai3dWorkflowRecord record, string planHash)
        {
            if (record == null || !string.Equals(record.PlanHash, planHash, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_PLAN_STALE", "planHash does not match the current workflow.");
        }

        private static void RequireHash(string expected, string actual, string label)
        {
            if (string.IsNullOrEmpty(expected) || !string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_HASH_MISMATCH", label + " does not match the current candidate.");
        }

        private static bool IsRemoteActive(string status) =>
            status == "starting" || status == "cancel_requested" ||
            (!string.IsNullOrEmpty(status) && status.StartsWith("running_", StringComparison.Ordinal));

        private static bool IsRecoverableGatewayError(Ai3dGatewayException exception)
        {
            if (exception == null) return false;
            return exception.Code != "GATEWAY_RESPONSE_INVALID"
                   && exception.Code != "GATEWAY_URL_REJECTED";
        }

        private static void Touch(Ai3dWorkflowRecord record)
        {
            record.UpdatedUtc = DateTime.UtcNow.ToString("O");
            if (string.IsNullOrEmpty(record.CreatedUtc)) record.CreatedUtc = record.UpdatedUtc;
        }

        private static string NodeIdempotencyKey(string workflowId, string node)
            => "ai3d-" + workflowId + "-" + node;

        private static string StoreRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(StoreRootOverride)) return Path.GetFullPath(StoreRootOverride);
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(projectRoot, "Library", "com.maicon.scene-analyzer", "ai3d", "workflows");
            }
        }

        private static string RecordPath(string workflowId) => Path.Combine(StoreRoot, workflowId + ".json");

        private static void SaveRecord(Ai3dWorkflowRecord record)
        {
            Directory.CreateDirectory(StoreRoot);
            var path = RecordPath(record.WorkflowId);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, record.ToJson(true).ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null); }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(path);
                    File.Move(temp, path);
                }
            }
            else File.Move(temp, path);
        }

        private static Ai3dWorkflowRecord LoadRecord(string workflowId)
        {
            var path = RecordPath(workflowId);
            if (!File.Exists(path)) return null;
            var record = Ai3dWorkflowRecord.Parse(JObject.Parse(File.ReadAllText(path)));
            if (record == null || !string.Equals(record.WorkflowId, workflowId, StringComparison.Ordinal))
                throw new Ai3dWorkflowException("AI3D_WORKFLOW_RECORD_INVALID", "Workflow record is invalid.");
            lock (Gate) Records[workflowId] = record;
            return record;
        }

        private static void ValidateId(string value, string label)
        {
            if (!IsId(value)) throw new Ai3dWorkflowException("AI3D_ID_INVALID", label + " is invalid.");
        }

        private static bool IsId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++) if (!Uri.IsHexDigit(value[i])) return false;
            return true;
        }

        private static JArray IssuesJson(IReadOnlyList<Ai3dCandidateIssue> issues)
        {
            var result = new JArray();
            if (issues == null) return result;
            for (int i = 0; i < issues.Count; i++)
                result.Add(new JObject
                {
                    ["code"] = issues[i].Code,
                    ["severity"] = issues[i].Severity.ToString().ToLowerInvariant(),
                    ["message"] = issues[i].Message,
                });
            return result;
        }

        private static JArray MergeIssues(
            IReadOnlyList<Ai3dCandidateIssue> first, IReadOnlyList<Ai3dCandidateIssue> second)
        {
            var result = IssuesJson(first ?? Array.Empty<Ai3dCandidateIssue>());
            foreach (var value in IssuesJson(second ?? Array.Empty<Ai3dCandidateIssue>())) result.Add(value);
            return result;
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                Plans.Clear();
                Records.Clear();
                Runners.Clear();
                _resumed = false;
            }
            Gateway = new Ai3dWorkflowGateway();
            StoreRootOverride = null;
        }
    }

    public sealed class Ai3dWorkflowException : Exception
    {
        public string Code { get; }
        public JToken DataToken { get; }

        public Ai3dWorkflowException(string code, string message, JToken data = null) : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? "AI3D_WORKFLOW_ERROR" : code;
            DataToken = data;
        }
    }
}
