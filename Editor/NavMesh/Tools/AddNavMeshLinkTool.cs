using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class AddNavMeshLinkTool : IMcpTool
    {
        public string Name => "add_navmesh_link";
        public string Description =>
            "Adds (or reconfigures) a NavMeshLink (modern equivalent of OffMeshLink — part of com.unity.ai.navigation). " +
            "startPoint / endPoint are local-space Vector3 endpoints relative to the host GO. width controls the link's connection breadth. " +
            "agentTypeID restricts which agent types use the link (use list_navmesh_agent_types to enumerate). area accepts a string name or int index (single value, not mask). bidirectional defaults to true. costModifier < 0 means inherit area cost. " +
            "Re-adds without losing existing settings: second call only patches the props you provide.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["startPoint"] = new JObject(),
                ["endPoint"] = new JObject(),
                ["width"] = new JObject { ["type"] = "number" },
                ["agentTypeID"] = new JObject { ["type"] = "integer", ["default"] = 0 },
                ["area"] = new JObject(),
                ["bidirectional"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["autoUpdate"] = new JObject { ["type"] = "boolean" },
                ["costModifier"] = new JObject { ["type"] = "number", ["default"] = -1 },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshSurfaceAuthor.AddLink(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID })
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
