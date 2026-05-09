using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class FindReferencesToAssetTool : IMcpTool
    {
        public string Name => "find_references_to_asset";
        public string Description =>
            "Walks every Component in the active scene and reports GameObjects whose serialized fields reference the given asset (matched by GUID or path). Use to audit who depends on an asset before deleting or modifying it.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["guidOrPath"] = new JObject { ["type"] = "string" },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 200, ["minimum"] = 1 },
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var guidOrPath = (string)args["guidOrPath"];
            var limit = (int?)args["limit"] ?? 200;
            if (string.IsNullOrEmpty(guidOrPath))
                return ToolResult.Error("BAD_ARGS", "guidOrPath is required");

            var path = guidOrPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? guidOrPath
                : AssetDatabase.GUIDToAssetPath(guidOrPath);
            if (string.IsNullOrEmpty(path))
                return ToolResult.Error("NOT_FOUND", $"asset not found: {guidOrPath}");

            var target = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (target == null) return ToolResult.Error("NOT_FOUND", $"failed to load: {path}");
            int targetId = target.GetInstanceID();

            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();

            var refs = new JArray();
            int found = 0;

            foreach (var info in snapshot.All())
            {
                if (found >= limit) break;
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var so = new SerializedObject(c);
                    var sp = so.GetIterator();
                    var enter = true;
                    while (sp.NextVisible(enter))
                    {
                        enter = false;
                        if (sp.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (sp.objectReferenceInstanceIDValue != targetId) continue;
                        refs.Add(new JObject
                        {
                            ["instanceId"] = info.InstanceID,
                            ["gameObjectPath"] = snapshot.GetPath(info.InstanceID),
                            ["componentType"] = c.GetType().FullName,
                            ["property"] = sp.propertyPath,
                        });
                        found++;
                        if (found >= limit) break;
                    }
                    if (found >= limit) break;
                }
            }

            var payload = new JObject
            {
                ["assetPath"] = path,
                ["assetType"] = target.GetType().Name,
                ["referenceCount"] = found,
                ["truncated"] = found >= limit,
                ["references"] = refs,
            };
            return ToolResult.Ok($"{found} reference(s) to '{target.name}'", payload);
        }
    }
}
