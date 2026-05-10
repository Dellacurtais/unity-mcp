using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class BakeNavMeshTool : IMcpTool
    {
        public string Name => "bake_navmesh";
        public string Description =>
            "Triggers `UnityEditor.AI.NavMeshBuilder.BuildNavMesh()` — the legacy global navmesh bake. Use the modern per-surface workflow (`bake_navmesh_surface`) when `com.unity.ai.navigation` is installed and the scene has NavMeshSurface components. Sync by default; pass `async: true` to fire-and-forget.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["async"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.BakeLegacy(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
