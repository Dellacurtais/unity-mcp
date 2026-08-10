using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    public static class Ai3dGatewayClient
    {
        private const int MaxResponseBytes = 4 * 1024 * 1024;

        private sealed class HttpResponse
        {
            public long StatusCode;
            public string Text;
            public string Error;
            public UnityWebRequest.Result Result;
        }

        public static async Task<Ai3dGatewayHealth> HealthAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync("GET", "/health", null, false, null, cancellationToken);
            var json = ParseObject(response.Text, "health");
            return new Ai3dGatewayHealth
            {
                Ready = (bool?)json["ready"] ?? false,
                Configured = (bool?)json["configured"] ?? false,
                Service = (string)json["service"],
                Version = (string)json["version"],
            };
        }

        public static async Task<JObject> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync("GET", "/v1/capabilities", null, true, null, cancellationToken);
            return ParseObject(response.Text, "capabilities");
        }

        public static async Task<JObject> GetBalanceAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendAsync("GET", "/v1/account/balance", null, true, null, cancellationToken);
            return ParseObject(response.Text, "balance");
        }

        public static async Task<Ai3dCredentialStatus> GetCredentialStatusAsync(
            CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(
                "GET", "/v1/credentials/tripo", null, true, null, cancellationToken);
            return Ai3dCredentialStatus.Parse(ParseObject(response.Text, "credential status"));
        }

        public static async Task<Ai3dCredentialStatus> SetTripoApiKeyAsync(
            string apiKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 8 || apiKey.Length > 2048)
                throw new ArgumentException("Tripo API key must contain 8-2048 characters.", nameof(apiKey));
            if (!string.Equals(apiKey, apiKey.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("Tripo API key cannot start or end with whitespace.", nameof(apiKey));
            var body = new JObject { ["apiKey"] = apiKey };
            try
            {
                var response = await SendAsync(
                    "PUT", "/v1/credentials/tripo", body, true, null, cancellationToken);
                return Ai3dCredentialStatus.Parse(ParseObject(response.Text, "credential update"));
            }
            finally
            {
                body.RemoveAll();
            }
        }

        public static async Task<Ai3dCredentialStatus> DeleteTripoApiKeyAsync(
            CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(
                "DELETE", "/v1/credentials/tripo", null, true, null, cancellationToken);
            return Ai3dCredentialStatus.Parse(ParseObject(response.Text, "credential removal"));
        }

        public static async Task<List<Ai3dJobSummary>> ListJobsAsync(
            int limit = 30, CancellationToken cancellationToken = default)
        {
            limit = Mathf.Clamp(limit, 1, 100);
            var response = await SendAsync("GET", "/v1/jobs?limit=" + limit, null, true, null, cancellationToken);
            var root = ParseObject(response.Text, "jobs");
            var result = new List<Ai3dJobSummary>();
            foreach (var item in root["jobs"] as JArray ?? new JArray())
            {
                var parsed = Ai3dJobSummary.Parse(item);
                if (parsed != null) result.Add(parsed);
            }
            return result;
        }

        public static async Task<Ai3dJobSnapshot> GetJobAsync(
            string jobId, CancellationToken cancellationToken = default)
        {
            ValidateIdentifier(jobId, "jobId");
            var response = await SendAsync("GET", "/v1/jobs/" + jobId, null, true, null, cancellationToken);
            return Ai3dJobSnapshot.Parse(ParseObject(response.Text, "job"));
        }

        public static async Task<Ai3dJobSnapshot> CancelJobAsync(
            string jobId, CancellationToken cancellationToken = default)
        {
            ValidateIdentifier(jobId, "jobId");
            var response = await SendAsync("POST", "/v1/jobs/" + jobId + "/cancel", new JObject(), true, null, cancellationToken);
            return Ai3dJobSnapshot.Parse(ParseObject(response.Text, "cancel"));
        }

        public static Task<Ai3dJobSnapshot> CreateTextJobAsync(
            string prompt,
            string model,
            int faceLimit,
            bool texture,
            bool pbr,
            bool smartLowPoly,
            string geometryQuality,
            int estimatedCredits,
            int capCredits,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt is required.", nameof(prompt));
            var payload = new JObject
            {
                ["prompt"] = prompt.Trim(),
                ["model"] = string.IsNullOrWhiteSpace(model) ? Ai3dGatewaySettings.DefaultModel : model.Trim(),
                ["face_limit"] = smartLowPoly ? Mathf.Clamp(faceLimit, 500, 20000) : Mathf.Max(500, faceLimit),
                ["texture"] = texture,
                ["pbr"] = pbr,
                ["geometry_quality"] = string.IsNullOrWhiteSpace(geometryQuality) ? "standard" : geometryQuality,
            };
            if (smartLowPoly) payload["smart_low_poly"] = true;
            return CreateJobAsync(
                "text_to_model", payload, estimatedCredits, capCredits,
                new[] { "text" }, Array.Empty<string>(), idempotencyKey, cancellationToken);
        }

        public static Task<Ai3dJobSnapshot> CreateImageJobAsync(
            string fileToken,
            string sourceHash,
            string model,
            int faceLimit,
            bool texture,
            bool pbr,
            bool smartLowPoly,
            string geometryQuality,
            int estimatedCredits,
            int capCredits,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(fileToken))
                throw new ArgumentException("fileToken is required.", nameof(fileToken));
            var payload = new JObject
            {
                ["input"] = fileToken,
                ["model"] = string.IsNullOrWhiteSpace(model) ? Ai3dGatewaySettings.DefaultModel : model.Trim(),
                ["face_limit"] = smartLowPoly ? Mathf.Clamp(faceLimit, 500, 20000) : Mathf.Max(500, faceLimit),
                ["texture"] = texture,
                ["pbr"] = pbr,
                ["geometry_quality"] = string.IsNullOrWhiteSpace(geometryQuality) ? "standard" : geometryQuality,
            };
            if (smartLowPoly) payload["smart_low_poly"] = true;
            var hashes = string.IsNullOrWhiteSpace(sourceHash) ? Array.Empty<string>() : new[] { sourceHash };
            return CreateJobAsync(
                "image_to_model", payload, estimatedCredits, capCredits,
                new[] { "image" }, hashes, idempotencyKey, cancellationToken);
        }

        public static Task<Ai3dJobSnapshot> CreateConvertToFbxJobAsync(
            string providerTaskId,
            int estimatedCredits,
            int capCredits,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            ValidateProviderTaskId(providerTaskId);
            var payload = new JObject
            {
                ["input"] = providerTaskId,
                ["format"] = "FBX",
                ["texture_size"] = 2048,
                ["texture_format"] = "PNG",
                ["bake"] = true,
                ["with_animation"] = true,
                ["fbx_preset"] = "blender",
            };
            return CreateJobAsync(
                "convert", payload, estimatedCredits, capCredits,
                new[] { "task_reference" }, Array.Empty<string>(), idempotencyKey, cancellationToken);
        }

        public static async Task<Ai3dUploadSnapshot> UploadAsync(
            string sourcePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            var absolute = Path.GetFullPath(sourcePath);
            if (!File.Exists(absolute)) throw new FileNotFoundException("AI3D_UPLOAD_SOURCE_NOT_FOUND", absolute);
            var sha256 = await Task.Run(() => ComputeFileSha256(absolute), cancellationToken);
            var body = new JObject
            {
                ["project_key"] = Ai3dGatewaySettings.ProjectKey,
                ["source_path"] = absolute,
                ["expected_sha256"] = sha256,
                ["external_send_approved"] = true,
            };
            var response = await SendAsync("POST", "/v1/uploads", body, true, null, cancellationToken);
            var json = ParseObject(response.Text, "upload");
            return new Ai3dUploadSnapshot
            {
                FileToken = (string)json["fileToken"],
                SourceName = (string)json["sourceName"],
                ByteCount = (long?)json["byteCount"] ?? 0L,
                Sha256 = (string)json["sha256"],
            };
        }

        internal static Task<Ai3dJobSnapshot> CreateJobAsync(
            string operation,
            JObject payload,
            int estimatedCredits,
            int capCredits,
            IEnumerable<string> disclosureKinds,
            IEnumerable<string> sourceHashes,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
            => CreateJobCoreAsync(
                operation, payload, estimatedCredits, capCredits, disclosureKinds,
                sourceHashes, idempotencyKey, null, cancellationToken);

        internal static Task<Ai3dJobSnapshot> CreateWorkflowJobAsync(
            string operation,
            JObject payload,
            int estimatedCredits,
            int capCredits,
            IEnumerable<string> disclosureKinds,
            IEnumerable<string> sourceHashes,
            string idempotencyKey,
            string planHash,
            CancellationToken cancellationToken = default)
        {
            if (!IsSha256(planHash))
                throw new ArgumentException("planHash must be a SHA-256 hex value.", nameof(planHash));
            return CreateJobCoreAsync(
                operation, payload, estimatedCredits, capCredits, disclosureKinds,
                sourceHashes, idempotencyKey, planHash.ToLowerInvariant(), cancellationToken);
        }

        private static async Task<Ai3dJobSnapshot> CreateJobCoreAsync(
            string operation,
            JObject payload,
            int estimatedCredits,
            int capCredits,
            IEnumerable<string> disclosureKinds,
            IEnumerable<string> sourceHashes,
            string idempotencyKey,
            string explicitPlanHash,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
            estimatedCredits = Math.Max(0, estimatedCredits);
            capCredits = Math.Max(estimatedCredits, capCredits);
            payload = payload ?? throw new ArgumentNullException(nameof(payload));
            var plan = new JObject
            {
                ["project_key"] = Ai3dGatewaySettings.ProjectKey,
                ["operation"] = operation,
                ["payload"] = payload.DeepClone(),
            };
            var body = new JObject
            {
                ["project_key"] = Ai3dGatewaySettings.ProjectKey,
                ["operation"] = operation,
                ["plan_hash"] = explicitPlanHash ?? Ai3dGatewaySettings.Sha256(plan.ToString(Formatting.None)),
                ["payload"] = payload,
                ["budget"] = new JObject
                {
                    ["estimated_credits"] = estimatedCredits,
                    ["cap_credits"] = capCredits,
                },
                ["disclosure"] = new JObject
                {
                    ["external_send_approved"] = true,
                    ["kinds"] = new JArray(disclosureKinds ?? Array.Empty<string>()),
                    ["source_hashes"] = new JArray(sourceHashes ?? Array.Empty<string>()),
                },
                ["auto_download"] = true,
            };
            var response = await SendAsync("POST", "/v1/jobs", body, true, idempotencyKey, cancellationToken);
            var root = ParseObject(response.Text, "create job");
            var job = Ai3dJobSnapshot.Parse(root["job"]);
            Ai3dGatewaySettings.LastJobId = job.JobId;
            return job;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
                if (!Uri.IsHexDigit(value[i])) return false;
            return true;
        }

        internal static string ComputeFileSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var algorithm = SHA256.Create())
            {
                var bytes = algorithm.ComputeHash(stream);
                var builder = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
                return builder.ToString();
            }
        }

        private static async Task<HttpResponse> SendAsync(
            string method,
            string relativePath,
            JObject body,
            bool authenticated,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (!Ai3dGatewaySettings.TryNormalizeGatewayUrl(
                    Ai3dGatewaySettings.GatewayUrl, out var baseUrl, out var urlError))
                throw new Ai3dGatewayException("GATEWAY_URL_REJECTED", urlError);
            if (authenticated && string.IsNullOrWhiteSpace(Ai3dGatewaySettings.GatewayToken))
                throw new Ai3dGatewayException("GATEWAY_TOKEN_REQUIRED", "Configure the local gateway token in AI 3D Studio.");
            var bytes = body != null ? Encoding.UTF8.GetBytes(body.ToString(Formatting.None)) : null;
            UnityWebRequest request = null;
            try
            {
                request = await MainThreadDispatcher.RunAsync(() =>
                {
                    var created = new UnityWebRequest(baseUrl + relativePath, method)
                    {
                        downloadHandler = new DownloadHandlerBuffer(),
                        timeout = Ai3dGatewaySettings.TimeoutSeconds,
                    };
                    if (bytes != null)
                        created.uploadHandler = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                    created.SetRequestHeader("Accept", "application/json");
                    if (authenticated)
                        created.SetRequestHeader("Authorization", "Bearer " + Ai3dGatewaySettings.GatewayToken);
                    if (!string.IsNullOrEmpty(idempotencyKey))
                        created.SetRequestHeader("Idempotency-Key", idempotencyKey);
                    return created;
                }, cancellationToken);

                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await MainThreadDispatcher.RunAsync(() =>
                {
                    var operation = request.SendWebRequest();
                    operation.completed += _ => completion.TrySetResult(true);
                    if (operation.isDone) completion.TrySetResult(true);
                }, cancellationToken);
                using (cancellationToken.Register(() =>
                {
                    completion.TrySetCanceled(cancellationToken);
                    _ = MainThreadDispatcher.RunAsync(() => request?.Abort());
                }))
                {
                    await completion.Task;
                }

                var response = await MainThreadDispatcher.RunAsync(() =>
                {
                    var responseBytes = request.downloadHandler?.data;
                    if (responseBytes != null && responseBytes.Length > MaxResponseBytes)
                        throw new Ai3dGatewayException("GATEWAY_RESPONSE_TOO_LARGE", "Gateway response exceeds 4 MB.");
                    return new HttpResponse
                    {
                        StatusCode = request.responseCode,
                        Text = request.downloadHandler?.text ?? string.Empty,
                        Error = request.error,
                        Result = request.result,
                    };
                }, cancellationToken);
                if (response.Result != UnityWebRequest.Result.Success)
                    throw ParseGatewayError(response);
                return response;
            }
            finally
            {
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                if (request != null)
                {
                    try { await MainThreadDispatcher.RunAsync(request.Dispose); }
                    catch { request.Dispose(); }
                }
            }
        }

        private static Ai3dGatewayException ParseGatewayError(HttpResponse response)
        {
            try
            {
                var json = JObject.Parse(response.Text ?? string.Empty);
                var error = json["error"] as JObject;
                if (error != null)
                {
                    return new Ai3dGatewayException(
                        (string)error["code"],
                        (string)error["message"] ?? "Gateway request failed.",
                        response.StatusCode,
                        (string)error["requestId"]);
                }
                var detail = (string)json["detail"];
                if (!string.IsNullOrEmpty(detail))
                    return new Ai3dGatewayException("GATEWAY_HTTP_" + response.StatusCode, detail, response.StatusCode);
            }
            catch (JsonException)
            {
                // Fall through to a bounded transport error without echoing headers or tokens.
            }
            var message = string.IsNullOrEmpty(response.Error) ? "Gateway request failed." : response.Error;
            return new Ai3dGatewayException("GATEWAY_HTTP_" + response.StatusCode, message, response.StatusCode);
        }

        private static JObject ParseObject(string text, string context)
        {
            try { return JObject.Parse(text ?? string.Empty); }
            catch (JsonException exception)
            {
                throw new Ai3dGatewayException(
                    "GATEWAY_RESPONSE_INVALID", "Invalid JSON returned for " + context + ": " + exception.Message);
            }
        }

        private static void ValidateIdentifier(string value, string name)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
                throw new ArgumentException(name + " is invalid.", name);
            for (int i = 0; i < value.Length; i++)
                if (!Uri.IsHexDigit(value[i])) throw new ArgumentException(name + " is invalid.", name);
        }

        private static void ValidateProviderTaskId(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("task_", StringComparison.Ordinal) || value.Length > 256)
                throw new ArgumentException("providerTaskId is invalid.", nameof(value));
        }
    }
}
