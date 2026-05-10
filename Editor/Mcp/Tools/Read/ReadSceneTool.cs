using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadSceneTool : IMcpTool
    {
        public string Name => "read_scene";
        public string Description =>
            "Returns the active scene as a hierarchical tree (parents → children) including transforms and component types. Use depth/maxNodes to bound payload size; use includeProperties=true sparingly (large) — prefer read_component for per-object detail.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["depth"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 5 },
                ["maxNodes"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 500 },
                ["includeComponents"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["includeProperties"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var depth = (int?)args["depth"] ?? 5;
            var maxNodes = (int?)args["maxNodes"] ?? 500;
            var includeComponents = (bool?)args["includeComponents"] ?? true;
            var includeProperties = (bool?)args["includeProperties"] ?? false;

            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();

            var roots = new JArray();
            int emitted = 0;
            foreach (var info in snapshot.All())
            {
                if (info.ParentId != 0) continue;
                roots.Add(BuildNode(info.InstanceID, depth, includeComponents, includeProperties, ref emitted, maxNodes));
                if (emitted >= maxNodes) break;
            }

            var loadedScenes = new JArray();
            foreach (var s in snapshot.LoadedScenes)
            {
                loadedScenes.Add(new JObject
                {
                    ["name"] = s.Name,
                    ["path"] = s.Path,
                    ["isActive"] = s.IsActive,
                });
            }

            var payload = new JObject
            {
                ["sceneName"] = snapshot.SceneName,
                ["scenePath"] = snapshot.ScenePath,
                ["loadedScenes"] = loadedScenes,
                ["totalGameObjects"] = snapshot.Count,
                ["emitted"] = emitted,
                ["truncated"] = emitted >= maxNodes,
                ["roots"] = roots,
            };
            return ToolResult.Ok($"scene tree (emitted {emitted} of {snapshot.Count})", payload);
        }

        private JObject BuildNode(int instanceId, int remainingDepth, bool includeComponents, bool includeProperties, ref int emitted, int maxNodes)
        {
            var snapshot = Bootstrap.Snapshot;
            var info = snapshot.Get(instanceId);
            emitted++;
            var node = new JObject
            {
                ["instanceId"] = instanceId,
                ["name"] = info.Name,
                ["sceneName"] = info.SceneName,
                ["tag"] = info.Tag,
                ["layer"] = info.Layer,
                ["isActive"] = info.IsActive,
                ["isStatic"] = info.IsStatic,
                ["staticFlags"] = (int)info.StaticFlags,
                ["prefabStatus"] = info.PrefabStatus.ToString(),
                ["prefabPath"] = info.PrefabAssetPath,
                ["localPosition"] = new JArray { info.LocalPosition.x, info.LocalPosition.y, info.LocalPosition.z },
                ["localRotation"] = new JArray { info.LocalRotation.x, info.LocalRotation.y, info.LocalRotation.z, info.LocalRotation.w },
                ["localScale"] = new JArray { info.LocalScale.x, info.LocalScale.y, info.LocalScale.z },
                ["worldPosition"] = new JArray { info.WorldPosition.x, info.WorldPosition.y, info.WorldPosition.z },
                ["worldRotation"] = new JArray { info.WorldRotation.x, info.WorldRotation.y, info.WorldRotation.z, info.WorldRotation.w },
            };

            if (includeComponents)
            {
                var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                var comps = new JArray();
                if (go != null)
                {
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) { comps.Add(new JObject { ["componentType"] = "[Missing]" }); continue; }
                        if (includeProperties) comps.Add(ReadHelpers.SerializeComponent(c));
                        else comps.Add(new JObject
                        {
                            ["componentType"] = c.GetType().FullName,
                            ["instanceId"] = c.GetInstanceID(),
                            ["enabled"] = (c is Behaviour b) ? b.enabled : (JToken)true,
                        });
                    }
                }
                node["components"] = comps;
            }

            if (remainingDepth <= 1 || info.ChildIds.Count == 0)
            {
                if (info.ChildIds.Count > 0) node["truncatedChildren"] = info.ChildIds.Count;
                return node;
            }

            var children = new JArray();
            foreach (var id in info.ChildIds)
            {
                if (emitted >= maxNodes) break;
                children.Add(BuildNode(id, remainingDepth - 1, includeComponents, includeProperties, ref emitted, maxNodes));
            }
            node["children"] = children;
            return node;
        }
    }
}
