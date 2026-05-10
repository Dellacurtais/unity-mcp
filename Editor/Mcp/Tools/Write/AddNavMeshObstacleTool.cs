using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddNavMeshObstacleTool : IMcpTool
    {
        public string Name => "add_navmesh_obstacle";
        public string Description =>
            "Adds (or reconfigures) a NavMeshObstacle on a GameObject for dynamic carving. " +
            "shape: 'Box' (uses size) or 'Capsule' (uses radius/height). carving=true enables runtime carving — pass carveOnlyStationary=true (default) so moving obstacles don't thrash the bake; carvingMoveThreshold and carvingTimeToStationary tune that behavior. " +
            "Re-adds without losing existing settings: the second call only patches the props you provide.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["shape"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Box", "Capsule" } },
                ["center"] = new JObject(),
                ["size"] = new JObject(),
                ["radius"] = new JObject { ["type"] = "number" },
                ["height"] = new JObject { ["type"] = "number" },
                ["carving"] = new JObject { ["type"] = "boolean" },
                ["carveOnlyStationary"] = new JObject { ["type"] = "boolean" },
                ["carvingMoveThreshold"] = new JObject { ["type"] = "number" },
                ["carvingTimeToStationary"] = new JObject { ["type"] = "number" },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.AddObstacle(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID })
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
