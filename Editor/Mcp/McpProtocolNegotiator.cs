using System;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public static class McpProtocolNegotiator
    {
        public const string V2024 = "2024-11-05";
        public const string V2025March = "2025-03-26";
        public const string V2025June = "2025-06-18";
        public static readonly string[] Supported = { V2025June, V2025March, V2024 };
        public static string Preferred => V2025June;

        public static bool IsSupported(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            foreach (var supported in Supported)
                if (string.Equals(supported, version, StringComparison.Ordinal)) return true;
            return false;
        }

        public static string Negotiate(string requested)
        {
            return IsSupported(requested) ? requested : Preferred;
        }

        public static bool IsModern(string version) =>
            string.Equals(version, V2025March, StringComparison.Ordinal) ||
            string.Equals(version, V2025June, StringComparison.Ordinal);
    }
}
