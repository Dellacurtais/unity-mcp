using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadHierarchyTool : IMcpTool
    {
        public string Name => "read_hierarchy";
        public string Description =>
            "Returns the loaded-scenes hierarchy (active + additive) as a flat list of {instanceId, name, parentId, sceneName, tag, layer, isActive, isStatic, prefabStatus, childIds, componentTypes}. Lightweight (no serialized field values). Use this before analyze_performance or batch authoring to know what already exists.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;
        public JObject OutputSchema => SceneToolSchemas.ReadHierarchy;

        public ToolResult Execute(JObject args)
        {
            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();

            var nodes = new JArray();
            foreach (var info in snapshot.All())
            {
                var componentTypes = new JArray();
                foreach (var c in info.Components) componentTypes.Add(c.TypeName);

                var children = new JArray();
                foreach (var id in info.ChildIds) children.Add(id);

                nodes.Add(new JObject
                {
                    ["instanceId"] = info.InstanceID,
                    ["name"] = info.Name,
                    ["parentId"] = info.ParentId,
                    ["sceneName"] = info.SceneName,
                    ["scenePath"] = info.ScenePath,
                    ["sceneGuid"] = info.SceneGuid,
                    ["sceneHandle"] = info.SceneHandle,
                    ["hierarchyPath"] = info.HierarchyPath,
                    ["globalObjectId"] = info.GlobalObjectId,
                    ["isPrefabStage"] = info.IsPrefabStage,
                    ["tag"] = info.Tag,
                    ["layer"] = info.Layer,
                    ["isActive"] = info.IsActive,
                    ["isStatic"] = info.IsStatic,
                    ["staticFlags"] = (int)info.StaticFlags,
                    ["prefabStatus"] = info.PrefabStatus.ToString(),
                    ["prefabPath"] = info.PrefabAssetPath,
                    ["childIds"] = children,
                    ["componentTypes"] = componentTypes,
                });
            }

            var loadedScenes = new JArray();
            foreach (var s in snapshot.LoadedScenes)
            {
                loadedScenes.Add(new JObject
                {
                    ["name"] = s.Name,
                    ["path"] = s.Path,
                    ["guid"] = s.Guid,
                    ["handle"] = s.Handle,
                    ["isActive"] = s.IsActive,
                    ["isPrefabStage"] = s.IsPrefabStage,
                });
            }

            var payload = new JObject
            {
                ["sceneName"] = snapshot.SceneName,
                ["scenePath"] = snapshot.ScenePath,
                ["loadedScenes"] = loadedScenes,
                ["count"] = snapshot.Count,
                ["nodes"] = nodes,
            };

            return ToolResult.Ok($"hierarchy with {snapshot.Count} GameObjects", payload);
        }
    }
}
