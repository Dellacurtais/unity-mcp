using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Broker
{
    internal static class BrokerProtocol
    {
        public const int Version = 1;

        public static readonly string GenerationId = Guid.NewGuid().ToString("N");

        public static JObject Envelope(
            string type,
            JObject payload,
            string projectKey,
            int processId,
            string correlationId = null,
            string messageId = null)
        {
            return new JObject
            {
                ["version"] = Version,
                ["type"] = type,
                ["messageId"] = messageId ?? Guid.NewGuid().ToString("N"),
                ["correlationId"] = correlationId == null ? JValue.CreateNull() : correlationId,
                ["projectKey"] = projectKey,
                ["processId"] = processId,
                ["generationId"] = GenerationId,
                ["payload"] = payload ?? new JObject(),
            };
        }

        public static string ProjectKey(string projectRoot)
        {
            var normalized = (projectRoot ?? string.Empty)
                .Replace('\\', '/')
                .TrimEnd('/')
                .ToLowerInvariant();
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        public static JObject Identity(string projectRoot, int registryCount, string registryHash)
        {
            var process = Process.GetCurrentProcess();
            return new JObject
            {
                ["projectKey"] = ProjectKey(projectRoot),
                ["projectName"] = Path.GetFileName(projectRoot ?? string.Empty),
                ["projectPath"] = projectRoot ?? string.Empty,
                ["processId"] = process.Id,
                ["processStartUtc"] = process.StartTime.ToUniversalTime().ToString("O"),
                ["generationId"] = GenerationId,
                ["packageVersion"] = SceneAnalyzerVersion.Current,
                ["registryCount"] = registryCount,
                ["registryHash"] = registryHash ?? string.Empty,
            };
        }
    }
}
