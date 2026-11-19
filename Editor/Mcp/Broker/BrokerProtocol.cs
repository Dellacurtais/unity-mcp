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

        // Captured on the main thread at load (Bootstrap); read later from worker threads.
        internal static string EditorPath = string.Empty;
        internal static string UnityVersion = string.Empty;
        internal static bool BatchMode;

        internal static void CaptureEditorIdentity()
        {
            EditorPath = UnityEditor.EditorApplication.applicationPath ?? string.Empty;
            UnityVersion = UnityEngine.Application.unityVersion ?? string.Empty;
            BatchMode = UnityEngine.Application.isBatchMode;
        }

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
                // Lets broker_open_editor reopen the project with this same Editor.
                ["editorPath"] = EditorPath,
                ["unityVersion"] = UnityVersion,
                ["batchMode"] = BatchMode,
            };
        }
    }
}
