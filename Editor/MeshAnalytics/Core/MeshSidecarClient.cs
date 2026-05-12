using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    /// HTTP client for the Python mesh-analytics sidecar
    /// (`Sidecar~/main.py`). Speaks /health, /methods, /segment.
    /// Serialises a Mesh to a Wavefront OBJ string and parses the returned
    /// regions into structures the C# tool layer can echo to the LLM.
    public static class MeshSidecarClient
    {
        public const string DefaultUrl = "http://127.0.0.1:8766";

        // ---- request / response shapes ----

        public sealed class SegmentParams
        {
            public string Method = "semantic";
            public int MinFaceCount = 16;
            public int NormalBuckets = 8;
            public float RansacAngleToleranceDeg = 5f;
            public float RansacDistanceTolerance = -1f;
            public int RansacMaxPlanes = 16;
            public float RegionGrowingFrontierDeg = 15f;
        }

        public sealed class SegmentRegion
        {
            public string RegionId;
            public int[] FaceIndices;
            public int FaceCount;
            public Vector3 AvgNormal;
            public string Classification;
            public float Confidence;
            public Bounds? WorldBounds;
        }

        public sealed class SegmentResult
        {
            public string Method;
            public List<SegmentRegion> Regions;
            public JObject Info;
            public float ElapsedMs;
            public string Error;
            public string ErrorCode;       // "SIDECAR_UNREACHABLE" / "SIDECAR_HTTP_<status>" / "PARSE_FAILED" / null on success
        }

        public sealed class HealthResult
        {
            public bool Ok;
            public string Service;
            public string Version;
            public string Error;
        }

        public sealed class MethodInfo
        {
            public string Name;
            public string Description;
            public bool ProducesLabels;
        }

        // ---- /health ----

        public static HealthResult Health(string sidecarUrl, int timeoutSeconds = 5)
        {
            if (string.IsNullOrEmpty(sidecarUrl)) sidecarUrl = DefaultUrl;
            using var req = UnityWebRequest.Get(sidecarUrl.TrimEnd('/') + "/health");
            req.timeout = Mathf.Clamp(timeoutSeconds, 1, 60);
            var op = req.SendWebRequest();
            while (!op.isDone) { /* spin — Editor main thread */ }
            if (req.result != UnityWebRequest.Result.Success)
                return new HealthResult { Ok = false, Error = $"{req.responseCode}: {req.error}" };
            try
            {
                var j = JObject.Parse(req.downloadHandler.text);
                return new HealthResult
                {
                    Ok = (string)j["status"] == "ok",
                    Service = (string)j["service"],
                    Version = (string)j["version"],
                };
            }
            catch (Exception e)
            {
                return new HealthResult { Ok = false, Error = $"parse failed: {e.Message}" };
            }
        }

        // ---- /methods ----

        public static List<MethodInfo> ListMethods(string sidecarUrl, int timeoutSeconds = 5)
        {
            if (string.IsNullOrEmpty(sidecarUrl)) sidecarUrl = DefaultUrl;
            using var req = UnityWebRequest.Get(sidecarUrl.TrimEnd('/') + "/methods");
            req.timeout = Mathf.Clamp(timeoutSeconds, 1, 60);
            var op = req.SendWebRequest();
            while (!op.isDone) { /* spin */ }
            if (req.result != UnityWebRequest.Result.Success) return null;
            try
            {
                var arr = JArray.Parse(req.downloadHandler.text);
                var list = new List<MethodInfo>();
                foreach (var item in arr)
                {
                    list.Add(new MethodInfo
                    {
                        Name = (string)item["name"],
                        Description = (string)item["description"],
                        ProducesLabels = (bool?)item["produces_labels"] ?? false,
                    });
                }
                return list;
            }
            catch { return null; }
        }

        // ---- /segment ----

        public static SegmentResult Segment(Mesh mesh, Matrix4x4 localToWorld,
            string sidecarUrl, SegmentParams p, int timeoutSeconds)
        {
            if (mesh == null) return Fail("BAD_ARGS", "mesh is null");
            if (p == null) p = new SegmentParams();
            if (string.IsNullOrEmpty(sidecarUrl)) sidecarUrl = DefaultUrl;

            string obj;
            try { obj = MeshToObj(mesh, localToWorld); }
            catch (Exception e) { return Fail("OBJ_SERIALISE_FAILED", e.Message); }

            var body = new JObject
            {
                ["mesh_obj"] = obj,
                ["method"] = p.Method,
                ["min_face_count"] = p.MinFaceCount,
                ["normal_buckets"] = p.NormalBuckets,
                ["ransac_angle_tolerance_deg"] = p.RansacAngleToleranceDeg,
                ["ransac_distance_tolerance"] = p.RansacDistanceTolerance,
                ["ransac_max_planes"] = p.RansacMaxPlanes,
                ["region_growing_frontier_deg"] = p.RegionGrowingFrontierDeg,
            };
            var bodyBytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));

            using var req = new UnityWebRequest(sidecarUrl.TrimEnd('/') + "/segment", "POST");
            req.uploadHandler = new UploadHandlerRaw(bodyBytes) { contentType = "application/json" };
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = Mathf.Clamp(timeoutSeconds, 1, 600);

            var op = req.SendWebRequest();
            while (!op.isDone) { /* spin */ }

            if (req.result == UnityWebRequest.Result.ConnectionError)
            {
                return Fail("SIDECAR_UNREACHABLE",
                    $"connection failed to {sidecarUrl}: {req.error}. " +
                    $"Start the sidecar with `uvicorn main:app --port 8766` from " +
                    $"extensions/unity-scene-analyzer/Sidecar~/. See README.md there.");
            }
            if (req.result != UnityWebRequest.Result.Success)
            {
                string snippet = Truncate(req.downloadHandler?.text, 300);
                return Fail($"SIDECAR_HTTP_{req.responseCode}",
                    $"{req.error}. Body: {snippet}");
            }

            try
            {
                var json = JObject.Parse(req.downloadHandler.text);
                var regions = new List<SegmentRegion>();
                foreach (var r in (JArray)json["regions"])
                {
                    var faceArr = (JArray)r["face_indices"];
                    var faces = new int[faceArr.Count];
                    for (int i = 0; i < faceArr.Count; i++) faces[i] = (int)faceArr[i];
                    var n = (JArray)r["avg_normal"];
                    Bounds? bounds = null;
                    if (r["world_bounds"] is JObject wb)
                    {
                        var mn = (JArray)wb["min"];
                        var mx = (JArray)wb["max"];
                        var center = new Vector3(
                            ((float)mn[0] + (float)mx[0]) * 0.5f,
                            ((float)mn[1] + (float)mx[1]) * 0.5f,
                            ((float)mn[2] + (float)mx[2]) * 0.5f);
                        var size = new Vector3(
                            (float)mx[0] - (float)mn[0],
                            (float)mx[1] - (float)mn[1],
                            (float)mx[2] - (float)mn[2]);
                        bounds = new Bounds(center, size);
                    }
                    regions.Add(new SegmentRegion
                    {
                        RegionId = (string)r["region_id"],
                        FaceIndices = faces,
                        FaceCount = (int)r["face_count"],
                        AvgNormal = new Vector3((float)n[0], (float)n[1], (float)n[2]),
                        Classification = (string)r["classification"] ?? "unknown",
                        Confidence = (float?)r["confidence"] ?? 1f,
                        WorldBounds = bounds,
                    });
                }
                return new SegmentResult
                {
                    Method = (string)json["method"],
                    Regions = regions,
                    Info = (JObject)json["info"],
                    ElapsedMs = (float?)json["elapsed_ms"] ?? 0f,
                };
            }
            catch (Exception e)
            {
                return Fail("PARSE_FAILED", $"{e.Message}. Body: {Truncate(req.downloadHandler.text, 300)}");
            }
        }

        // ---- serialisation ----

        /// Minimal Wavefront OBJ writer. Vertex positions in world space
        /// (`localToWorld` applied) + face indices. Triangle indices are
        /// preserved 1:1, so `face_indices` round-tripped from the sidecar
        /// map back to `mesh.triangles[3*i .. 3*i+2]` on this side.
        public static string MeshToObj(Mesh mesh, Matrix4x4 localToWorld)
        {
            var sb = new StringBuilder();
            sb.Append("# scene-analyzer mesh-analytics sidecar payload\n");
            var verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                var v = localToWorld.MultiplyPoint3x4(verts[i]);
                sb.Append("v ").Append(v.x.ToString("R")).Append(' ')
                  .Append(v.y.ToString("R")).Append(' ')
                  .Append(v.z.ToString("R")).Append('\n');
            }
            var tris = mesh.triangles;
            int triCount = tris.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int a = tris[3 * t + 0] + 1;
                int b = tris[3 * t + 1] + 1;
                int c = tris[3 * t + 2] + 1;
                sb.Append("f ").Append(a).Append(' ').Append(b).Append(' ').Append(c).Append('\n');
            }
            return sb.ToString();
        }

        // ---- internals ----

        private static SegmentResult Fail(string code, string message)
            => new SegmentResult { Error = message, ErrorCode = code };

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }
}
