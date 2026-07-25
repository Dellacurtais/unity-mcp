using System.Linq;
using Maicon.SceneAnalyzer.Editor.AssetTools;
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
                ["nameContains"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["type"] = new JObject { ["type"] = "string" },
                ["folder"] = new JObject { ["type"] = "string", ["description"] = "Path prefix (e.g. \"Assets/Prefabs\")." },
                ["offset"] = new JObject { ["type"] = "integer", ["default"] = 0, ["minimum"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["default"] = 50, ["minimum"] = 1, ["maximum"] = 5000 },
            },
            ["required"] = new JArray { "nameContains" },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => AssetToolSupport.AssetListOutput("matches");
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var name = (string)args["nameContains"];
            var type = (string)args["type"];
            var folder = (string)args["folder"];
            var offset = (int?)args["offset"] ?? 0;
            var limit = (int?)args["limit"] ?? 50;

            var catalog = Bootstrap.AssetCatalog;
            catalog.EnsureBuilt();

            var matches = catalog.Search(name, type, folder)
                .OrderBy(entry => entry.Path, System.StringComparer.Ordinal)
                .ToArray();
            var arr = new JArray();
            foreach (var e in matches.Skip(offset).Take(limit))
            {
                arr.Add(new JObject
                {
                    ["guid"] = e.Guid,
                    ["path"] = e.Path,
                    ["name"] = e.Name,
                    ["typeName"] = e.TypeName,
                });
            }
            var payload = new JObject
            {
                ["catalogTotal"] = catalog.Count,
                ["total"] = matches.Length,
                ["returned"] = arr.Count,
                ["offset"] = offset,
                ["truncated"] = offset + arr.Count < matches.Length,
                ["matches"] = arr,
            };
            return ToolResult.Ok($"{arr.Count} matches for '{name}'", payload);
        }
    }
}
