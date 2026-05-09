using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover
{
    public sealed class SearchAssetsTool : IMcpTool
    {
        public string Name => "search_assets";
        public string Description =>
            "Searches the project's AssetCatalog by name substring (case-insensitive), optional type filter and folder prefix. Lighter wrapper around list_assets when you only have a fragment of the asset's name.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["nameContains"] = new JObject { ["type"] = "string" },
                ["type"] = new JObject { ["type"] = "string" },
                ["folder"] = new JObject { ["type"] = "string", ["description"] = "Path prefix (e.g. \"Assets/Prefabs\")." },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 50, ["minimum"] = 1 },
            },
            ["required"] = new JArray { "nameContains" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var name = (string)args["nameContains"];
            var type = (string)args["type"];
            var folder = (string)args["folder"];
            var limit = (int?)args["limit"] ?? 50;

            var catalog = Bootstrap.AssetCatalog;
            catalog.EnsureBuilt();

            var arr = new JArray();
            int returned = 0;
            foreach (var e in catalog.Search(name, type, folder))
            {
                if (returned++ >= limit) break;
                arr.Add(new JObject
                {
                    ["guid"] = e.Guid,
                    ["path"] = e.Path,
                    ["name"] = e.Name,
                    ["typeName"] = e.TypeName,
                });
            }
            var payload = new JObject { ["matches"] = arr, ["returned"] = returned };
            return ToolResult.Ok($"{returned} matches for '{name}'", payload);
        }
    }
}
