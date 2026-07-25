using System;
using Maicon.SceneAnalyzer.Editor.AssetTools;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ReadAssetMetadataTool : IMcpTool
    {
        public string Name => "read_asset_metadata";
        public string Description =>
            "Returns asset-type-specific metadata: meshes (verts, tris, sub-meshes, bounds), textures (size, format, mipmaps), audio clips (length, channels, frequency), materials (shader, render queue), prefabs (GameObject count), animation clips (length, fps), shaders (queue), sprites (rect, pivot). Accepts asset path or GUID.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["guidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["path"] = new JObject { ["type"] = "string" },
                ["guid"] = new JObject { ["type"] = "string" },
                ["localFileId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["type"] = new JObject { ["type"] = "string" },
                ["target"] = new JObject { ["type"] = new JArray { "object", "null" } },
            },
            ["required"] = new JArray { "path", "guid", "localFileId", "name", "type", "target" },
            ["additionalProperties"] = true,
        };
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var guidOrPath = (string)args["guidOrPath"];
            if (string.IsNullOrEmpty(guidOrPath))
                return ToolResult.Error("BAD_ARGS", "guidOrPath is required");

            var resolvedPath = AssetToolSupport.ResolveGuidOrPath(guidOrPath);
            if (!AssetToolSupport.TryAssetPath(resolvedPath, true, out var path, out var pathError))
                return ToolResult.Error("NOT_FOUND", pathError);

            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                return ToolResult.Error("NOT_FOUND", $"failed to load asset at {path}");

            var guid = AssetDatabase.AssetPathToGUID(path);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out long localFileId);
            var meta = new JObject
            {
                ["path"] = path,
                ["guid"] = guid,
                ["localFileId"] = localFileId,
                ["name"] = asset.name,
                ["type"] = asset.GetType().Name,
                ["target"] = ToolTargetRef.Capture(asset)?.ToJson(),
            };

            switch (asset)
            {
                case Mesh m:
                    meta["vertexCount"] = m.vertexCount;
                    long indexCount = 0;
                    long triangleCount = 0;
                    for (var subMesh = 0; subMesh < m.subMeshCount; subMesh++)
                    {
                        var subMeshIndexCount = (long)m.GetIndexCount(subMesh);
                        indexCount += subMeshIndexCount;
                        if (m.GetTopology(subMesh) == MeshTopology.Triangles) triangleCount += subMeshIndexCount / 3;
                    }
                    meta["indexCount"] = indexCount;
                    meta["triangleCount"] = triangleCount;
                    meta["subMeshCount"] = m.subMeshCount;
                    meta["bounds"] = new JObject
                    {
                        ["center"] = new JArray { m.bounds.center.x, m.bounds.center.y, m.bounds.center.z },
                        ["size"] = new JArray { m.bounds.size.x, m.bounds.size.y, m.bounds.size.z },
                    };
                    break;
                case Texture2D t2:
                    meta["width"] = t2.width;
                    meta["height"] = t2.height;
                    meta["format"] = t2.format.ToString();
                    meta["mipmapCount"] = t2.mipmapCount;
                    break;
                case Texture tx:
                    meta["width"] = tx.width;
                    meta["height"] = tx.height;
                    break;
                case AudioClip ac:
                    meta["length"] = ac.length;
                    meta["channels"] = ac.channels;
                    meta["frequency"] = ac.frequency;
                    meta["samples"] = ac.samples;
                    break;
                case Material mat:
                    meta["shader"] = mat.shader != null ? mat.shader.name : null;
                    meta["renderQueue"] = mat.renderQueue;
                    break;
                case AnimationClip clip:
                    meta["length"] = clip.length;
                    meta["frameRate"] = clip.frameRate;
                    meta["isLooping"] = clip.isLooping;
                    break;
                case Shader sh:
                    meta["renderQueue"] = sh.renderQueue;
                    meta["passCount"] = sh.passCount;
                    break;
                case Sprite sp:
                    meta["rect"] = new JArray { sp.rect.x, sp.rect.y, sp.rect.width, sp.rect.height };
                    meta["pivot"] = new JArray { sp.pivot.x, sp.pivot.y };
                    meta["pixelsPerUnit"] = sp.pixelsPerUnit;
                    break;
                case GameObject prefab:
                {
                    int goCount = 0;
                    foreach (var t in prefab.GetComponentsInChildren<Transform>(true)) goCount++;
                    meta["gameObjectCount"] = goCount;
                    var components = new JArray();
                    foreach (var c in prefab.GetComponents<Component>())
                        if (c != null) components.Add(c.GetType().Name);
                    meta["topLevelComponents"] = components;
                    break;
                }
            }

            return ToolResult.Ok($"{asset.GetType().Name} '{asset.name}' metadata", meta);
        }
    }
}
