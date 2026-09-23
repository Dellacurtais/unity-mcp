using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools
{
    public static class SceneToolSchemas
    {
        public static JObject SceneIdentity => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string" },
                ["path"] = new JObject { ["type"] = "string" },
                ["guid"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["handle"] = new JObject { ["type"] = "integer" },
                ["buildIndex"] = new JObject { ["type"] = "integer" },
                ["isLoaded"] = new JObject { ["type"] = "boolean" },
                ["isDirty"] = new JObject { ["type"] = "boolean" },
                ["isActive"] = new JObject { ["type"] = "boolean" },
                ["rootCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            },
            ["required"] = new JArray { "name", "path", "guid", "handle", "buildIndex", "isLoaded", "isDirty", "isActive", "rootCount" },
            ["additionalProperties"] = false,
        };

        public static JObject ReadHierarchy => Object(
            new JObject
            {
                ["sceneName"] = String(),
                ["scenePath"] = String(),
                ["loadedScenes"] = Array(Object()),
                ["count"] = Integer(0),
                ["scopeCount"] = Integer(0),
                ["matched"] = Integer(0),
                ["offset"] = Integer(0),
                ["returned"] = Integer(0),
                ["hasMore"] = Boolean(),
                ["truncated"] = Boolean(),
                ["depthLimited"] = Boolean(),
                ["snapshotVersion"] = String(),
                ["nextCursor"] = new JObject { ["type"] = new JArray("string", "null") },
                ["nodes"] = Array(HierarchyNode),
            },
            "sceneName", "scenePath", "loadedScenes", "count", "scopeCount", "matched", "offset",
            "returned", "hasMore", "truncated", "depthLimited", "snapshotVersion", "nextCursor", "nodes");

        private static JObject HierarchyNode => Object(new JObject
        {
            ["instanceId"] = Integer(),
            ["name"] = String(),
            ["parentId"] = Integer(),
            ["target"] = SchemaHelpers.TargetRefSchema,
            ["sceneName"] = String(),
            ["scenePath"] = String(),
            ["sceneGuid"] = new JObject { ["type"] = new JArray("string", "null") },
            ["sceneHandle"] = Integer(),
            ["hierarchyPath"] = String(),
            ["globalObjectId"] = new JObject { ["type"] = new JArray("string", "null") },
            ["isPrefabStage"] = Boolean(),
            ["tag"] = String(),
            ["layer"] = Integer(),
            ["isActive"] = Boolean(),
            ["isStatic"] = Boolean(),
            ["staticFlags"] = Integer(),
            ["prefabStatus"] = String(),
            ["prefabPath"] = new JObject { ["type"] = new JArray("string", "null") },
            ["childIds"] = Array(Integer()),
            ["componentTypes"] = Array(String()),
        }, "instanceId", "name", "parentId", "target");

        public static JObject ReadScene => Object(
            new JObject
            {
                ["sceneName"] = String(),
                ["scenePath"] = String(),
                ["loadedScenes"] = Array(Object()),
                ["totalGameObjects"] = Integer(0),
                ["emitted"] = Integer(0),
                ["truncated"] = Boolean(),
                ["roots"] = Array(Object()),
            },
            "sceneName", "scenePath", "loadedScenes", "totalGameObjects", "emitted", "truncated", "roots");

        public static JObject ReadComponent => Object(
            new JObject
            {
                ["instanceId"] = Integer(),
                ["requestedInstanceId"] = Integer(),
                ["gameObjectInstanceId"] = Integer(),
                ["componentInstanceId"] = NullableInteger(),
                ["gameObjectName"] = String(),
                ["components"] = Array(Object()),
            },
            "instanceId", "requestedInstanceId", "gameObjectInstanceId", "componentInstanceId", "gameObjectName", "components");

        public static JObject QueryScene => Object(
            new JObject
            {
                ["matchCount"] = Integer(0),
                ["returnedCount"] = Integer(0),
                ["truncated"] = Boolean(),
                ["sortBy"] = String(),
                ["matches"] = Array(Object()),
            },
            "matchCount", "returnedCount", "truncated", "sortBy", "matches");

        public static JObject ClassifyScene => Object(
            new JObject
            {
                ["totalGameObjects"] = Integer(0),
                ["tags"] = Array(Object()),
                ["layers"] = Array(Object()),
                ["components"] = Array(Object()),
                ["hierarchy"] = Object(),
                ["spatial"] = Object(),
                ["prefabs"] = Object(),
            },
            "totalGameObjects", "tags", "layers", "components", "hierarchy", "spatial", "prefabs");

        public static JObject ListScenes => Object(
            new JObject
            {
                ["activeSceneHandle"] = Integer(),
                ["sceneCount"] = Integer(0),
                ["loadedCount"] = Integer(0),
                ["unloadedCount"] = Integer(0),
                ["scenes"] = Array(SceneIdentity),
                ["loadedScenes"] = Array(SceneIdentity),
                ["unloadedScenes"] = Array(SceneIdentity),
            },
            "activeSceneHandle", "sceneCount", "loadedCount", "unloadedCount", "scenes", "loadedScenes", "unloadedScenes");

        public static JObject CreateScene => Object(
            new JObject
            {
                ["scene"] = SceneIdentity,
                ["saved"] = Boolean(),
                ["nextTool"] = String(),
            },
            "scene", "saved", "nextTool");

        public static JObject SceneResult => Object(
            new JObject { ["scene"] = SceneIdentity },
            "scene");

        public static JObject CloseScene => Object(
            new JObject { ["closedScene"] = SceneIdentity },
            "closedScene");

        public static JObject SaveScene => Object(
            new JObject
            {
                ["scene"] = SceneIdentity,
                ["destinationPath"] = String(),
                ["saveAsCopy"] = Boolean(),
            },
            "scene", "destinationPath", "saveAsCopy");

        public static JObject SaveAllScenes => Object(
            new JObject
            {
                ["savedCount"] = Integer(0),
                ["skippedCount"] = Integer(0),
                ["saved"] = Array(SceneIdentity),
                ["skipped"] = Array(Object()),
            },
            "savedCount", "skippedCount", "saved", "skipped");

        public static JObject ActiveCameras => Object(
            new JObject
            {
                ["count"] = Integer(0),
                ["mainCameraInstanceId"] = NullableInteger(),
                ["cameras"] = Array(Object()),
            },
            "count", "mainCameraInstanceId", "cameras");

        private static JObject Object(JObject properties = null, params string[] required)
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties ?? new JObject(),
            };
            if (required != null && required.Length > 0) schema["required"] = new JArray(required);
            return schema;
        }

        private static JObject Array(JObject items) => new JObject
        {
            ["type"] = "array",
            ["items"] = items,
        };

        private static JObject String() => new JObject { ["type"] = "string" };
        private static JObject Boolean() => new JObject { ["type"] = "boolean" };
        private static JObject Integer(int? minimum = null)
        {
            var schema = new JObject { ["type"] = "integer" };
            if (minimum.HasValue) schema["minimum"] = minimum.Value;
            return schema;
        }

        private static JObject NullableInteger() => new JObject { ["type"] = new JArray { "integer", "null" } };
    }
}
