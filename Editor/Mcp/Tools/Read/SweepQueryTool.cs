using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class SweepQueryTool : IMcpTool
    {
        public string Name => "sweep_query";
        public string Description =>
            "Sweeps a volume along a direction (Physics.BoxCastAll/SphereCastAll/CapsuleCastAll), reports every collider hit. " +
            "Useful for 'what would I hit if I moved this rigidbody'. shape: 'box'/'sphere'/'capsule'. Same args as overlap_query plus direction + maxDistance.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["shape"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "box", "sphere", "capsule" } },
                ["direction"] = new JObject(),
                ["maxDistance"] = new JObject { ["type"] = "number" },
                ["center"] = new JObject(),
                ["halfExtents"] = new JObject(),
                ["rotation"] = new JObject(),
                ["origin"] = new JObject(),
                ["radius"] = new JObject { ["type"] = "number" },
                ["pointA"] = new JObject(),
                ["pointB"] = new JObject(),
                ["layerMask"] = new JObject(),
                ["hitTriggers"] = new JObject(),
            },
            ["required"] = new JArray { "shape", "direction" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.SweepQuery(args);
            return r.Ok ? ToolResult.Ok(r.Summary) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
