using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core
{
    public static class MeshSidecarClient
    {
        public const string DefaultUrl = "http://127.0.0.1:8766";
        public const int MaxVertices = 500_000;
        public const int MaxFaces = 1_000_000;
        public const int MaxRequestBytes = 64 * 1024 * 1024;
        public const int MaxResponseBytes = 64 * 1024 * 1024;

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
            public string ErrorCode;
        }

        public sealed class HealthResult
        {
            public bool Ok;
            public string Service;
            public string Version;
            public string Error;
            public string ErrorCode;
        }

        public sealed class MethodInfo
        {
            public string Name;
            public string Description;
            public bool ProducesLabels;
        }

        private sealed class HttpTextResult
        {
            public long StatusCode;
            public string Text;
            public string Error;
            public UnityWebRequest.Result Result;
        }

        public static async Task<HealthResult> HealthAsync(
            string sidecarUrl, int timeoutSeconds = 5, CancellationToken cancellationToken = default)
        {
            if (!TryNormalizeUrl(sidecarUrl, out var baseUrl, out var urlError))
                return new HealthResult { ErrorCode = "SIDECAR_URL_REJECTED", Error = urlError };
            var response = await SendTextAsync(
                () => UnityWebRequest.Get(baseUrl + "/health"), timeoutSeconds, cancellationToken);
            if (response.Result != UnityWebRequest.Result.Success)
                return new HealthResult
                {
                    ErrorCode = response.Result == UnityWebRequest.Result.ConnectionError
                        ? "SIDECAR_UNREACHABLE"
                        : $"SIDECAR_HTTP_{response.StatusCode}",
                    Error = $"{response.StatusCode}: {response.Error}. Body: {Truncate(response.Text, 300)}",
                };
            try
            {
                var json = JObject.Parse(response.Text);
                return new HealthResult
                {
                    Ok = string.Equals((string)json["status"], "ok", StringComparison.Ordinal),
                    Service = (string)json["service"],
                    Version = (string)json["version"],
                };
            }
            catch (Exception exception)
            {
                return new HealthResult { ErrorCode = "PARSE_FAILED", Error = exception.Message };
            }
        }

        public static async Task<List<MethodInfo>> ListMethodsAsync(
            string sidecarUrl, int timeoutSeconds = 5, CancellationToken cancellationToken = default)
        {
            if (!TryNormalizeUrl(sidecarUrl, out var baseUrl, out var urlError))
                throw new InvalidOperationException(urlError);
            var response = await SendTextAsync(
                () => UnityWebRequest.Get(baseUrl + "/methods"), timeoutSeconds, cancellationToken);
            if (response.Result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(
                    $"sidecar methods request failed ({response.StatusCode}): {response.Error}");

            var array = JArray.Parse(response.Text);
            var methods = new List<MethodInfo>(array.Count);
            foreach (var item in array)
            {
                methods.Add(new MethodInfo
                {
                    Name = (string)item["name"],
                    Description = (string)item["description"],
                    ProducesLabels = (bool?)item["produces_labels"] ?? false,
                });
            }
            return methods;
        }

        public static async Task<SegmentResult> SegmentAsync(
            string meshObj, string sidecarUrl, SegmentParams parameters, int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(meshObj)) return Fail("BAD_ARGS", "mesh OBJ payload is empty");
            if (!TryNormalizeUrl(sidecarUrl, out var baseUrl, out var urlError))
                return Fail("SIDECAR_URL_REJECTED", urlError);
            parameters = parameters ?? new SegmentParams();

            var body = new JObject
            {
                ["mesh_obj"] = meshObj,
                ["method"] = parameters.Method,
                ["min_face_count"] = parameters.MinFaceCount,
                ["normal_buckets"] = parameters.NormalBuckets,
                ["ransac_angle_tolerance_deg"] = parameters.RansacAngleToleranceDeg,
                ["ransac_distance_tolerance"] = parameters.RansacDistanceTolerance,
                ["ransac_max_planes"] = parameters.RansacMaxPlanes,
                ["region_growing_frontier_deg"] = parameters.RegionGrowingFrontierDeg,
            };
            var bodyBytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            if (bodyBytes.Length > MaxRequestBytes)
                return Fail("SIDECAR_REQUEST_TOO_LARGE",
                    $"sidecar request is {bodyBytes.Length} bytes; limit is {MaxRequestBytes}");

            var response = await SendTextAsync(() =>
            {
                var request = new UnityWebRequest(baseUrl + "/segment", "POST")
                {
                    uploadHandler = new UploadHandlerRaw(bodyBytes) { contentType = "application/json" },
                    downloadHandler = new DownloadHandlerBuffer(),
                };
                request.SetRequestHeader("Accept", "application/json");
                return request;
            }, timeoutSeconds, cancellationToken);

            if (response.Result == UnityWebRequest.Result.ConnectionError)
                return Fail("SIDECAR_UNREACHABLE",
                    $"connection failed to {baseUrl}: {response.Error}. Start `uvicorn main:app --host 127.0.0.1 --port 8766` from Sidecar~/.");
            if (response.Result != UnityWebRequest.Result.Success)
                return Fail($"SIDECAR_HTTP_{response.StatusCode}",
                    $"{response.Error}. Body: {Truncate(response.Text, 300)}");

            try { return ParseSegment(response.Text); }
            catch (Exception exception)
            {
                return Fail("PARSE_FAILED", $"{exception.Message}. Body: {Truncate(response.Text, 300)}");
            }
        }

        public static string MeshToObj(Mesh mesh, Matrix4x4 localToWorld)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            if (vertices.Length > MaxVertices)
                throw new InvalidOperationException($"mesh has {vertices.Length} vertices; sidecar limit is {MaxVertices}");
            if (triangles.Length / 3 > MaxFaces)
                throw new InvalidOperationException($"mesh has {triangles.Length / 3} faces; sidecar limit is {MaxFaces}");

            var invariant = CultureInfo.InvariantCulture;
            var builder = new StringBuilder(Math.Min(MaxRequestBytes, vertices.Length * 40 + triangles.Length * 8));
            builder.Append("# unity-mcp mesh sidecar payload\n");
            for (int i = 0; i < vertices.Length; i++)
            {
                var vertex = localToWorld.MultiplyPoint3x4(vertices[i]);
                builder.Append("v ").Append(vertex.x.ToString("R", invariant)).Append(' ')
                    .Append(vertex.y.ToString("R", invariant)).Append(' ')
                    .Append(vertex.z.ToString("R", invariant)).Append('\n');
                if (builder.Length > MaxRequestBytes)
                    throw new InvalidOperationException($"OBJ payload exceeds {MaxRequestBytes} characters");
            }
            for (int face = 0; face < triangles.Length / 3; face++)
            {
                builder.Append("f ").Append(triangles[face * 3] + 1).Append(' ')
                    .Append(triangles[face * 3 + 1] + 1).Append(' ')
                    .Append(triangles[face * 3 + 2] + 1).Append('\n');
                if (builder.Length > MaxRequestBytes)
                    throw new InvalidOperationException($"OBJ payload exceeds {MaxRequestBytes} characters");
            }
            return builder.ToString();
        }

        public static bool TryNormalizeUrl(string sidecarUrl, out string baseUrl, out string error)
        {
            baseUrl = null;
            error = null;
            if (string.IsNullOrWhiteSpace(sidecarUrl)) sidecarUrl = DefaultUrl;
            if (!Uri.TryCreate(sidecarUrl, UriKind.Absolute, out var uri))
            {
                error = "sidecarUrl must be an absolute URL";
                return false;
            }
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                error = "sidecarUrl must use http on a loopback address";
                return false;
            }
            if (!uri.IsLoopback && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                && !IPAddress.TryParse(uri.Host, out var address))
            {
                error = "sidecarUrl must target localhost/loopback";
                return false;
            }
            if (IPAddress.TryParse(uri.Host, out var parsed) && !IPAddress.IsLoopback(parsed))
            {
                error = "sidecarUrl must target localhost/loopback";
                return false;
            }
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "sidecarUrl must not contain credentials, query, or fragment";
                return false;
            }
            baseUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return true;
        }

        private static async Task<HttpTextResult> SendTextAsync(
            Func<UnityWebRequest> requestFactory, int timeoutSeconds, CancellationToken cancellationToken)
        {
            UnityWebRequest request = null;
            try
            {
                request = await MainThreadDispatcher.RunAsync(() =>
                {
                    var created = requestFactory();
                    created.timeout = Mathf.Clamp(timeoutSeconds, 1, 600);
                    return created;
                }, cancellationToken);

                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                UnityWebRequestAsyncOperation operation = null;
                await MainThreadDispatcher.RunAsync(() =>
                {
                    operation = request.SendWebRequest();
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

                return await MainThreadDispatcher.RunAsync(() =>
                {
                    var bytes = request.downloadHandler?.data;
                    if (bytes != null && bytes.Length > MaxResponseBytes)
                        throw new InvalidOperationException(
                            $"sidecar response is {bytes.Length} bytes; limit is {MaxResponseBytes}");
                    return new HttpTextResult
                    {
                        StatusCode = request.responseCode,
                        Text = request.downloadHandler?.text ?? string.Empty,
                        Error = request.error,
                        Result = request.result,
                    };
                }, cancellationToken);
            }
            finally
            {
                if (request != null)
                {
                    try { await MainThreadDispatcher.RunAsync(request.Dispose); }
                    catch { request.Dispose(); }
                }
            }
        }

        private static SegmentResult ParseSegment(string text)
        {
            var json = JObject.Parse(text);
            var regions = new List<SegmentRegion>();
            foreach (var token in json["regions"] as JArray ?? new JArray())
            {
                var facesJson = token["face_indices"] as JArray ?? new JArray();
                var faces = new int[facesJson.Count];
                for (int i = 0; i < faces.Length; i++) faces[i] = (int)facesJson[i];
                var normal = token["avg_normal"] as JArray ?? throw new InvalidOperationException("region avg_normal is missing");
                Bounds? bounds = null;
                if (token["world_bounds"] is JObject boundsJson)
                {
                    var min = boundsJson["min"] as JArray;
                    var max = boundsJson["max"] as JArray;
                    if (min != null && max != null && min.Count >= 3 && max.Count >= 3)
                    {
                        var minimum = new Vector3((float)min[0], (float)min[1], (float)min[2]);
                        var maximum = new Vector3((float)max[0], (float)max[1], (float)max[2]);
                        bounds = new Bounds((minimum + maximum) * 0.5f, maximum - minimum);
                    }
                }
                regions.Add(new SegmentRegion
                {
                    RegionId = (string)token["region_id"],
                    FaceIndices = faces,
                    FaceCount = (int?)token["face_count"] ?? faces.Length,
                    AvgNormal = new Vector3((float)normal[0], (float)normal[1], (float)normal[2]),
                    Classification = (string)token["classification"] ?? "unknown",
                    Confidence = (float?)token["confidence"] ?? 1f,
                    WorldBounds = bounds,
                });
            }
            return new SegmentResult
            {
                Method = (string)json["method"],
                Regions = regions,
                Info = json["info"] as JObject ?? new JObject(),
                ElapsedMs = (float?)json["elapsed_ms"] ?? 0f,
            };
        }

        private static SegmentResult Fail(string code, string message)
            => new SegmentResult { Error = message, ErrorCode = code, Regions = new List<SegmentRegion>() };

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= length ? value : value.Substring(0, length) + "...";
        }
    }
}
