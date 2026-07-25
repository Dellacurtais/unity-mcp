using System.Linq;
using Maicon.SceneAnalyzer.Editor.AssetTools;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ListAssetsTool : IMcpTool
    {
        public string Name => "list_assets";
        public string Description =>
            "Lists project assets indexed by AssetCatalog, optionally filtered by type (Prefab, Material, Texture, Texture2D, Mesh, AudioClip, AnimationClip, Shader, Sprite, AnimatorController, AudioMixer, TimelineAsset, VolumeProfile, PhysicMaterial, AvatarMask, ScriptableObject), name substring, or folder prefix. Returns guid, path, name, typeName, labels.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["type"] = new JObject { ["type"] = "string" },
                ["nameContains"] = new JObject { ["type"] = "string" },
                ["folder"] = new JObject { ["type"] = "string" },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 100 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
            },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => AssetToolSupport.AssetListOutput("assets");
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var type = (string)args["type"];
            var name = (string)args["nameContains"];
            var folder = (string)args["folder"];
            var limit = (int?)args["limit"] ?? 100;
            var offset = (int?)args["offset"] ?? 0;

            var catalog = Bootstrap.AssetCatalog;
            catalog.EnsureBuilt();

            var matches = catalog.Search(name, type, folder)
                .OrderBy(entry => entry.Path, System.StringComparer.Ordinal)
                .ToArray();
            var page = matches.Skip(offset).Take(limit).ToArray();
            var arr = new JArray();
            foreach (var e in page)
            {
                var labels = new JArray();
                if (e.Labels != null)
                    foreach (var l in e.Labels) labels.Add(l);
                arr.Add(new JObject
                {
                    ["guid"] = e.Guid,
                    ["path"] = e.Path,
                    ["name"] = e.Name,
                    ["typeName"] = e.TypeName,
                    ["labels"] = labels,
                });
            }

            var payload = new JObject
            {
                ["catalogTotal"] = catalog.Count,
                ["total"] = matches.Length,
                ["returned"] = arr.Count,
                ["offset"] = offset,
                ["truncated"] = offset + arr.Count < matches.Length,
                ["assets"] = arr,
            };
            return ToolResult.Ok($"{arr.Count} assets" + (string.IsNullOrEmpty(type) ? "" : $" of type {type}"), payload);
        }
    }
}
