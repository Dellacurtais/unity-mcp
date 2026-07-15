using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class ConfirmationTokenService
    {
        private sealed class Entry
        {
            public string ToolName;
            public string ArgsHash;
            public string ProjectKey;
            public DateTimeOffset ExpiresAtUtc;
            public bool Used;
        }

        private readonly ConcurrentDictionary<string, Entry> _tokens = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        public string Issue(string toolName, JObject args, string projectKey, TimeSpan ttl)
        {
            PruneExpired();
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _tokens[token] = new Entry
            {
                ToolName = toolName ?? string.Empty,
                ArgsHash = Hash(args),
                ProjectKey = projectKey ?? string.Empty,
                ExpiresAtUtc = DateTimeOffset.UtcNow.Add(ttl),
            };
            return token;
        }

        public bool TryConsume(string token, string toolName, JObject args, string projectKey)
        {
            if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var entry)) return false;
            lock (entry)
            {
                if (entry.Used || entry.ExpiresAtUtc <= DateTimeOffset.UtcNow) return false;
                if (!string.Equals(entry.ToolName, toolName ?? string.Empty, StringComparison.Ordinal)) return false;
                if (!string.Equals(entry.ProjectKey, projectKey ?? string.Empty, StringComparison.Ordinal)) return false;
                if (!string.Equals(entry.ArgsHash, Hash(args), StringComparison.Ordinal)) return false;
                entry.Used = true;
                _tokens.TryRemove(token, out _);
                return true;
            }
        }

        private void PruneExpired()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _tokens)
                if (pair.Value.ExpiresAtUtc <= now || pair.Value.Used) _tokens.TryRemove(pair.Key, out _);
        }

        private static string Hash(JObject args)
        {
            var canonical = Canonicalize(args ?? new JObject()).ToString(Formatting.None);
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }

        private static JToken Canonicalize(JToken token)
        {
            if (token is JObject obj)
            {
                var result = new JObject();
                foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    result[property.Name] = Canonicalize(property.Value);
                return result;
            }
            if (token is JArray array) return new JArray(array.Select(Canonicalize));
            return token.DeepClone();
        }
    }
}
