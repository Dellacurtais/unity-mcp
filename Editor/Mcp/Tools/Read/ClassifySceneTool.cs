using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ClassifySceneTool : IMcpTool
    {
        public string Name => "classify_scene";
        public string Description =>
            "Single-shot scene summary bucketed by tag, layer, top component types, hierarchy stats (root/leaf count + max depth), spatial bounds (+ near/far origin counts), and prefab status (linked / broken / unlinked). Cheaper than read_scene for getting a feel of a large scene.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => SceneToolSchemas.ClassifyScene;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.ClassifyScene(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
