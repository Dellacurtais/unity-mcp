using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddNavMeshObstacleTool : IMcpTool
    {
        public string Name => "add_navmesh_obstacle";
        public string Description => "Adds or patches a NavMeshObstacle with Undo and prefab-override recording. Box uses size; Capsule uses radius and height. Carving changes the runtime NavMesh near the obstacle and should be tuned with stationary thresholds.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["shape"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Box", "Capsule" } },
                ["center"] = SchemaHelpers.StrictVec3Schema,
                ["size"] = SchemaHelpers.NonNegativeVec3Schema,
                ["radius"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["height"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["carving"] = new JObject { ["type"] = "boolean" },
                ["carveOnlyStationary"] = new JObject { ["type"] = "boolean" },
                ["carvingMoveThreshold"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["carvingTimeToStationary"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["hostInstanceId"] = new JObject { ["type"] = "integer" },
                ["shape"] = new JObject { ["type"] = "string" },
                ["center"] = NavMeshSchemas.Vec3Output,
                ["size"] = NavMeshSchemas.Vec3Output,
                ["radius"] = new JObject { ["type"] = "number" },
                ["height"] = new JObject { ["type"] = "number" },
                ["carving"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "instanceId", "hostInstanceId", "shape", "center", "size", "radius", "height", "carving" },
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var result = NavMeshAuthor.AddObstacle(args);
            return result.Ok ? ToolResult.Ok(result.Summary, result.Data) : ToolResult.Error("OP_FAILED", result.Error);
        }
    }
}
