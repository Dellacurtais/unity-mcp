using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    public static class Ai3dGatewaySettings
    {
        public const string DefaultUrl = "http://127.0.0.1:8771";
        public const string DefaultModel = "v3.1-20260211";
        public const string DefaultQuarantineRoot = "Assets/AI3D/Quarantine";
        public const string DefaultAcceptedRoot = "Assets/AI3D/Generated";

        private const string UrlKey = "Maicon.SceneAnalyzer.AI3D.GatewayUrl";
        private const string TokenKey = "Maicon.SceneAnalyzer.AI3D.GatewayToken";
        private const string ModelKey = "Maicon.SceneAnalyzer.AI3D.Model";
        private const string QuarantineKey = "Maicon.SceneAnalyzer.AI3D.QuarantineRoot";
        private const string AcceptedKey = "Maicon.SceneAnalyzer.AI3D.AcceptedRoot";
        private const string TimeoutKey = "Maicon.SceneAnalyzer.AI3D.TimeoutSeconds";
        private const string LastJobKey = "Maicon.SceneAnalyzer.AI3D.LastJobId";
        private const string MaxWorkflowCreditsKey = "Maicon.SceneAnalyzer.AI3D.MaxWorkflowCredits";
        private const string LastWorkflowKey = "Maicon.SceneAnalyzer.AI3D.LastWorkflowId";

        public static string GatewayUrl
        {
            get => EditorPrefs.GetString(UrlKey, DefaultUrl);
            set => EditorPrefs.SetString(UrlKey, value ?? string.Empty);
        }

        public static string GatewayToken
        {
            get => EditorPrefs.GetString(TokenKey, string.Empty);
            set => EditorPrefs.SetString(TokenKey, value ?? string.Empty);
        }

        public static string ModelVersion
        {
            get => EditorPrefs.GetString(ModelKey, DefaultModel);
            set => EditorPrefs.SetString(ModelKey, value ?? DefaultModel);
        }

        public static string QuarantineRoot
        {
            get => EditorPrefs.GetString(QuarantineKey, DefaultQuarantineRoot);
            set => EditorPrefs.SetString(QuarantineKey, NormalizeAssetFolder(value, DefaultQuarantineRoot));
        }

        public static string AcceptedRoot
        {
            get => EditorPrefs.GetString(AcceptedKey, DefaultAcceptedRoot);
            set => EditorPrefs.SetString(AcceptedKey, NormalizeAssetFolder(value, DefaultAcceptedRoot));
        }

        public static int TimeoutSeconds
        {
            get => Math.Max(1, Math.Min(600, EditorPrefs.GetInt(TimeoutKey, 30)));
            set => EditorPrefs.SetInt(TimeoutKey, Math.Max(1, Math.Min(600, value)));
        }

        public static string LastJobId
        {
            get => EditorPrefs.GetString(LastJobKey, string.Empty);
            set => EditorPrefs.SetString(LastJobKey, value ?? string.Empty);
        }

        public static int MaxCreditsPerWorkflow
        {
            get => Math.Max(1, Math.Min(100000, EditorPrefs.GetInt(MaxWorkflowCreditsKey, 300)));
            set => EditorPrefs.SetInt(MaxWorkflowCreditsKey, Math.Max(1, Math.Min(100000, value)));
        }

        public static string LastWorkflowId
        {
            get => EditorPrefs.GetString(LastWorkflowKey, string.Empty);
            set => EditorPrefs.SetString(LastWorkflowKey, value ?? string.Empty);
        }

        public static string ProjectKey
        {
            get
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return "unity:" + Sha256(root.Replace('\\', '/').ToLowerInvariant()).Substring(0, 32);
            }
        }

        public static string GenerateGatewayToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        public static bool TryNormalizeGatewayUrl(string value, out string normalized, out string error)
        {
            normalized = null;
            error = null;
            if (string.IsNullOrWhiteSpace(value)) value = DefaultUrl;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            {
                error = "Gateway URL must be absolute.";
                return false;
            }
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                error = "Gateway URL must use HTTP on loopback.";
                return false;
            }
            if (!IsLoopback(uri.Host))
            {
                error = "Gateway URL must target localhost or a loopback IP.";
                return false;
            }
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "Gateway URL cannot contain credentials, query, or fragment.";
                return false;
            }
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.Length > 0)
            {
                error = "Gateway URL must not contain a path.";
                return false;
            }
            normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return true;
        }

        public static string NormalizeAssetFolder(string value, string fallback)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Replace('\\', '/').TrimEnd('/');
            if (!normalized.Equals("Assets", StringComparison.Ordinal)
                && !normalized.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("AI 3D folders must be inside Assets/.", nameof(value));
            var segments = normalized.Split('/');
            for (int i = 0; i < segments.Length; i++)
                if (segments[i] == "." || segments[i] == ".." || string.IsNullOrWhiteSpace(segments[i]))
                    throw new ArgumentException("AI 3D folders cannot contain empty, '.' or '..' segments.", nameof(value));
            return normalized;
        }

        private static bool IsLoopback(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        }

        internal static string Sha256(string value)
        {
            using (var algorithm = SHA256.Create())
            {
                var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
