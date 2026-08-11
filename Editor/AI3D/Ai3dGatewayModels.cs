using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    public enum Ai3dGenerationPreset
    {
        GameMobile = 0,
        GamePc = 1,
        HeroProp = 2,
        CharacterHumanoid = 3,
    }

    public sealed class Ai3dGatewayHealth
    {
        public bool Ready;
        public bool Configured;
        public string Service;
        public string Version;
        public string Error;
    }

    public sealed class Ai3dCredentialStatus
    {
        public bool Configured;
        public string Source;
        public bool StoreAvailable;
        public bool Persistent;
        public string Store;
        public string Platform;

        internal static Ai3dCredentialStatus Parse(JObject json)
        {
            if (json == null)
                throw new Ai3dGatewayException("GATEWAY_RESPONSE_INVALID", "Credential status is not an object.");
            return new Ai3dCredentialStatus
            {
                Configured = (bool?)json["configured"] ?? false,
                Source = (string)json["source"],
                StoreAvailable = (bool?)json["storeAvailable"] ?? false,
                Persistent = (bool?)json["persistent"] ?? false,
                Store = (string)json["store"],
                Platform = (string)json["platform"],
            };
        }
    }

    public sealed class Ai3dArtifactSnapshot
    {
        public string LogicalName;
        public string Path;
        public long ByteCount;
        public string Sha256;
        public string ContentType;
        public string SourceHost;
        public string DownloadedUtc;

        public bool IsFbx
            => !string.IsNullOrEmpty(Path)
               && Path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);

        internal static Ai3dArtifactSnapshot Parse(JToken token)
        {
            if (!(token is JObject json)) return null;
            return new Ai3dArtifactSnapshot
            {
                LogicalName = (string)json["logical_name"],
                Path = (string)json["path"],
                ByteCount = (long?)json["byte_count"] ?? 0L,
                Sha256 = (string)json["sha256"],
                ContentType = (string)json["content_type"],
                SourceHost = (string)json["source_host"],
                DownloadedUtc = (string)json["downloaded_utc"],
            };
        }
    }

    public sealed class Ai3dJobError
    {
        public string Code;
        public string Message;
        public string RequestId;
    }

    public sealed class Ai3dJobSnapshot
    {
        private static readonly HashSet<string> TerminalStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            "completed", "failed", "cancelled", "completed_after_cancel",
        };

        public string JobId;
        public string ProjectKey;
        public string Provider;
        public string Operation;
        public string PlanHash;
        public string Status;
        public float Progress;
        public string Message;
        public string ProviderTaskId;
        public int EstimatedCredits;
        public int? CreditsConsumed;
        public bool CancelRequested;
        public string CreatedUtc;
        public string UpdatedUtc;
        public string CompletedUtc;
        public Ai3dJobError Error;
        public JObject ProviderResult;
        public readonly List<Ai3dArtifactSnapshot> Artifacts = new List<Ai3dArtifactSnapshot>();

        public bool IsTerminal => TerminalStatuses.Contains(Status ?? string.Empty);
        public bool IsSuccessful
            => string.Equals(Status, "completed", StringComparison.Ordinal)
               || string.Equals(Status, "completed_after_cancel", StringComparison.Ordinal);

        public Ai3dArtifactSnapshot FindFbxArtifact()
        {
            for (int i = 0; i < Artifacts.Count; i++)
                if (Artifacts[i] != null && Artifacts[i].IsFbx) return Artifacts[i];
            return null;
        }

        internal static Ai3dJobSnapshot Parse(JToken token)
        {
            if (!(token is JObject json))
                throw new Ai3dGatewayException("GATEWAY_RESPONSE_INVALID", "Job response is not an object.");
            var result = new Ai3dJobSnapshot
            {
                JobId = (string)json["job_id"],
                ProjectKey = (string)json["project_key"],
                Provider = (string)json["provider"] ?? "tripo",
                Operation = (string)json["operation"],
                PlanHash = (string)json["plan_hash"],
                Status = (string)json["status"],
                Progress = (float?)json["progress"] ?? 0f,
                Message = (string)json["message"],
                ProviderTaskId = (string)json["provider_task_id"],
                EstimatedCredits = (int?)json["estimated_credits"] ?? 0,
                CreditsConsumed = (int?)json["credits_consumed"],
                CancelRequested = (bool?)json["cancel_requested"] ?? false,
                CreatedUtc = (string)json["created_utc"],
                UpdatedUtc = (string)json["updated_utc"],
                CompletedUtc = (string)json["completed_utc"],
                ProviderResult = json["provider_result"] as JObject,
            };
            if (json["error"] is JObject error)
            {
                result.Error = new Ai3dJobError
                {
                    Code = (string)error["code"],
                    Message = (string)error["message"],
                    RequestId = (string)error["request_id"] ?? (string)error["requestId"],
                };
            }
            foreach (var artifact in json["artifacts"] as JArray ?? new JArray())
            {
                var parsed = Ai3dArtifactSnapshot.Parse(artifact);
                if (parsed != null) result.Artifacts.Add(parsed);
            }
            return result;
        }
    }

    public sealed class Ai3dJobSummary
    {
        public string JobId;
        public string Provider;
        public string Operation;
        public string Status;
        public float Progress;
        public string Message;
        public string UpdatedUtc;
        public int ArtifactCount;
        public Ai3dJobError Error;

        internal static Ai3dJobSummary Parse(JToken token)
        {
            if (!(token is JObject json)) return null;
            var result = new Ai3dJobSummary
            {
                JobId = (string)json["job_id"],
                Provider = (string)json["provider"] ?? "tripo",
                Operation = (string)json["operation"],
                Status = (string)json["status"],
                Progress = (float?)json["progress"] ?? 0f,
                Message = (string)json["message"],
                UpdatedUtc = (string)json["updated_utc"],
                ArtifactCount = (int?)json["artifact_count"] ?? 0,
            };
            if (json["error"] is JObject error)
            {
                result.Error = new Ai3dJobError
                {
                    Code = (string)error["code"],
                    Message = (string)error["message"],
                    RequestId = (string)error["request_id"] ?? (string)error["requestId"],
                };
            }
            return result;
        }
    }

    public sealed class Ai3dUploadSnapshot
    {
        public string Provider;
        public string FileToken;
        public string SourceName;
        public long ByteCount;
        public string Sha256;
    }

    public sealed class Ai3dGatewayException : Exception
    {
        public string Code { get; }
        public long StatusCode { get; }
        public string RequestId { get; }

        public Ai3dGatewayException(string code, string message, long statusCode = 0L, string requestId = null)
            : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? "GATEWAY_ERROR" : code;
            StatusCode = statusCode;
            RequestId = requestId;
        }
    }
}
