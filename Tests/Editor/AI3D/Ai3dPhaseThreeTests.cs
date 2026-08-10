using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D.Tests
{
    public sealed class Ai3dPhaseThreeTests
    {
        private string _storeRoot;
        private int _previousMaxCredits;
        private bool _previousExternalTools;
        private bool _previousConfirmation;
        private string _assetRoot;

        [SetUp]
        public void SetUp()
        {
            _storeRoot = Path.Combine(Path.GetTempPath(), "ai3d-phase3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_storeRoot);
            Ai3dWorkflowService.ResetForTests();
            Ai3dWorkflowService.StoreRootOverride = _storeRoot;
            _previousMaxCredits = Ai3dGatewaySettings.MaxCreditsPerWorkflow;
            _previousExternalTools = Settings.AllowExternalServiceTools;
            _previousConfirmation = Settings.RequireHighRiskConfirmation;
            Ai3dGatewaySettings.MaxCreditsPerWorkflow = 300;
            _assetRoot = "Assets/__AI3DQualityTests_" + Guid.NewGuid().ToString("N");
        }

        [TearDown]
        public void TearDown()
        {
            Ai3dWorkflowService.ResetForTests();
            Ai3dGatewaySettings.MaxCreditsPerWorkflow = _previousMaxCredits;
            Settings.AllowExternalServiceTools = _previousExternalTools;
            Settings.RequireHighRiskConfirmation = _previousConfirmation;
            if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, true);
            if (!string.IsNullOrEmpty(_assetRoot) && AssetDatabase.IsValidFolder(_assetRoot))
                AssetDatabase.DeleteAsset(_assetRoot);
        }

        [Test]
        public void Plan_IsNonBillableHashBoundAndDoesNotEchoPrompt()
        {
            var arguments = TextPlan();
            var first = Ai3dWorkflowService.Plan(arguments);
            var second = Ai3dWorkflowService.Plan(arguments);

            Assert.That(first.WorkflowId, Is.Not.EqualTo(second.WorkflowId));
            Assert.That(first.PlanHash, Is.EqualTo(second.PlanHash));
            Assert.That(first.PlanHash, Has.Length.EqualTo(64));
            Assert.That(first.EstimatedCredits, Is.EqualTo(130));
            Assert.That(first.BudgetCapCredits, Is.EqualTo(195));
            var publicJson = first.ToPublicJson().ToString();
            Assert.That(publicJson, Does.Not.Contain("private knight prompt"));
            Assert.That((bool)first.ToPublicJson()["requiresConfirmation"], Is.True);
            Assert.That((bool)first.ToPublicJson()["createsCandidateOnly"], Is.True);
        }

        [Test]
        public void Plan_RejectsFuturePresetAndPolicyOverflow()
        {
            var future = TextPlan();
            future["preset"] = "wearable";
            var unavailable = Assert.Throws<Ai3dWorkflowException>(() => Ai3dWorkflowService.Plan(future));
            Assert.That(unavailable.Code, Is.EqualTo("AI3D_PRESET_NOT_AVAILABLE"));

            var overflow = TextPlan();
            overflow["budgetCapCredits"] = 301;
            var blocked = Assert.Throws<Ai3dWorkflowException>(() => Ai3dWorkflowService.Plan(overflow));
            Assert.That(blocked.Code, Is.EqualTo("AI3D_BUDGET_POLICY_EXCEEDED"));
        }

        [Test]
        public void RetopologyPlan_UsesTypedSmartPipelineAndHardGeometryLimit()
        {
            var path = TemporaryModel("source.fbx");
            var plan = Ai3dWorkflowService.Plan(new JObject
            {
                ["workflowKind"] = "retopology",
                ["sourceKind"] = "mesh",
                ["meshPath"] = path,
                ["preset"] = "hero-prop",
                ["retopologyModel"] = "v2.0",
                ["faceLimit"] = 90000,
                ["quad"] = true,
                ["autoConvertToFbx"] = true,
            });

            Assert.That(plan.WorkflowKind, Is.EqualTo("retopology"));
            Assert.That(plan.FaceLimit, Is.EqualTo(10000));
            Assert.That(plan.Nodes.Select(value => value.Operation), Is.EqualTo(new[] { "retopology", "convert" }));
            Assert.That((int)plan.Nodes[0].Payload["face_limit"], Is.EqualTo(10000));
            Assert.That((bool)plan.ToPublicJson()["qualityGate"]["requiresSourceComparison"], Is.True);
            Assert.That(plan.ToPublicJson().ToString(), Does.Not.Contain(path));
        }

        [Test]
        public void SegmentPlan_ComposesSemanticSplitCompletionAndFbxWithoutRawEndpoint()
        {
            var path = TemporaryModel("body.glb");
            var plan = Ai3dWorkflowService.Plan(new JObject
            {
                ["workflowKind"] = "segment_complete",
                ["sourceKind"] = "mesh",
                ["meshPath"] = path,
                ["preset"] = "modular-character",
                ["segmentationModel"] = "v2.0-20260430",
                ["segmentationGranularity"] = "detailed",
                ["completionMode"] = "quick_cap",
                ["partNames"] = new JArray { "head", "body" },
            });

            Assert.That(plan.Nodes.Select(value => value.Operation),
                Is.EqualTo(new[] { "segment", "complete", "convert" }));
            Assert.That((string)plan.Nodes[0].Payload["segmentation_granularity"], Is.EqualTo("detailed"));
            Assert.That((string)plan.Nodes[1].Payload["completion_mode"], Is.EqualTo("quick_cap"));
            Assert.That(plan.RequiredDisclosureKinds, Is.EqualTo(new[] { "mesh" }));
        }

        [Test]
        public async Task RigAnimationRunner_AlwaysChecksRiggabilityAndUsesRecommendedType()
        {
            var fake = new FakeGateway();
            Ai3dWorkflowService.Gateway = fake;
            var path = TemporaryModel("character.fbx");
            var plan = Ai3dWorkflowService.Plan(new JObject
            {
                ["workflowKind"] = "rig_animation",
                ["sourceKind"] = "mesh",
                ["meshPath"] = path,
                ["preset"] = "animation-pack",
                ["rigType"] = "auto",
                ["animations"] = new JArray { "preset:idle", "preset:walk" },
            });

            await Ai3dWorkflowService.StartAsync(
                plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true, new[] { "mesh" });
            var completed = await Ai3dWorkflowService.WaitAsync(plan.WorkflowId, 5d);

            Assert.That(completed.Status, Is.EqualTo("awaiting_candidate"));
            Assert.That(fake.CreatedOperations,
                Is.EqualTo(new[] { "convert", "rig_check", "rig", "retarget" }));
            Assert.That((string)fake.CreatedPayloads[2]["rig_type"], Is.EqualTo("biped"));
            Assert.That((string)fake.CreatedPayloads[3]["out_format"], Is.EqualTo("fbx"));
            Assert.That((bool)completed.QualityReport["rigCheck"]["riggable"], Is.True);
        }

        [Test]
        public void RetopologyQualityGate_BlocksCandidateAboveReviewedTriangleBudget()
        {
            EnsureAssetRoot();
            var sourcePath = CreateStaticPrefab("source", 1000, 1);
            var candidatePath = CreateStaticPrefab("candidate", 600, 1);
            var record = new Ai3dWorkflowRecord
            {
                WorkflowKind = "retopology",
                SourceAssetPath = sourcePath,
                PlanHash = new string('a', 64),
                PipelineNodes = new JArray
                {
                    new JObject
                    {
                        ["id"] = "retopology", ["operation"] = "retopology",
                        ["estimatedCredits"] = 30, ["budgetCapCredits"] = 45,
                        ["payload"] = new JObject { ["face_limit"] = 500 },
                    },
                },
            };
            var candidate = new Ai3dStagedCandidate
            {
                StagedAssetPath = candidatePath,
                Asset = AssetDatabase.LoadAssetAtPath<GameObject>(candidatePath),
                SourceArtifactHash = new string('b', 64),
                ValidationHash = new string('c', 64),
            };

            var quality = Ai3dWorkflowQualityService.Evaluate(record, candidate);

            Assert.That(quality.AcceptAllowed, Is.False);
            Assert.That(quality.Issues.Select(value => value.Code),
                Does.Contain("AI3D_TRIANGLE_BUDGET_EXCEEDED"));
            Assert.That((int)quality.Report["candidate"]["triangleCount"], Is.EqualTo(600));
        }

        [Test]
        public void SegmentationQualityGate_BlocksSilentSkinAndBindPoseLoss()
        {
            EnsureAssetRoot();
            var sourcePath = CreateRiggedPrefab("rigged-source");
            var candidatePath = CreateStaticPrefab("static-parts", 2, 2);
            var record = new Ai3dWorkflowRecord
            {
                WorkflowKind = "segment_complete",
                SourceAssetPath = sourcePath,
                PlanHash = new string('d', 64),
                PipelineNodes = new JArray(),
            };
            var candidate = new Ai3dStagedCandidate
            {
                StagedAssetPath = candidatePath,
                Asset = AssetDatabase.LoadAssetAtPath<GameObject>(candidatePath),
                SourceArtifactHash = new string('e', 64),
                ValidationHash = new string('f', 64),
            };

            var quality = Ai3dWorkflowQualityService.Evaluate(record, candidate);

            Assert.That(quality.AcceptAllowed, Is.False);
            var codes = quality.Issues.Select(value => value.Code).ToArray();
            Assert.That(codes, Does.Contain("AI3D_SKIN_RENDERER_LOSS"));
            Assert.That(codes, Does.Contain("AI3D_SKIN_WEIGHT_LOSS"));
            Assert.That(codes, Does.Contain("AI3D_BIND_POSE_LOSS"));
            Assert.That((bool)quality.Report["smartBodySplitRequired"], Is.True);
        }

        [Test]
        public void DurableRecordParse_BackfillsLegacyGenerationPipeline()
        {
            var record = Ai3dWorkflowRecord.Parse(new JObject
            {
                ["schemaVersion"] = 1,
                ["workflowId"] = new string('1', 32),
                ["planHash"] = new string('2', 64),
                ["status"] = "running_generation",
                ["preset"] = "game-pc",
                ["sourceKind"] = "text",
                ["inputHash"] = new string('3', 64),
                ["modelVersion"] = "v3.1",
                ["autoConvertToFbx"] = true,
                ["estimatedCredits"] = 130,
                ["budgetCapCredits"] = 195,
                ["currentNode"] = "generate",
                ["createdUtc"] = DateTime.UtcNow.ToString("O"),
                ["updatedUtc"] = DateTime.UtcNow.ToString("O"),
            });

            var nodes = Ai3dWorkflowNodePlan.ParseMany(record.PipelineNodes);
            Assert.That(nodes.Select(value => value.Operation),
                Is.EqualTo(new[] { "text_to_model", "convert" }));
            Assert.That(nodes[1].BudgetCapCredits, Is.EqualTo(15));
        }

        [Test]
        public async Task Start_RequiresExactBudgetAndPerKindDisclosureBeforeGatewayCall()
        {
            var fake = new FakeGateway();
            Ai3dWorkflowService.Gateway = fake;
            var plan = Ai3dWorkflowService.Plan(TextPlan());

            var noConsent = Assert.ThrowsAsync<Ai3dWorkflowException>(async () =>
                await Ai3dWorkflowService.StartAsync(
                    plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, false, new[] { "text" }));
            Assert.That(noConsent.Code, Is.EqualTo("AI3D_EXTERNAL_SEND_APPROVAL_REQUIRED"));

            var wrongBudget = Assert.ThrowsAsync<Ai3dWorkflowException>(async () =>
                await Ai3dWorkflowService.StartAsync(
                    plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits - 1, true, new[] { "text" }));
            Assert.That(wrongBudget.Code, Is.EqualTo("AI3D_BUDGET_APPROVAL_MISMATCH"));

            var missingKind = Assert.ThrowsAsync<Ai3dWorkflowException>(async () =>
                await Ai3dWorkflowService.StartAsync(
                    plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true, new[] { "image" }));
            Assert.That(missingKind.Code, Is.EqualTo("AI3D_DISCLOSURE_APPROVAL_REQUIRED"));

            var broaderConsent = Assert.ThrowsAsync<Ai3dWorkflowException>(async () =>
                await Ai3dWorkflowService.StartAsync(
                    plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true, new[] { "text", "mesh" }));
            Assert.That(broaderConsent.Code, Is.EqualTo("AI3D_DISCLOSURE_APPROVAL_REQUIRED"));
            Assert.That(fake.BalanceCalls, Is.Zero);
        }

        [Test]
        public async Task WorkflowRunner_UsesOnePlanHashAndCompletesGenerationThenConversion()
        {
            var fake = new FakeGateway();
            Ai3dWorkflowService.Gateway = fake;
            var plan = Ai3dWorkflowService.Plan(TextPlan());

            var started = await Ai3dWorkflowService.StartAsync(
                plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true, new[] { "text" });
            var completed = await Ai3dWorkflowService.WaitAsync(plan.WorkflowId, 5d);
            var replay = await Ai3dWorkflowService.StartAsync(
                plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true, new[] { "text" });
            var lateCancel = await Ai3dWorkflowService.CancelAsync(plan.WorkflowId, plan.PlanHash);

            Assert.That(started.GenerationJobId, Is.Not.Empty);
            Assert.That(completed.Status, Is.EqualTo("awaiting_candidate"));
            Assert.That(completed.ConversionJobId, Is.Not.Empty);
            Assert.That(completed.CreditsConsumed, Is.EqualTo(10));
            Assert.That(fake.CreatedOperations, Is.EqualTo(new[] { "text_to_model", "convert" }));
            Assert.That(fake.PlanHashes, Is.All.EqualTo(plan.PlanHash));
            Assert.That(fake.IdempotencyKeys.Distinct().Count(), Is.EqualTo(2));
            Assert.That(fake.BalanceCalls, Is.EqualTo(1));
            Assert.That(replay.WorkflowId, Is.EqualTo(completed.WorkflowId));
            Assert.That(lateCancel.Status, Is.EqualTo("awaiting_candidate"));
            Assert.That(fake.CancelCalls, Is.Zero);
            Assert.That(completed.ToPublicJson()["resume"], Is.Null);
            Assert.That(completed.ToPublicJson().ToString(), Does.Not.Contain("private knight prompt"));
        }

        [Test]
        public async Task ImageStart_RetryDoesNotUploadApprovedFileTwice()
        {
            var imagePath = Path.Combine(_storeRoot, "source.png");
            File.WriteAllBytes(imagePath, new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 });
            var fake = new FakeGateway { FailFirstCreate = true };
            Ai3dWorkflowService.Gateway = fake;
            var plan = Ai3dWorkflowService.Plan(new JObject
            {
                ["sourceKind"] = "image",
                ["imagePath"] = imagePath,
                ["preset"] = "game-pc",
                ["autoConvertToFbx"] = false,
            });

            Assert.ThrowsAsync<Ai3dGatewayException>(async () =>
                await Ai3dWorkflowService.StartAsync(
                    plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true, new[] { "image" }));
            var restarted = await Ai3dWorkflowService.StartAsync(
                plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true, new[] { "image" });
            await Ai3dWorkflowService.WaitAsync(plan.WorkflowId, 5d);

            Assert.That(restarted.GenerationJobId, Is.Not.Empty);
            Assert.That(fake.UploadCalls, Is.EqualTo(1));
        }

        [Test]
        public void ExternalServiceCapability_IsOptInAndAlwaysConfirmed()
        {
            var tool = new Ai3dStartWorkflowTool();
            Settings.AllowExternalServiceTools = false;
            Settings.RequireHighRiskConfirmation = false;

            Assert.That(ToolAuthorization.RequiresConfirmation(tool.Descriptor), Is.True);
            Assert.That(ToolAuthorization.TryAuthorize(tool, tool.Descriptor, out var code, out _), Is.False);
            Assert.That(code, Is.EqualTo("EXTERNAL_SERVICE_DISABLED"));

            Settings.AllowExternalServiceTools = true;
            Assert.That(ToolAuthorization.TryAuthorize(tool, tool.Descriptor, out _, out _), Is.True);
            Assert.That(ToolAuthorization.RequiresConfirmation(tool.Descriptor), Is.True);
        }

        [Test]
        public void McpSurface_HasTenTypedToolsWithoutRawEndpoint()
        {
            IMcpTool[] tools =
            {
                new Ai3dListProvidersTool(), new Ai3dPlanWorkflowTool(), new Ai3dStartWorkflowTool(),
                new Ai3dGetWorkflowTool(), new Ai3dWaitWorkflowTool(), new Ai3dCancelWorkflowTool(),
                new Ai3dPreviewCandidateTool(), new Ai3dValidateCandidateTool(),
                new Ai3dAcceptCandidateTool(), new Ai3dRejectCandidateTool(),
            };
            Assert.That(tools.Select(tool => tool.Name).Distinct().Count(), Is.EqualTo(10));
            Assert.That(tools.Any(tool => tool.Name.Contains("call_endpoint")), Is.False);
            foreach (var tool in tools)
            {
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.InputSchema, out var inputError),
                    Is.True, tool.Name + " input: " + inputError);
                Assert.That(ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError),
                    Is.True, tool.Name + " output: " + outputError);
                Assert.That(tool.Descriptor.Category, Is.EqualTo("ai3d"), tool.Name);
            }
            Ai3dMcpBootstrap.Initialize();
            foreach (var tool in tools)
                Assert.That(Bootstrap.ToolRegistry.TryGet(tool.Name, out _), Is.True, tool.Name);
            var selfTest = OperationalReport.SelfTest();
            Assert.That((bool)selfTest["valid"], Is.True, selfTest["issues"]?.ToString());
        }

        [Test]
        public async Task HashGates_RejectStalePlanBeforeCandidateMutation()
        {
            Ai3dWorkflowService.Gateway = new FakeGateway();
            var plan = Ai3dWorkflowService.Plan(TextPlan());
            await Ai3dWorkflowService.StartAsync(
                plan.WorkflowId, plan.PlanHash, plan.BudgetCapCredits, true, new[] { "text" });
            await Ai3dWorkflowService.WaitAsync(plan.WorkflowId, 5d);
            var exception = Assert.Throws<Ai3dWorkflowException>(() =>
                Ai3dWorkflowService.PreviewCandidate(plan.WorkflowId, new string('0', 64)));
            Assert.That(exception.Code, Is.EqualTo("AI3D_PLAN_STALE"));
        }

        private static JObject TextPlan() => new JObject
        {
            ["sourceKind"] = "text",
            ["prompt"] = "private knight prompt",
            ["preset"] = "game-pc",
            ["autoConvertToFbx"] = true,
        };

        private string TemporaryModel(string name)
        {
            var path = Path.Combine(_storeRoot, name);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            return path;
        }

        private void EnsureAssetRoot()
        {
            if (!AssetDatabase.IsValidFolder(_assetRoot))
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(_assetRoot));
        }

        private string CreateStaticPrefab(string name, int triangleCount, int rendererCount)
        {
            var mesh = TriangleSoup(name + "Mesh", triangleCount);
            var meshPath = _assetRoot + "/" + name + ".asset";
            AssetDatabase.CreateAsset(mesh, meshPath);
            var root = new GameObject(name);
            try
            {
                for (var i = 0; i < rendererCount; i++)
                {
                    var child = new GameObject("part_" + i);
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    child.AddComponent<MeshRenderer>();
                }
                var prefabPath = _assetRoot + "/" + name + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return prefabPath;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private string CreateRiggedPrefab(string name)
        {
            var mesh = TriangleSoup(name + "Mesh", 1);
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            };
            var meshPath = _assetRoot + "/" + name + ".asset";
            AssetDatabase.CreateAsset(mesh, meshPath);
            var root = new GameObject(name);
            try
            {
                var bone = new GameObject("bone").transform;
                bone.SetParent(root.transform, false);
                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                renderer.rootBone = bone;
                renderer.bones = new[] { bone };
                var prefabPath = _assetRoot + "/" + name + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return prefabPath;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static Mesh TriangleSoup(string name, int triangleCount)
        {
            var vertices = new Vector3[triangleCount * 3];
            var triangles = new int[vertices.Length];
            for (var i = 0; i < triangleCount; i++)
            {
                var offset = i * 3;
                var x = i * 0.001f;
                vertices[offset] = new Vector3(x, 0f, 0f);
                vertices[offset + 1] = new Vector3(x + 0.0005f, 0f, 0f);
                vertices[offset + 2] = new Vector3(x, 0.0005f, 0f);
                triangles[offset] = offset;
                triangles[offset + 1] = offset + 1;
                triangles[offset + 2] = offset + 2;
            }
            var mesh = new Mesh { name = name, vertices = vertices, triangles = triangles };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        private sealed class FakeGateway : IAi3dWorkflowGateway
        {
            private readonly Dictionary<string, Ai3dJobSnapshot> _jobs =
                new Dictionary<string, Ai3dJobSnapshot>(StringComparer.Ordinal);
            private int _next;

            public int BalanceCalls;
            public int UploadCalls;
            public int CancelCalls;
            public bool FailFirstCreate;
            public readonly List<string> CreatedOperations = new List<string>();
            public readonly List<string> PlanHashes = new List<string>();
            public readonly List<string> IdempotencyKeys = new List<string>();
            public readonly List<JObject> CreatedPayloads = new List<JObject>();

            public Task<Ai3dGatewayHealth> HealthAsync(CancellationToken token) =>
                Task.FromResult(new Ai3dGatewayHealth { Ready = true, Configured = true, Service = "fake", Version = "1" });
            public Task<JObject> CapabilitiesAsync(CancellationToken token) => Task.FromResult(new JObject
            {
                ["contractVersion"] = "test",
                ["operations"] = new JArray { "text_to_model", "convert" },
                ["durableJobs"] = true,
                ["remoteCancellation"] = false,
                ["artifactVaultRoot"] = _storeRootStatic,
            });
            public Task<Ai3dCredentialStatus> CredentialStatusAsync(CancellationToken token) =>
                Task.FromResult(new Ai3dCredentialStatus { Configured = true, Source = "memory" });
            public Task<JObject> BalanceAsync(CancellationToken token)
            {
                BalanceCalls++;
                return Task.FromResult(new JObject { ["balance"] = 10000, ["frozen"] = 0 });
            }
            public Task<Ai3dUploadSnapshot> UploadAsync(string path, CancellationToken token)
            {
                UploadCalls++;
                return Task.FromResult(new Ai3dUploadSnapshot
                {
                    FileToken = "file_test",
                    SourceName = Path.GetFileName(path),
                    ByteCount = new FileInfo(path).Length,
                    Sha256 = Ai3dGatewayClient.ComputeFileSha256(path),
                });
            }

            public Task<Ai3dJobSnapshot> CreateJobAsync(
                string operation, JObject payload, int estimate, int cap,
                IEnumerable<string> disclosureKinds, IEnumerable<string> sourceHashes,
                string idempotencyKey, string planHash, CancellationToken token)
            {
                if (FailFirstCreate)
                {
                    FailFirstCreate = false;
                    throw new Ai3dGatewayException("GATEWAY_HTTP_503", "synthetic retry");
                }
                CreatedOperations.Add(operation);
                CreatedPayloads.Add(payload.DeepClone() as JObject);
                PlanHashes.Add(planHash);
                IdempotencyKeys.Add(idempotencyKey);
                var id = (++_next).ToString("x32");
                var queued = new Ai3dJobSnapshot
                {
                    JobId = id,
                    Operation = operation,
                    PlanHash = planHash,
                    Status = "queued",
                    Progress = 0f,
                    EstimatedCredits = estimate,
                    ProviderTaskId = "task_" + operation,
                };
                _jobs[id] = queued;
                return Task.FromResult(queued);
            }

            public Task<Ai3dJobSnapshot> GetJobAsync(string jobId, CancellationToken token)
            {
                var queued = _jobs[jobId];
                var completed = new Ai3dJobSnapshot
                {
                    JobId = queued.JobId,
                    Operation = queued.Operation,
                    PlanHash = queued.PlanHash,
                    Status = "completed",
                    Progress = 1f,
                    EstimatedCredits = queued.EstimatedCredits,
                    CreditsConsumed = queued.Operation == "rig_check" ? 0 : queued.Operation == "convert" ? 3 : 7,
                    ProviderTaskId = queued.ProviderTaskId,
                };
                if (queued.Operation == "rig_check")
                    completed.ProviderResult = new JObject
                    {
                        ["output"] = new JObject { ["riggable"] = true, ["rig_type"] = "biped" },
                    };
                if (queued.Operation == "convert")
                    completed.Artifacts.Add(new Ai3dArtifactSnapshot { Path = "C:/vault/model.fbx" });
                return Task.FromResult(completed);
            }

            public Task<Ai3dJobSnapshot> CancelJobAsync(string jobId, CancellationToken token)
            {
                CancelCalls++;
                var job = _jobs[jobId];
                job.Status = "cancel_requested";
                job.CancelRequested = true;
                return Task.FromResult(job);
            }

            private static string _storeRootStatic => Path.GetTempPath();
        }
    }
}
