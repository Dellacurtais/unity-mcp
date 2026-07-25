using System;
using Maicon.SceneAnalyzer.Editor.AssetTools;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
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
                ["guidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 200, ["minimum"] = 1, ["maximum"] = 5000 },
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["assetType"] = new JObject { ["type"] = "string" },
                ["referenceCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["truncated"] = new JObject { ["type"] = "boolean" },
                ["references"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "assetPath", "assetType", "referenceCount", "truncated", "references" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var guidOrPath = (string)args["guidOrPath"];
            var limit = (int?)args["limit"] ?? 200;
            if (string.IsNullOrEmpty(guidOrPath))
                return ToolResult.Error("BAD_ARGS", "guidOrPath is required");

            var resolvedPath = AssetToolSupport.ResolveGuidOrPath(guidOrPath);
            if (!AssetToolSupport.TryAssetPath(resolvedPath, true, out var path, out var pathError))
                return ToolResult.Error("NOT_FOUND", pathError);

            var target = AssetDatabase.LoadMainAssetAtPath(path);
            if (target == null) return ToolResult.Error("NOT_FOUND", $"failed to load: {path}");
            int targetId = target.GetInstanceID();

            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();

            var refs = new JArray();
            int found = 0;
            int probeLimit = limit + 1;

            foreach (var info in snapshot.All())
            {
                if (found >= probeLimit) break;
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
                            ["target"] = ToolTargetRef.Capture(go)?.ToJson(),
                            ["gameObjectPath"] = snapshot.GetPath(info.InstanceID),
                            ["componentType"] = c.GetType().FullName,
                            ["property"] = sp.propertyPath,
                        });
                        found++;
                        if (found >= probeLimit) break;
                    }
                    if (found >= probeLimit) break;
                }
            }

            bool truncated = refs.Count > limit;
            if (truncated) refs.RemoveAt(refs.Count - 1);
            var payload = new JObject
            {
                ["assetPath"] = path,
                ["assetType"] = target.GetType().Name,
                ["referenceCount"] = refs.Count,
                ["truncated"] = truncated,
                ["references"] = refs,
            };
            return ToolResult.Ok($"{refs.Count} reference(s) to '{target.name}'", payload);
        }
    }
}
