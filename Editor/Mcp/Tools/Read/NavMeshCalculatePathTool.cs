using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class NavMeshCalculatePathTool : IMcpTool
    {
        public string Name => "navmesh_calculate_path";
        public string Description =>
            "`NavMesh.CalculatePath` — full path from start to end on the navmesh. Returns status (Complete / Partial / Invalid), corners, total distance. Use to answer 'can the agent reach this target' before assigning destinations.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["start"] = new JObject(),
                ["end"] = new JObject(),
                ["areaMask"] = new JObject(),
            },
            ["required"] = new JArray { "start", "end" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.CalculatePath(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
