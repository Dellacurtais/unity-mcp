using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class RaycastTool : IMcpTool
    {
        public string Name => "raycast";
        public string Description =>
            "Casts a ray and reports hit colliders (Physics.Raycast / RaycastAll). Each hit returns the GameObject id/name, tag, layer, hit point, normal, distance, and collider type. layerMask: int OR layer name string OR array of layer names. hitTriggers: 'UseGlobal' (default) / true / false. Set multiple=true to get every collider along the ray (sorted by distance).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["origin"] = new JObject(),
                ["direction"] = new JObject(),
                ["maxDistance"] = new JObject { ["type"] = "number" },
                ["layerMask"] = new JObject(),
                ["hitTriggers"] = new JObject(),
                ["multiple"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "origin", "direction" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.Raycast(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
