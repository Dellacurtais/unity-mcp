using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadHierarchyTool : IMcpTool
    {
        public string Name => "read_hierarchy";
        public string Description =>
            "Returns the active scene hierarchy as a flat list of {instanceId, name, parentId, isActive, isStatic, childIds, componentTypes}. Lightweight (no serialized field values). Use this before analyze_performance or batch authoring to know what already exists.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

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
                    ["isActive"] = info.IsActive,
                    ["isStatic"] = info.IsStatic,
                    ["childIds"] = children,
                    ["componentTypes"] = componentTypes,
                });
            }

            var payload = new JObject
            {
                ["sceneName"] = snapshot.SceneName,
                ["scenePath"] = snapshot.ScenePath,
                ["count"] = snapshot.Count,
                ["nodes"] = nodes,
            };

            return ToolResult.Ok($"hierarchy with {snapshot.Count} GameObjects", payload);
        }
    }
}
