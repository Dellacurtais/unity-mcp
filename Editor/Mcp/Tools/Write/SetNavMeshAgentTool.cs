using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetNavMeshAgentTool : IMcpTool
    {
        public string Name => "set_navmesh_agent";
        public string Description =>
            "Combo tool — writes optional NavMeshAgent settings (destination, isStopped, speed/acceleration/angularSpeed/stoppingDistance/radius/height/baseOffset, areaMask, resetPath) AND always returns the current state (isOnNavMesh, hasPath, pathPending, isStopped, destination, remainingDistance, velocity, pathStatus, corner count). Most movement/state happens in Play Mode; in edit mode SetDestination still computes a path.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["destination"] = new JObject(),
                ["isStopped"] = new JObject { ["type"] = "boolean" },
                ["speed"] = new JObject { ["type"] = "number" },
                ["acceleration"] = new JObject { ["type"] = "number" },
                ["angularSpeed"] = new JObject { ["type"] = "number" },
                ["stoppingDistance"] = new JObject { ["type"] = "number" },
                ["radius"] = new JObject { ["type"] = "number" },
                ["height"] = new JObject { ["type"] = "number" },
                ["baseOffset"] = new JObject { ["type"] = "number" },
                ["areaMask"] = new JObject(),
                ["resetPath"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.SetAgent(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
