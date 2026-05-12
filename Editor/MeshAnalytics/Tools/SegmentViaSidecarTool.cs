using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Off-loads mesh segmentation to the Python sidecar service. The
    /// default `method=semantic` runs RANSAC plane segmentation followed
    /// by relational geometric labelling — every returned region carries
    /// a class name like `ground`, `wall_090`, `door_candidate`,
    /// `sidewalk_top`, etc. Other methods (`ransac_planes`,
    /// `region_growing`, `normal_cluster`) expose the underlying
    /// algorithms without labelling for cases where the user wants raw
    /// geometry instead.
    ///
    /// Requires `Sidecar~/main.py` to be running (uvicorn). The tool emits
    /// a clear `SIDECAR_UNREACHABLE` message with start-up instructions
    /// when the service is down, so the LLM can guide the user.
    public sealed class SegmentViaSidecarTool : IMcpTool
    {
        public string Name => "mesh_segment_via_sidecar";
        public string Description =>
            "Segments a mesh by calling the Python sidecar service. Default method 'semantic' returns face groupings labelled ground/road/sidewalk_top/wall_<bearing>/door_candidate/window_candidate/ceiling/step/curb/roof_top/platform via RANSAC + heuristics. Alternative methods: 'ransac_planes' (raw planes), 'region_growing' (smooth patches separated by sharp edges), 'normal_cluster' (cheap baseline). Sidecar must be running at the configured URL (default 127.0.0.1:8766); see Sidecar~/README.md to start it.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["sidecarUrl"] = new JObject { ["type"] = "string", ["default"] = MeshSidecarClient.DefaultUrl },
                ["method"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "semantic", "ransac_planes", "region_growing", "normal_cluster" },
                    ["default"] = "semantic",
                },
                ["minFaceCount"] = new JObject { ["type"] = "integer", ["default"] = 16, ["minimum"] = 1 },
                ["normalBuckets"] = new JObject { ["type"] = "integer", ["default"] = 8, ["minimum"] = 2, ["maximum"] = 64, ["description"] = "normal_cluster only" },
                ["ransacAngleToleranceDeg"] = new JObject { ["type"] = "number", ["default"] = 5.0, ["minimum"] = 0.1, ["maximum"] = 45.0 },
                ["ransacDistanceTolerance"] = new JObject { ["type"] = "number", ["default"] = -1.0, ["description"] = "Metres. -1 = auto (1% of bounds diag)." },
                ["ransacMaxPlanes"] = new JObject { ["type"] = "integer", ["default"] = 16, ["minimum"] = 1, ["maximum"] = 64 },
                ["regionGrowingFrontierDeg"] = new JObject { ["type"] = "number", ["default"] = 15.0, ["minimum"] = 1.0, ["maximum"] = 89.0 },
                ["timeoutSeconds"] = new JObject { ["type"] = "integer", ["default"] = 60, ["minimum"] = 1, ["maximum"] = 600 },
                ["maxFaceIndicesInResponse"] = new JObject { ["type"] = "integer", ["default"] = 0, ["minimum"] = 0, ["maximum"] = 50000 },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            int instanceId = (int?)args["instanceId"] ?? 0;
            if (!MeshResolver.TryGetMeshAndTransforms(instanceId, out var go, out var mesh, out var ltw))
                return ToolResult.Error("NOT_FOUND",
                    $"no MeshFilter/SkinnedMeshRenderer mesh on GameObject {instanceId}");
            if (!MeshResolver.IsReadable(mesh, out var notReadable))
                return ToolResult.Error("MESH_NOT_READABLE", notReadable);

            var p = new MeshSidecarClient.SegmentParams
            {
                Method = (string)args["method"] ?? "semantic",
                MinFaceCount = (int?)args["minFaceCount"] ?? 16,
                NormalBuckets = (int?)args["normalBuckets"] ?? 8,
                RansacAngleToleranceDeg = (float?)args["ransacAngleToleranceDeg"] ?? 5f,
                RansacDistanceTolerance = (float?)args["ransacDistanceTolerance"] ?? -1f,
                RansacMaxPlanes = (int?)args["ransacMaxPlanes"] ?? 16,
                RegionGrowingFrontierDeg = (float?)args["regionGrowingFrontierDeg"] ?? 15f,
            };
            string url = (string)args["sidecarUrl"] ?? MeshSidecarClient.DefaultUrl;
            int timeout = (int?)args["timeoutSeconds"] ?? 60;
            int faceCap = (int?)args["maxFaceIndicesInResponse"] ?? 0;

            var result = MeshSidecarClient.Segment(mesh, ltw, url, p, timeout);
            if (!string.IsNullOrEmpty(result.Error))
                return ToolResult.Error(result.ErrorCode ?? "SIDECAR_ERROR", result.Error);

            // Translate sidecar regions into the in-memory cache so they
            // can be visualised with render_region_highlight and sampled
            // with mesh_sample_points_on_region. We use PlaneRegion as the
            // canonical container because it carries Classification +
            // OrientationLabel + NormalWorld + PointWorld + WorldBounds —
            // exactly what the sidecar emits.
            string meshKey = MeshResolver.MakeMeshKey(mesh);
            string paramsHash = $"sidecar:{p.Method}:{p.MinFaceCount}:{p.RansacAngleToleranceDeg:F2}:{p.RansacDistanceTolerance:F4}:{p.RansacMaxPlanes}:{p.RegionGrowingFrontierDeg:F2}:{p.NormalBuckets}";
            var bundle = new AnalysisResult
            {
                MeshGuid = meshKey,
                OwnerInstanceId = instanceId,
                OwnerLocalToWorld = ltw,
            };

            for (int i = 0; i < result.Regions.Count; i++)
            {
                var src = result.Regions[i];
                var bb = src.WorldBounds ?? new Bounds();
                bundle.Planes.Add(new PlaneRegion
                {
                    Kind = RegionKind.Plane,
                    OwnerInstanceId = instanceId,
                    RegionId = MeshAnalysisCache.MakeRegionId(meshKey, paramsHash, RegionKind.Plane, i),
                    FaceIndices = src.FaceIndices,
                    FaceCount = src.FaceCount,
                    NormalWorld = src.AvgNormal,
                    PointWorld = bb.center,
                    AreaApprox = 0f,                                // sidecar doesn't report it; LLM can derive from faceCount
                    WorldBounds = bb,
                    OrientationLabel = OrientationLabelFromNormal(src.AvgNormal),
                    Classification = src.Classification,
                });
            }
            MeshAnalysisCache.StoreAndPersist(meshKey, paramsHash, bundle);

            var arr = new JArray();
            foreach (var r in bundle.Planes)
            {
                var entry = new JObject
                {
                    ["regionId"] = r.RegionId,
                    ["faceCount"] = r.FaceCount,
                    ["classification"] = r.Classification,
                    ["orientationLabel"] = r.OrientationLabel,
                    ["normal"] = new JArray { r.NormalWorld.x, r.NormalWorld.y, r.NormalWorld.z },
                    ["center"] = new JArray { r.PointWorld.x, r.PointWorld.y, r.PointWorld.z },
                    ["worldBounds"] = BoundsJson(r.WorldBounds),
                };
                if (faceCap > 0 && r.FaceIndices != null)
                {
                    var faces = new JArray();
                    int nFaces = Mathf.Min(r.FaceIndices.Length, faceCap);
                    for (int k = 0; k < nFaces; k++) faces.Add(r.FaceIndices[k]);
                    entry["faceIndices"] = faces;
                    entry["faceIndicesTruncated"] = r.FaceIndices.Length > nFaces;
                }
                arr.Add(entry);
            }
            var payload = new JObject
            {
                ["meshName"] = mesh.name,
                ["method"] = result.Method,
                ["regionCount"] = bundle.Planes.Count,
                ["elapsedMs"] = result.ElapsedMs,
                ["regions"] = arr,
                ["sidecarInfo"] = result.Info,
            };
            return ToolResult.Ok(
                $"sidecar '{result.Method}' returned {bundle.Planes.Count} region(s) on '{go.name}' in {result.ElapsedMs:F0}ms",
                payload);
        }

        // Mirrors PlaneDetector.ClassifyOrientation so highlights and
        // downstream tools see the same vocabulary regardless of whether
        // a plane came from the in-process detector or the sidecar.
        private static string OrientationLabelFromNormal(Vector3 worldNormal)
        {
            const float horizThresholdDeg = 30f;
            float cosHoriz = Mathf.Cos(horizThresholdDeg * Mathf.Deg2Rad);
            float dotUp = Vector3.Dot(worldNormal.normalized, Vector3.up);
            if (dotUp > cosHoriz) return "horizontal_up";
            if (dotUp < -cosHoriz) return "horizontal_down";
            float bearing = Mathf.Atan2(worldNormal.z, worldNormal.x) * Mathf.Rad2Deg;
            if (bearing < 0) bearing += 360f;
            return $"vertical:{Mathf.RoundToInt(bearing)}";
        }

        private static JObject BoundsJson(Bounds b) => new JObject
        {
            ["center"] = new JArray { b.center.x, b.center.y, b.center.z },
            ["size"] = new JArray { b.size.x, b.size.y, b.size.z },
        };
    }
}
