using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    internal static class MeshToolSchemas
    {
        public static JObject Target => SchemaHelpers.TargetRefSchema;

        public static JArray TargetOrInstance => new JArray
        {
            RequiredShape("target", Target),
            RequiredShape("instanceId", Integer()),
        };

        public static JArray TargetInstanceOrRegionIds => new JArray
        {
            RequiredShape("target", Target),
            RequiredShape("instanceId", Integer()),
            RequiredShape("chainRegionIds", Array(String(), 1)),
        };

        public static JArray TargetInstanceOrAssetPath => new JArray
        {
            RequiredShape("target", Target),
            RequiredShape("instanceId", Integer()),
            RequiredShape("assetPath", String()),
        };

        public static JObject Vec3 => Array(Number(), 3, 3);
        public static JObject Matrix4 => Array(Number(), 16, 16);
        public static JObject Bounds => Object(
            ("center", Vec3),
            ("size", Vec3));

        public static JObject SubmeshesOutput => Object(
            ("meshName", String()),
            ("submeshCount", Integer()),
            ("submeshes", Array(Object(
                ("regionId", String()),
                ("submeshIndex", Integer()),
                ("materialName", NullableString()),
                ("triangleCount", Integer()),
                ("topology", String()),
                ("localBounds", Bounds),
                ("worldBounds", Bounds)))));

        public static JObject SharpEdgesOutput => Object(
            ("meshName", String()),
            ("chainCount", Integer()),
            ("dihedralAngleMin", Number()),
            ("chains", Array(Object(
                ("regionId", String()),
                ("totalLength", Number()),
                ("isLoop", Boolean()),
                ("vertexCount", Integer()),
                ("adjacentFaceCount", Integer()),
                ("worldBounds", Bounds),
                ("vertexPositionsWorld", Array(Vec3)),
                ("vertexPositionsTruncated", Boolean())))));

        public static JObject PlanesOutput => Object(
            ("meshName", String()),
            ("planeCount", Integer()),
            ("planes", Array(PlaneRegion())));

        public static JObject SmoothRegionsOutput => Object(
            ("meshName", String()),
            ("regionCount", Integer()),
            ("regions", Array(Object(
                ("regionId", String()),
                ("avgNormal", Vec3),
                ("areaApprox", Number()),
                ("faceCount", Integer()),
                ("roughness", Number()),
                ("orientationLabel", String()),
                ("worldBounds", Bounds),
                ("faceIndices", Array(Integer())),
                ("faceIndicesTruncated", Boolean())))));

        public static JObject ParallelGroupsOutput => Object(
            ("sourceChainCount", Integer()),
            ("groupCount", Integer()),
            ("groups", Array(Object(
                ("regionId", String()),
                ("direction", Vec3),
                ("meanSpacing", Number()),
                ("spacingStdDev", Number()),
                ("instanceCount", Integer()),
                ("childChainRegionIds", Array(String())),
                ("worldBounds", Bounds)))));

        public static JObject SamplesOutput => Object(
            ("regionId", String()),
            ("sampleCount", Integer()),
            ("distribution", String()),
            ("samples", Array(Object(
                ("position", Vec3),
                ("normal", Vec3)))));

        public static JObject PickOutput => Object(
            ("target", Target),
            ("instanceId", Integer()),
            ("worldPoint", Vec3),
            ("worldNormal", Vec3),
            ("triangleIndex", Integer()),
            ("submeshIndex", Integer()),
            ("distanceFromCamera", Number()),
            ("regionIds", Array(String())),
            ("rayOrigin", Vec3),
            ("rayDirection", Vec3));

        public static JObject EnableReadWriteOutput => Object(
            ("assetPath", String()),
            ("wasEnabled", Boolean()),
            ("isEnabledNow", Boolean()),
            ("reimported", Boolean()),
            ("savedAssets", Boolean()),
            ("warning", NullableString()));

        public static JObject SidecarSegmentOutput => Object(
            ("meshName", String()),
            ("method", String()),
            ("regionCount", Integer()),
            ("elapsedMs", Number()),
            ("regions", Array(PlaneRegion())),
            ("sidecarInfo", new JObject()));

        public static JObject SidecarHealthOutput => Object(
            ("url", String()),
            ("service", String()),
            ("version", String()),
            ("methods", Array(Object(
                ("name", String()),
                ("description", String()),
                ("producesLabels", Boolean())))));

        public static JObject RenderViewsOutput => Object(
            ("target", Target),
            ("width", Integer()),
            ("height", Integer()),
            ("overlay", String()),
            ("isolated", Boolean()),
            ("elapsedMs", Number()),
            ("views", Array(Object(
                ("index", Integer()),
                ("preset", String()),
                ("cameraPos", Vec3),
                ("lookAt", Vec3),
                ("cameraToWorld", Matrix4),
                ("projection", Matrix4),
                ("bytes", Integer())))));

        public static JObject RenderHighlightOutput => Object(
            ("target", Target),
            ("regionId", String()),
            ("regionKind", String()),
            ("view", String()),
            ("width", Integer()),
            ("height", Integer()),
            ("bytes", Integer()));

        private static JObject PlaneRegion() => Object(
            ("regionId", String()),
            ("faceCount", Integer()),
            ("classification", NullableString()),
            ("orientationLabel", NullableString()),
            ("normal", Vec3),
            ("point", Vec3),
            ("center", Vec3),
            ("areaApprox", Number()),
            ("worldBounds", Bounds),
            ("faceIndices", Array(Integer())),
            ("faceIndicesTruncated", Boolean()));

        private static JObject RequiredShape(string name, JObject schema) => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { [name] = schema },
            ["required"] = new JArray { name },
        };

        private static JObject Object(params (string name, JObject schema)[] properties)
        {
            var map = new JObject();
            foreach (var property in properties) map[property.name] = property.schema;
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = map,
                ["additionalProperties"] = false,
            };
        }

        private static JObject Array(JObject items, int? min = null, int? max = null)
        {
            var schema = new JObject { ["type"] = "array", ["items"] = items };
            if (min.HasValue) schema["minItems"] = min.Value;
            if (max.HasValue) schema["maxItems"] = max.Value;
            return schema;
        }

        private static JObject String() => new JObject { ["type"] = "string" };
        private static JObject NullableString() => new JObject { ["type"] = new JArray { "string", "null" } };
        private static JObject Number() => new JObject { ["type"] = "number" };
        private static JObject Integer() => new JObject { ["type"] = "integer" };
        private static JObject Boolean() => new JObject { ["type"] = "boolean" };
    }
}
