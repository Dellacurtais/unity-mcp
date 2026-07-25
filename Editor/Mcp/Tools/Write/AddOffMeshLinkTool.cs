using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
#pragma warning disable CS0618
    public sealed class AddOffMeshLinkTool : IMcpTool
    {
        public string Name => "add_offmesh_link";
        public string Description => "Adds or reconfigures Unity's deprecated OffMeshLink component. Unity 6000.3 marks OffMeshLink obsolete; use add_navmesh_link from AI Navigation when available. A valid endInstanceId is required, and area is one defined name or index.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["startInstanceId"] = new JObject { ["type"] = "integer" },
                ["endInstanceId"] = new JObject { ["type"] = "integer" },
                ["costOverride"] = new JObject { ["type"] = "number" },
                ["biDirectional"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["activated"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["autoUpdatePositions"] = new JObject { ["type"] = "boolean" },
                ["area"] = NavMeshSchemas.AreaIndex,
            },
            ["required"] = new JArray { "instanceId", "endInstanceId" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["hostInstanceId"] = new JObject { ["type"] = "integer" },
                ["startInstanceId"] = new JObject { ["type"] = "integer" },
                ["endInstanceId"] = new JObject { ["type"] = "integer" },
                ["area"] = new JObject { ["type"] = "integer" },
                ["areaName"] = new JObject { ["type"] = "string" },
                ["deprecated"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "instanceId", "hostInstanceId", "startInstanceId", "endInstanceId", "area", "areaName", "deprecated" },
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.AddOffMeshLink(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
#pragma warning restore CS0618
}
