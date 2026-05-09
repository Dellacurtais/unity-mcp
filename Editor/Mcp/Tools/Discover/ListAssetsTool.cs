using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class ListAssetsTool : IMcpTool
    {
        public string Name => "list_assets";
        public string Description =>
            "Lists project assets indexed by AssetCatalog, optionally filtered by type (Prefab, Material, Texture, Mesh, AudioClip, AnimationClip, Shader, Sprite, ScriptableObject), name substring, or folder prefix. Returns guid, path, name, typeName, labels.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["type"] = new JObject { ["type"] = "string" },
                ["nameContains"] = new JObject { ["type"] = "string" },
                ["folder"] = new JObject { ["type"] = "string" },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 100 },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
            },
            ["additionalProperties"] = false,
        };

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

            var arr = new JArray();
            int seen = 0;
            int returned = 0;
            foreach (var e in catalog.Search(name, type, folder))
            {
                if (seen++ < offset) continue;
                if (returned++ >= limit) break;
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
                ["total"] = catalog.Count,
                ["returned"] = returned,
                ["offset"] = offset,
                ["assets"] = arr,
            };
            return ToolResult.Ok($"{returned} assets" + (string.IsNullOrEmpty(type) ? "" : $" of type {type}"), payload);
        }
    }
}
