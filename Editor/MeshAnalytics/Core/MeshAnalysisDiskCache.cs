using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// Persists AnalysisResult bundles to disk under
    /// `<project>/Library/MeshAnalytics/`. Library/ is gitignored by default
    /// in Unity projects and Unity rebuilds it on demand, so disk cache
    /// entries are scratch — not part of the source repo, safe to nuke.
    ///
    /// Entries are JSON, one file per (meshKey, paramsHash) pair. The
    /// AssetPostprocessor pre-warms the cache when a mesh asset is
    /// (re-)imported; runtime tool calls then hit the in-memory cache after
    /// a single deserialise.
    ///
    /// Validity check on read: the cached AnalysisResult embeds the
    /// OwnerLocalToWorld matrix used at compute time. If the runtime
    /// instance's transform doesn't match, the entry is stale (world-space
    /// data would be wrong) and we return false, forcing recompute.
    public static class MeshAnalysisDiskCache
    {
        private static readonly JsonSerializerSettings _settings = new()
        {
            TypeNameHandling = TypeNameHandling.Auto,    // serialise abstract MeshRegion subtype info
            Formatting = Formatting.None,
            Converters = { new Vector3Converter(), new Matrix4x4Converter(), new BoundsConverter() },
            // Default contract resolver picks up our public fields fine; no
            // ContractResolver override needed.
        };

        public static bool TryLoad(string meshKey, string paramsHash, Matrix4x4 expectedXf,
            out AnalysisResult result)
        {
            result = null;
            var path = PathFor(meshKey, paramsHash);
            if (!File.Exists(path)) return false;

            try
            {
                var json = File.ReadAllText(path);
                result = JsonConvert.DeserializeObject<AnalysisResult>(json, _settings);
                if (result == null) return false;
                // Reject stale entries — the runtime caller has a different
                // transform and the cached world-space data would mislead.
                if (!Approximately(result.OwnerLocalToWorld, expectedXf))
                {
                    result = null;
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[mesh-analytics] disk cache read failed for {path}: {e.Message}");
                result = null;
                return false;
            }
        }

        public static void Save(string meshKey, string paramsHash, AnalysisResult result)
        {
            if (result == null) return;
            try
            {
                var path = PathFor(meshKey, paramsHash);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(result, _settings));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[mesh-analytics] disk cache write failed: {e.Message}");
            }
        }

        /// Wipes every entry on disk for the given meshKey across all
        /// param-hashes. Called by the AssetPostprocessor on re-import so
        /// stale entries from a previous version of the asset don't
        /// pollute fresh computations.
        public static void InvalidateMesh(string meshKey)
        {
            try
            {
                var dir = MeshDir(meshKey);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[mesh-analytics] disk cache invalidate failed for {meshKey}: {e.Message}");
            }
        }

        public static string CacheRoot
        {
            get
            {
                var lib = Path.Combine(UnityEngine.Application.dataPath, "..", "Library", "MeshAnalytics");
                return Path.GetFullPath(lib);
            }
        }

        private static string MeshDir(string meshKey)
            => Path.Combine(CacheRoot, StableName(meshKey));

        private static string PathFor(string analysisKey, string paramsHash)
        {
            string meshKey = MeshKeyFromAnalysisKey(analysisKey);
            return Path.Combine(MeshDir(meshKey), StableName(analysisKey), StableName(paramsHash) + ".json");
        }

        private static string MeshKeyFromAnalysisKey(string analysisKey)
        {
            if (string.IsNullOrEmpty(analysisKey)) return "null";
            int ownerSeparator = analysisKey.IndexOf('@');
            return ownerSeparator >= 0 ? analysisKey.Substring(0, ownerSeparator) : analysisKey;
        }

        private static string StableName(string value)
        {
            value = value ?? string.Empty;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(32);
            for (int i = 0; i < 16; i++) builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }

        private static bool Approximately(Matrix4x4 a, Matrix4x4 b)
        {
            // 1e-4 tolerance — handles float rounding from prefab placement
            // while still catching real moves (a 1mm shift is well above).
            const float eps = 1e-4f;
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > eps) return false;
            return true;
        }

        // ---- Newtonsoft converters for Unity types ----

        private sealed class Vector3Converter : JsonConverter<Vector3>
        {
            public override void WriteJson(JsonWriter w, Vector3 v, JsonSerializer s)
            {
                w.WriteStartArray(); w.WriteValue(v.x); w.WriteValue(v.y); w.WriteValue(v.z); w.WriteEndArray();
            }
            public override Vector3 ReadJson(JsonReader r, Type _, Vector3 _2, bool _3, JsonSerializer _4)
            {
                if (r.TokenType != JsonToken.StartArray) return Vector3.zero;
                float x = ReadFloat(r), y = ReadFloat(r), z = ReadFloat(r);
                while (r.TokenType != JsonToken.EndArray) r.Read();
                return new Vector3(x, y, z);
            }
            private static float ReadFloat(JsonReader r) { r.Read(); return Convert.ToSingle(r.Value); }
        }

        private sealed class Matrix4x4Converter : JsonConverter<Matrix4x4>
        {
            public override void WriteJson(JsonWriter w, Matrix4x4 m, JsonSerializer s)
            {
                w.WriteStartArray();
                for (int i = 0; i < 16; i++) w.WriteValue(m[i]);
                w.WriteEndArray();
            }
            public override Matrix4x4 ReadJson(JsonReader r, Type _, Matrix4x4 _2, bool _3, JsonSerializer _4)
            {
                var m = new Matrix4x4();
                if (r.TokenType != JsonToken.StartArray) return Matrix4x4.identity;
                for (int i = 0; i < 16; i++) { r.Read(); m[i] = Convert.ToSingle(r.Value); }
                while (r.TokenType != JsonToken.EndArray) r.Read();
                return m;
            }
        }

        private sealed class BoundsConverter : JsonConverter<Bounds>
        {
            public override void WriteJson(JsonWriter w, Bounds b, JsonSerializer s)
            {
                w.WriteStartArray();
                w.WriteValue(b.center.x); w.WriteValue(b.center.y); w.WriteValue(b.center.z);
                w.WriteValue(b.size.x); w.WriteValue(b.size.y); w.WriteValue(b.size.z);
                w.WriteEndArray();
            }
            public override Bounds ReadJson(JsonReader r, Type _, Bounds _2, bool _3, JsonSerializer _4)
            {
                if (r.TokenType != JsonToken.StartArray) return new Bounds();
                float cx = F(r), cy = F(r), cz = F(r), sx = F(r), sy = F(r), sz = F(r);
                while (r.TokenType != JsonToken.EndArray) r.Read();
                return new Bounds(new Vector3(cx, cy, cz), new Vector3(sx, sy, sz));
            }
            private static float F(JsonReader r) { r.Read(); return Convert.ToSingle(r.Value); }
        }
    }
}
