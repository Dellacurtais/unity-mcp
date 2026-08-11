using System;
using System.IO;
using System.Text;
using Maicon.SceneAnalyzer.Editor.AI3D;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D.Tests
{
    public sealed class Ai3dPhaseTwoTests
    {
        [Test]
        public void GatewayUrl_AllowsOnlyLoopbackHttpWithoutPathOrCredentials()
        {
            Assert.That(Ai3dGatewaySettings.TryNormalizeGatewayUrl(
                "http://127.0.0.1:8771", out var normalized, out _), Is.True);
            Assert.That(normalized, Is.EqualTo("http://127.0.0.1:8771"));
            Assert.That(Ai3dGatewaySettings.TryNormalizeGatewayUrl(
                "https://127.0.0.1:8771", out _, out _), Is.False);
            Assert.That(Ai3dGatewaySettings.TryNormalizeGatewayUrl(
                "http://192.168.1.5:8771", out _, out _), Is.False);
            Assert.That(Ai3dGatewaySettings.TryNormalizeGatewayUrl(
                "http://user:password@127.0.0.1:8771", out _, out _), Is.False);
            Assert.That(Ai3dGatewaySettings.TryNormalizeGatewayUrl(
                "http://127.0.0.1:8771/v1", out _, out _), Is.False);
        }

        [Test]
        public void AssetFolders_RejectTraversalAndNonAssetsPaths()
        {
            Assert.That(
                Ai3dGatewaySettings.NormalizeAssetFolder("Assets/AI3D/Quarantine", "Assets/Fallback"),
                Is.EqualTo("Assets/AI3D/Quarantine"));
            Assert.Throws<ArgumentException>(() =>
                Ai3dGatewaySettings.NormalizeAssetFolder("D:/outside", "Assets/Fallback"));
            Assert.Throws<ArgumentException>(() =>
                Ai3dGatewaySettings.NormalizeAssetFolder("Assets/AI3D/../Escape", "Assets/Fallback"));
        }

        [Test]
        public void JobParser_PreservesArtifactsErrorsAndTerminalState()
        {
            var json = JObject.Parse(@"{
              'job_id':'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
              'project_key':'unity:test',
              'operation':'convert',
              'plan_hash':'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
              'status':'completed',
              'progress':1.0,
              'message':'completed',
              'provider_task_id':'task_abc',
              'estimated_credits':10,
              'credits_consumed':7,
              'cancel_requested':false,
              'artifacts':[{
                'logical_name':'model_url',
                'path':'C:/vault/model.fbx',
                'byte_count':42,
                'sha256':'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'content_type':'application/octet-stream',
                'source_host':'cdn.tripo3d.ai',
                'downloaded_utc':'2026-08-09T00:00:00Z'
              }]
            }");
            var job = Ai3dJobSnapshot.Parse(json);
            Assert.That(job.IsTerminal, Is.True);
            Assert.That(job.IsSuccessful, Is.True);
            Assert.That(job.FindFbxArtifact(), Is.Not.Null);
            Assert.That(job.CreditsConsumed, Is.EqualTo(7));
            Assert.That(job.PlanHash, Has.Length.EqualTo(64));
        }

        [Test]
        public void CredentialStatusParser_ExposesOnlySafeMetadata()
        {
            var status = Ai3dCredentialStatus.Parse(JObject.Parse(@"{
              'configured':true,
              'source':'windows_credential_manager',
              'storeAvailable':true,
              'persistent':true,
              'store':'windows_credential_manager',
              'platform':'win32'
            }"));
            Assert.That(status.Configured, Is.True);
            Assert.That(status.StoreAvailable, Is.True);
            Assert.That(status.Persistent, Is.True);
            Assert.That(status.Source, Is.EqualTo("windows_credential_manager"));
        }

        [Test]
        public void FbxMagic_AcceptsBinaryAndAsciiAndRejectsArbitraryData()
        {
            var folder = Path.Combine(Path.GetTempPath(), "ai3d-phase2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var binary = Path.Combine(folder, "binary.fbx");
                var ascii = Path.Combine(folder, "ascii.fbx");
                var invalid = Path.Combine(folder, "invalid.fbx");
                File.WriteAllBytes(binary, Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0synthetic"));
                File.WriteAllText(ascii, "; FBX 7.4.0 project file\nFBXHeaderExtension:  {", Encoding.ASCII);
                File.WriteAllText(invalid, "not a model", Encoding.ASCII);
                Assert.That(Ai3dCandidateService.LooksLikeFbx(binary), Is.True);
                Assert.That(Ai3dCandidateService.LooksLikeFbx(ascii), Is.True);
                Assert.That(Ai3dCandidateService.LooksLikeFbx(invalid), Is.False);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void ArtifactValidation_RequiresCompletedJobTrustedHostAndExactHash()
        {
            var jobId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var vaultRoot = Path.Combine(Path.GetTempPath(), "ai3d-vault-" + Guid.NewGuid().ToString("N"));
            var jobFolder = Path.Combine(vaultRoot, jobId);
            Directory.CreateDirectory(jobFolder);
            var path = Path.Combine(jobFolder, "model.fbx");
            File.WriteAllText(path, "; FBX 7.4.0 project file\nFBXHeaderExtension:  {", Encoding.ASCII);
            try
            {
                var artifact = new Ai3dArtifactSnapshot
                {
                    LogicalName = "model_url",
                    Path = path,
                    ByteCount = new FileInfo(path).Length,
                    Sha256 = Ai3dGatewayClient.ComputeFileSha256(path),
                    ContentType = "application/octet-stream",
                    SourceHost = "cdn.tripo3d.ai",
                };
                var job = new Ai3dJobSnapshot
                {
                    JobId = jobId,
                    ProviderTaskId = "task_abc",
                    PlanHash = new string('b', 64),
                    Status = "completed",
                };
                var valid = Ai3dCandidateService.ValidateArtifact(job, artifact, vaultRoot);
                Assert.That(valid.IsValid, Is.True, string.Join(" | ", valid.Issues));

                job.Provider = "meshy";
                artifact.SourceHost = "assets.meshy.ai";
                var meshyValid = Ai3dCandidateService.ValidateArtifact(job, artifact, vaultRoot);
                Assert.That(meshyValid.IsValid, Is.True, string.Join(" | ", meshyValid.Issues));
                artifact.SourceHost = "cdn.tripo3d.ai";
                var crossProvider = Ai3dCandidateService.ValidateArtifact(job, artifact, vaultRoot);
                Assert.That(crossProvider.Issues.Exists(issue => issue.Code == "ARTIFACT_HOST_REJECTED"), Is.True);
                job.Provider = "tripo";

                artifact.SourceHost = "example.com";
                var rejected = Ai3dCandidateService.ValidateArtifact(job, artifact, vaultRoot);
                Assert.That(rejected.IsValid, Is.False);
                Assert.That(rejected.Issues.Exists(issue => issue.Code == "ARTIFACT_HOST_REJECTED"), Is.True);

                artifact.SourceHost = "cdn.tripo3d.ai";
                artifact.ContentType = "text/html";
                rejected = Ai3dCandidateService.ValidateArtifact(job, artifact, vaultRoot);
                Assert.That(rejected.Issues.Exists(issue => issue.Code == "ARTIFACT_MIME_REJECTED"), Is.True);

                artifact.ContentType = "application/octet-stream";
                artifact.Sha256 = new string('0', 64);
                rejected = Ai3dCandidateService.ValidateArtifact(job, artifact, vaultRoot);
                Assert.That(rejected.Issues.Exists(issue => issue.Code == "ARTIFACT_HASH_MISMATCH"), Is.True);

                artifact.Sha256 = Ai3dGatewayClient.ComputeFileSha256(path);
                rejected = Ai3dCandidateService.ValidateArtifact(job, artifact, Path.GetTempPath());
                Assert.That(rejected.Issues.Exists(issue => issue.Code == "ARTIFACT_OUTSIDE_JOB_VAULT"), Is.True);

                job.PlanHash = null;
                rejected = Ai3dCandidateService.ValidateArtifact(job, artifact, vaultRoot);
                Assert.That(rejected.Issues.Exists(issue => issue.Code == "PLAN_HASH_INVALID"), Is.True);
            }
            finally
            {
                Directory.Delete(vaultRoot, true);
            }
        }

        [Test]
        public void PreviewHash_IsBoundToValidationAndImportedAssetDependencies()
        {
            var folderName = "__AI3DPhaseTwoTests_" + Guid.NewGuid().ToString("N");
            var folder = "Assets/" + folderName;
            var meshPath = folder + "/mesh.asset";
            var prefabPath = folder + "/candidate.prefab";
            GameObject temporary = null;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var mesh = new Mesh
                {
                    name = "AI3D preview fixture",
                    vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                    triangles = new[] { 0, 1, 2 },
                };
                mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, meshPath);
                temporary = new GameObject("AI3D preview fixture");
                temporary.AddComponent<MeshFilter>().sharedMesh = mesh;
                temporary.AddComponent<MeshRenderer>();
                var prefab = PrefabUtility.SaveAsPrefabAsset(temporary, prefabPath);
                UnityEngine.Object.DestroyImmediate(temporary);
                temporary = null;
                AssetDatabase.SaveAssets();

                var validationHash = new string('c', 64);
                var first = Ai3dCandidateService.ComputePreviewHash(prefab, validationHash);
                var repeated = Ai3dCandidateService.ComputePreviewHash(prefab, validationHash);
                Assert.That(first, Has.Length.EqualTo(64));
                Assert.That(repeated, Is.EqualTo(first));

                mesh.vertices = new[] { Vector3.zero, Vector3.right * 2f, Vector3.up };
                mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(meshPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var changed = Ai3dCandidateService.ComputePreviewHash(prefab, validationHash);
                Assert.That(changed, Is.Not.EqualTo(first));
            }
            finally
            {
                if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
