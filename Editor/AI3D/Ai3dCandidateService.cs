using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Modeling.Validation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    public enum Ai3dIssueSeverity
    {
        Info,
        Warning,
        Error,
    }

    [Serializable]
    public sealed class Ai3dCandidateIssue
    {
        public string Code;
        public string Message;
        public Ai3dIssueSeverity Severity;

        public override string ToString() => Severity.ToString().ToUpperInvariant() + " " + Code + ": " + Message;
    }

    public sealed class Ai3dArtifactValidation
    {
        public string JobId;
        public string ProviderTaskId;
        public string PlanHash;
        public Ai3dArtifactSnapshot Artifact;
        public string AbsolutePath;
        public string CandidateHash;
        public string ValidationHash;
        public readonly List<Ai3dCandidateIssue> Issues = new List<Ai3dCandidateIssue>();

        public bool IsValid
        {
            get
            {
                for (int i = 0; i < Issues.Count; i++)
                    if (Issues[i].Severity == Ai3dIssueSeverity.Error) return false;
                return true;
            }
        }
    }

    public sealed class Ai3dStagedCandidate
    {
        public string JobId;
        public string ProviderTaskId;
        public string PlanHash;
        public string SourceArtifactHash;
        public string ImportedAssetHash;
        public string PreviewHash;
        public string StagedAssetPath;
        public string ManifestAssetPath;
        public string ValidationHash;
        public long ByteCount;
        public int MeshCount;
        public int VertexCount;
        public int TriangleCount;
        public Bounds LocalBounds;
        public GameObject Asset;
        public readonly List<Ai3dCandidateIssue> Issues = new List<Ai3dCandidateIssue>();

        public bool IsValid
        {
            get
            {
                for (int i = 0; i < Issues.Count; i++)
                    if (Issues[i].Severity == Ai3dIssueSeverity.Error) return false;
                return Asset != null && MeshCount > 0;
            }
        }
    }

    public static class Ai3dCandidateService
    {
        public const long MaximumArtifactBytes = 1024L * 1024L * 1024L;

        public static Ai3dArtifactValidation ValidateArtifact(
            Ai3dJobSnapshot job, Ai3dArtifactSnapshot artifact, string artifactVaultRoot)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            var result = new Ai3dArtifactValidation
            {
                JobId = job.JobId,
                ProviderTaskId = job.ProviderTaskId,
                PlanHash = job.PlanHash,
                Artifact = artifact,
            };
            if (!IsSha256(job.PlanHash))
                Add(result.Issues, "PLAN_HASH_INVALID", "The completed job has no valid plan hash.", Ai3dIssueSeverity.Error);
            if (!string.Equals(job.Status, "completed", StringComparison.Ordinal))
                Add(result.Issues, "JOB_NOT_COMPLETED", "Only a completed, non-cancelled job can be staged.", Ai3dIssueSeverity.Error);
            if (artifact == null)
            {
                Add(result.Issues, "ARTIFACT_REQUIRED", "Select an FBX artifact.", Ai3dIssueSeverity.Error);
                result.ValidationHash = ComputeArtifactValidationHash(result);
                return result;
            }
            if (!artifact.IsFbx)
                Add(result.Issues, "ARTIFACT_NOT_FBX", "Unity quarantine accepts FBX in Phase 2. Convert the provider task first.", Ai3dIssueSeverity.Error);
            if (string.IsNullOrWhiteSpace(artifact.ContentType))
                Add(result.Issues, "ARTIFACT_MIME_MISSING", "Artifact has no content type; FBX magic bytes are still required.", Ai3dIssueSeverity.Warning);
            else if (!IsApprovedFbxContentType(artifact.ContentType))
                Add(result.Issues, "ARTIFACT_MIME_REJECTED", "Artifact content type is not an approved FBX/binary MIME type.", Ai3dIssueSeverity.Error);
            if (!IsApprovedProviderHost(artifact.SourceHost, job.Provider))
                Add(result.Issues, "ARTIFACT_HOST_REJECTED",
                    "Artifact source host is not approved for provider " + (job.Provider ?? "tripo") + ".",
                    Ai3dIssueSeverity.Error);
            if (artifact.ByteCount <= 0 || artifact.ByteCount > MaximumArtifactBytes)
                Add(result.Issues, "ARTIFACT_SIZE_REJECTED", "Artifact size is empty or exceeds the 1 GB limit.", Ai3dIssueSeverity.Error);
            if (!IsSha256(artifact.Sha256))
                Add(result.Issues, "ARTIFACT_HASH_INVALID", "Gateway artifact SHA-256 is invalid.", Ai3dIssueSeverity.Error);
            try
            {
                result.AbsolutePath = Path.GetFullPath(artifact.Path ?? string.Empty);
                if (!File.Exists(result.AbsolutePath))
                {
                    Add(result.Issues, "ARTIFACT_FILE_MISSING", "Artifact is no longer present in the gateway vault.", Ai3dIssueSeverity.Error);
                }
                else
                {
                    var info = new FileInfo(result.AbsolutePath);
                    if (info.Length != artifact.ByteCount)
                        Add(result.Issues, "ARTIFACT_SIZE_MISMATCH", "Artifact byte count differs from the gateway manifest.", Ai3dIssueSeverity.Error);
                    if (IsUnderDirectory(result.AbsolutePath, Application.dataPath))
                        Add(result.Issues, "ARTIFACT_ALREADY_IN_ASSETS", "Provider artifacts must remain outside Assets until staged.", Ai3dIssueSeverity.Error);
                    if (!TryResolveJobVault(artifactVaultRoot, job.JobId, out var jobVault, out var vaultError))
                        Add(result.Issues, "ARTIFACT_VAULT_ROOT_INVALID", vaultError, Ai3dIssueSeverity.Error);
                    else if (!IsUnderDirectory(result.AbsolutePath, jobVault))
                        Add(result.Issues, "ARTIFACT_OUTSIDE_JOB_VAULT", "Artifact path is outside the authenticated vault folder for this job.", Ai3dIssueSeverity.Error);
                    else if (HasReparsePoint(result.AbsolutePath, jobVault))
                        Add(result.Issues, "ARTIFACT_REPARSE_POINT_REJECTED", "Artifact or job vault contains a symbolic link/reparse point.", Ai3dIssueSeverity.Error);
                    result.CandidateHash = Ai3dGatewayClient.ComputeFileSha256(result.AbsolutePath);
                    if (!string.Equals(result.CandidateHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        Add(result.Issues, "ARTIFACT_HASH_MISMATCH", "Artifact SHA-256 differs from the gateway manifest.", Ai3dIssueSeverity.Error);
                    if (!LooksLikeFbx(result.AbsolutePath))
                        Add(result.Issues, "ARTIFACT_MAGIC_REJECTED", "Artifact content does not look like binary or ASCII FBX.", Ai3dIssueSeverity.Error);
                }
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
            {
                Add(result.Issues, "ARTIFACT_READ_FAILED", exception.Message, Ai3dIssueSeverity.Error);
            }
            result.ValidationHash = ComputeArtifactValidationHash(result);
            return result;
        }

        public static Ai3dStagedCandidate Stage(Ai3dArtifactValidation validation, string quarantineRoot = null)
        {
            if (validation == null) throw new ArgumentNullException(nameof(validation));
            if (!validation.IsValid)
                throw new InvalidOperationException("AI3D_ARTIFACT_INVALID: fix artifact validation errors before staging.");
            var currentHash = Ai3dGatewayClient.ComputeFileSha256(validation.AbsolutePath);
            if (!string.Equals(currentHash, validation.CandidateHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AI3D_ARTIFACT_CHANGED: artifact hash changed after validation.");

            var root = AssetModelerAssetPaths.ValidateAndEnsureFolder(
                Ai3dGatewaySettings.NormalizeAssetFolder(
                    quarantineRoot, Ai3dGatewaySettings.QuarantineRoot));
            var jobFolder = AssetModelerAssetPaths.ValidateAndEnsureFolder(
                root + "/" + AssetModelerAssetPaths.SafeName(validation.JobId, "candidate"));
            var fileName = AssetModelerAssetPaths.SafeName(
                Path.GetFileNameWithoutExtension(validation.AbsolutePath), "candidate");
            var assetPath = AssetModelerAssetPaths.UniqueAssetPath(jobFolder, fileName, ".fbx");
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("UNITY_PROJECT_PATH_MISSING");
            var absoluteTarget = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            bool copied = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absoluteTarget) ?? projectRoot);
                File.Copy(validation.AbsolutePath, absoluteTarget, false);
                copied = true;
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                ConfigureImporter(assetPath);
                var staged = InspectImportedAsset(
                    assetPath,
                     validation.JobId,
                     validation.ProviderTaskId,
                     validation.PlanHash,
                     validation.CandidateHash,
                    new FileInfo(absoluteTarget).Length);
                staged.ManifestAssetPath = WriteManifest(staged, "quarantined");
                AssetDatabase.SaveAssets();
                return staged;
            }
            catch
            {
                var manifest = Path.ChangeExtension(assetPath, ".ai3d.json").Replace('\\', '/');
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(manifest) != null) AssetDatabase.DeleteAsset(manifest);
                if (copied) AssetDatabase.DeleteAsset(assetPath);
                throw;
            }
        }

        public static string Accept(Ai3dStagedCandidate staged, string previewHash, string acceptedRoot = null)
        {
            if (staged == null) throw new ArgumentNullException(nameof(staged));
            if (!staged.IsValid)
                throw new InvalidOperationException("AI3D_CANDIDATE_INVALID: only a valid quarantined candidate can be accepted.");
            var expectedPreviewHash = ComputePreviewHash(staged.Asset, staged.ValidationHash);
            if (!IsSha256(previewHash) || !string.Equals(previewHash, expectedPreviewHash, StringComparison.Ordinal))
                throw new InvalidOperationException("AI3D_PREVIEW_REVIEW_REQUIRED: review the current candidate in Scene View before accepting it.");
            staged.PreviewHash = previewHash;
            var current = InspectImportedAsset(
                staged.StagedAssetPath,
                staged.JobId,
                staged.ProviderTaskId,
                staged.PlanHash,
                staged.SourceArtifactHash,
                staged.ByteCount);
            if (!current.IsValid || !string.Equals(current.ValidationHash, staged.ValidationHash, StringComparison.Ordinal))
                throw new InvalidOperationException("AI3D_VALIDATION_STALE: candidate changed after review; validate and stage it again.");
            var root = AssetModelerAssetPaths.ValidateAndEnsureFolder(
                Ai3dGatewaySettings.NormalizeAssetFolder(acceptedRoot, Ai3dGatewaySettings.AcceptedRoot));
            var extension = Path.GetExtension(staged.StagedAssetPath);
            var target = AssetModelerAssetPaths.UniqueAssetPath(
                root,
                Path.GetFileNameWithoutExtension(staged.StagedAssetPath),
                extension);
            if (!AssetDatabase.CopyAsset(staged.StagedAssetPath, target))
                throw new InvalidOperationException("AI3D_ACCEPT_COPY_FAILED: AssetDatabase.CopyAsset returned false.");
            try
            {
                AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var accepted = InspectImportedAsset(
                    target,
                 staged.JobId,
                 staged.ProviderTaskId,
                 staged.PlanHash,
                 staged.SourceArtifactHash,
                 staged.ByteCount);
                accepted.PreviewHash = previewHash;
                if (!accepted.IsValid || !string.Equals(accepted.ValidationHash, staged.ValidationHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("AI3D_ACCEPT_VALIDATION_FAILED: copied asset differs from the reviewed candidate.");
                WriteManifest(accepted, "accepted");
                AssetDatabase.SaveAssets();
                return target;
            }
            catch
            {
                var manifest = Path.ChangeExtension(target, ".ai3d.json").Replace('\\', '/');
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(manifest) != null) AssetDatabase.DeleteAsset(manifest);
                AssetDatabase.DeleteAsset(target);
                throw;
            }
        }

        /// <summary>
        /// Rebuilds the validation snapshot for an existing quarantine asset. This
        /// is used by durable MCP workflows after a domain reload; no manifest hash
        /// is trusted without re-reading the imported FBX and its mesh channels.
        /// </summary>
        public static Ai3dStagedCandidate RevalidateStaged(
            string assetPath,
            string jobId,
            string providerTaskId,
            string planHash,
            string sourceArtifactHash)
        {
            if (string.IsNullOrWhiteSpace(assetPath)
                || (!assetPath.Equals("Assets", StringComparison.Ordinal)
                    && !assetPath.StartsWith("Assets/", StringComparison.Ordinal)))
                throw new InvalidOperationException("AI3D_STAGED_PATH_INVALID");
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("UNITY_PROJECT_PATH_MISSING");
            var absolute = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            if (!IsUnderDirectory(absolute, Application.dataPath) || !File.Exists(absolute))
                throw new InvalidOperationException("AI3D_STAGED_ASSET_MISSING");
            var staged = InspectImportedAsset(
                assetPath, jobId, providerTaskId, planHash, sourceArtifactHash,
                new FileInfo(absolute).Length);
            staged.ManifestAssetPath = Path.ChangeExtension(assetPath, ".ai3d.json").Replace('\\', '/');
            return staged;
        }

        public static GameObject InstantiateAccepted(string assetPath, Vector3 position)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null) throw new InvalidOperationException("AI3D_ACCEPTED_ASSET_MISSING");
            var instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            if (instance == null) instance = UnityEngine.Object.Instantiate(asset);
            instance.name = asset.name;
            instance.transform.position = position;
            Undo.RegisterCreatedObjectUndo(instance, "Add AI 3D candidate to scene");
            Selection.activeGameObject = instance;
            return instance;
        }

        internal static bool LooksLikeFbx(string path)
        {
            var buffer = new byte[256];
            int count;
            using (var stream = File.OpenRead(path)) count = stream.Read(buffer, 0, buffer.Length);
            if (count <= 0) return false;
            var prefix = Encoding.ASCII.GetString(buffer, 0, count);
            return prefix.StartsWith("Kaydara FBX Binary", StringComparison.Ordinal)
                   || prefix.IndexOf("FBXHeaderExtension", StringComparison.OrdinalIgnoreCase) >= 0
                   || (prefix.TrimStart().StartsWith(";", StringComparison.Ordinal)
                       && prefix.IndexOf("FBX", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool IsApprovedTripoHost(string host)
            => IsApprovedProviderHost(host, "tripo");

        internal static bool IsApprovedProviderHost(string host, string provider)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            provider = (provider ?? "tripo").Trim().ToLowerInvariant();
            if (provider == "meshy")
                return host == "assets.meshy.ai" || host.EndsWith(".meshy.ai", StringComparison.Ordinal);
            if (provider != "tripo") return false;
            return host.EndsWith(".tripo3d.ai", StringComparison.Ordinal)
                   || host.EndsWith(".tripo3d.com", StringComparison.Ordinal);
        }

        private static bool IsApprovedFbxContentType(string value)
        {
            var separator = value.IndexOf(';');
            if (separator >= 0) value = value.Substring(0, separator);
            value = value.Trim();
            return string.Equals(value, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "application/vnd.autodesk.fbx", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "model/fbx", StringComparison.OrdinalIgnoreCase);
        }

        private static void ConfigureImporter(string assetPath)
        {
            if (!(AssetImporter.GetAtPath(assetPath) is ModelImporter importer))
                throw new InvalidOperationException("AI3D_MODEL_IMPORTER_MISSING: FBX did not create a ModelImporter.");
            importer.isReadable = true;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.importBlendShapes = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importAnimation = true;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.SaveAndReimport();
        }

        private static Ai3dStagedCandidate InspectImportedAsset(
            string assetPath,
            string jobId,
            string providerTaskId,
            string planHash,
            string sourceArtifactHash,
            long byteCount)
        {
            var result = new Ai3dStagedCandidate
            {
                JobId = jobId,
                ProviderTaskId = providerTaskId,
                PlanHash = planHash,
                SourceArtifactHash = sourceArtifactHash,
                StagedAssetPath = assetPath,
                ByteCount = byteCount,
                Asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath),
            };
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                    ?? throw new InvalidOperationException("UNITY_PROJECT_PATH_MISSING");
                var absoluteAsset = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
                result.ImportedAssetHash = Ai3dGatewayClient.ComputeFileSha256(absoluteAsset);
                if (!string.Equals(result.ImportedAssetHash, sourceArtifactHash, StringComparison.OrdinalIgnoreCase))
                    Add(result.Issues, "IMPORTED_HASH_MISMATCH", "Imported FBX bytes differ from the reviewed vault artifact.", Ai3dIssueSeverity.Error);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
            {
                Add(result.Issues, "IMPORTED_HASH_FAILED", exception.Message, Ai3dIssueSeverity.Error);
            }
            if (result.Asset == null)
            {
                Add(result.Issues, "IMPORT_ROOT_MISSING", "Unity did not create an FBX GameObject asset.", Ai3dIssueSeverity.Error);
                result.ValidationHash = ComputeStageValidationHash(result);
                return result;
            }

            var all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            var boundsInitialized = false;
            for (int i = 0; i < all.Length; i++)
            {
                if (!(all[i] is Mesh mesh)) continue;
                result.MeshCount++;
                result.VertexCount += mesh.vertexCount;
                if (!mesh.isReadable)
                {
                    Add(result.Issues, "MESH_NOT_READABLE", mesh.name + " is not readable.", Ai3dIssueSeverity.Error);
                    continue;
                }
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    if (mesh.GetTopology(subMesh) == MeshTopology.Triangles)
                        result.TriangleCount += mesh.GetIndices(subMesh, true).Length / 3;
                var report = MeshValidator.Validate(mesh);
                for (int issue = 0; issue < report.Issues.Count; issue++)
                {
                    var value = report.Issues[issue];
                    Add(result.Issues, value.Code, mesh.name + ": " + value.Message,
                        value.IsError ? Ai3dIssueSeverity.Error : Ai3dIssueSeverity.Warning);
                }
                if (mesh.normals == null || mesh.normals.Length == 0)
                    Add(result.Issues, "NORMALS_MISSING", mesh.name + " has no imported normals.", Ai3dIssueSeverity.Warning);
                var uv0 = new List<Vector2>();
                mesh.GetUVs(0, uv0);
                if (uv0.Count == 0)
                    Add(result.Issues, "UV0_MISSING", mesh.name + " has no UV0 channel.", Ai3dIssueSeverity.Warning);
                if (!boundsInitialized)
                {
                    result.LocalBounds = mesh.bounds;
                    boundsInitialized = true;
                }
                else result.LocalBounds.Encapsulate(mesh.bounds);
            }
            if (result.MeshCount == 0)
                Add(result.Issues, "NO_MESHES", "Imported FBX contains no Mesh sub-assets.", Ai3dIssueSeverity.Error);
            if (TryCalculateRootLocalBounds(result.Asset, out var prefabBounds))
                result.LocalBounds = prefabBounds;
            if (result.TriangleCount > 2_000_000)
                Add(result.Issues, "TRIANGLE_BUDGET_HIGH", "Candidate exceeds 2,000,000 triangles.", Ai3dIssueSeverity.Warning);
            if (boundsInitialized)
            {
                var maximum = Mathf.Max(result.LocalBounds.size.x, result.LocalBounds.size.y, result.LocalBounds.size.z);
                if (maximum < 0.001f)
                    Add(result.Issues, "SCALE_TOO_SMALL", "Candidate bounds are below 1 mm in Unity units.", Ai3dIssueSeverity.Warning);
                if (maximum > 1000f)
                    Add(result.Issues, "SCALE_TOO_LARGE", "Candidate bounds exceed 1000 Unity units.", Ai3dIssueSeverity.Warning);
            }
            result.ValidationHash = ComputeStageValidationHash(result);
            return result;
        }

        private static string WriteManifest(Ai3dStagedCandidate staged, string status)
        {
            var manifestPath = Path.ChangeExtension(staged.StagedAssetPath, ".ai3d.json").Replace('\\', '/');
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("UNITY_PROJECT_PATH_MISSING");
            var absolute = Path.GetFullPath(Path.Combine(projectRoot, manifestPath));
            var issues = new JArray();
            for (int i = 0; i < staged.Issues.Count; i++)
            {
                issues.Add(new JObject
                {
                    ["code"] = staged.Issues[i].Code,
                    ["severity"] = staged.Issues[i].Severity.ToString().ToLowerInvariant(),
                    ["message"] = staged.Issues[i].Message,
                });
            }
            var json = new JObject
            {
                ["schemaVersion"] = 1,
                ["status"] = status,
                ["jobId"] = staged.JobId,
                ["providerTaskId"] = staged.ProviderTaskId,
                ["planHash"] = staged.PlanHash,
                ["sourceArtifactHash"] = staged.SourceArtifactHash,
                ["importedAssetHash"] = staged.ImportedAssetHash,
                ["validationHash"] = staged.ValidationHash,
                ["previewHash"] = staged.PreviewHash,
                ["assetPath"] = staged.StagedAssetPath,
                ["byteCount"] = staged.ByteCount,
                ["meshCount"] = staged.MeshCount,
                ["vertexCount"] = staged.VertexCount,
                ["triangleCount"] = staged.TriangleCount,
                ["valid"] = staged.IsValid,
                ["issues"] = issues,
                ["writtenUtc"] = DateTime.UtcNow.ToString("O"),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? projectRoot);
            File.WriteAllText(absolute, json.ToString(Formatting.Indented), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return manifestPath;
        }

        private static string ComputeArtifactValidationHash(Ai3dArtifactValidation validation)
        {
            var issues = new JArray();
            for (int i = 0; i < validation.Issues.Count; i++)
                issues.Add(validation.Issues[i].Code + ":" + validation.Issues[i].Severity);
            return Ai3dGatewaySettings.Sha256(new JObject
            {
                ["jobId"] = validation.JobId,
                ["providerTaskId"] = validation.ProviderTaskId,
                ["planHash"] = validation.PlanHash,
                ["path"] = validation.AbsolutePath,
                ["candidateHash"] = validation.CandidateHash,
                ["byteCount"] = validation.Artifact?.ByteCount ?? 0L,
                ["issues"] = issues,
            }.ToString(Formatting.None));
        }

        private static string ComputeStageValidationHash(Ai3dStagedCandidate staged)
        {
            var issues = new JArray();
            for (int i = 0; i < staged.Issues.Count; i++)
                issues.Add(staged.Issues[i].Code + ":" + staged.Issues[i].Severity);
            return Ai3dGatewaySettings.Sha256(new JObject
            {
                ["sourceArtifactHash"] = staged.SourceArtifactHash,
                ["importedAssetHash"] = staged.ImportedAssetHash,
                ["planHash"] = staged.PlanHash,
                ["meshCount"] = staged.MeshCount,
                ["vertexCount"] = staged.VertexCount,
                ["triangleCount"] = staged.TriangleCount,
                ["boundsCenter"] = Vector(staged.LocalBounds.center),
                ["boundsSize"] = Vector(staged.LocalBounds.size),
                ["issues"] = issues,
            }.ToString(Formatting.None));
        }

        private static JArray Vector(Vector3 value) => new JArray(value.x, value.y, value.z);

        internal static string ComputePreviewHash(GameObject asset, string validationHash)
        {
            if (asset == null || !IsSha256(validationHash)) return null;
            var assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return null;
            var dependencies = AssetDatabase.GetDependencies(assetPath, true);
            Array.Sort(dependencies, StringComparer.Ordinal);
            var dependencyHashes = new JArray();
            for (int i = 0; i < dependencies.Length; i++)
            {
                dependencyHashes.Add(new JObject
                {
                    ["path"] = dependencies[i],
                    ["hash"] = AssetDatabase.GetAssetDependencyHash(dependencies[i]).ToString(),
                });
            }
            return Ai3dGatewaySettings.Sha256(new JObject
            {
                ["schemaVersion"] = 1,
                ["assetPath"] = assetPath,
                ["dependencies"] = dependencyHashes,
                ["validationHash"] = validationHash,
                ["previewMode"] = "scene-view-static-v1",
            }.ToString(Formatting.None));
        }

        private static bool TryResolveJobVault(
            string artifactVaultRoot, string jobId, out string jobVault, out string error)
        {
            jobVault = null;
            error = null;
            if (string.IsNullOrWhiteSpace(artifactVaultRoot))
            {
                error = "Authenticated gateway capabilities did not provide an artifact vault root.";
                return false;
            }
            if (string.IsNullOrEmpty(jobId) || jobId.Length > 128)
            {
                error = "Job id is invalid for artifact vault validation.";
                return false;
            }
            for (int i = 0; i < jobId.Length; i++)
            {
                if (Uri.IsHexDigit(jobId[i])) continue;
                error = "Job id is invalid for artifact vault validation.";
                return false;
            }
            try
            {
                var root = Path.GetFullPath(artifactVaultRoot);
                jobVault = Path.GetFullPath(Path.Combine(root, jobId));
                if (!IsUnderDirectory(jobVault, root))
                {
                    error = "Job vault escaped the authenticated artifact vault root.";
                    return false;
                }
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool TryCalculateRootLocalBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            if (root == null) return false;
            var initialized = false;
            var rootInverse = root.transform.worldToLocalMatrix;
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i].sharedMesh == null) continue;
                var relative = rootInverse * filters[i].transform.localToWorldMatrix;
                Encapsulate(ref bounds, ref initialized, TransformBounds(filters[i].sharedMesh.bounds, relative));
            }
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                if (skinned[i].sharedMesh == null) continue;
                var relative = rootInverse * skinned[i].transform.localToWorldMatrix;
                Encapsulate(ref bounds, ref initialized, TransformBounds(skinned[i].localBounds, relative));
            }
            return initialized;
        }

        private static void Encapsulate(ref Bounds target, ref bool initialized, Bounds value)
        {
            if (!initialized)
            {
                target = value;
                initialized = true;
            }
            else target.Encapsulate(value);
        }

        private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var center = matrix.MultiplyPoint3x4(bounds.center);
            var extents = bounds.extents;
            var x = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            var y = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
            var z = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
            var transformed = new Vector3(
                Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(center, transformed * 2f);
        }

        private static bool IsUnderDirectory(string file, string directory)
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(file);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasReparsePoint(string file, string jobVault)
        {
            var root = Path.GetFullPath(jobVault).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(file).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return false;
                current = Path.GetDirectoryName(current)?.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return true;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
                if (!Uri.IsHexDigit(value[i])) return false;
            return true;
        }

        private static void Add(List<Ai3dCandidateIssue> issues, string code, string message, Ai3dIssueSeverity severity)
            => issues.Add(new Ai3dCandidateIssue { Code = code, Message = message, Severity = severity });
    }
}
