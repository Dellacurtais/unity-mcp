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
                ["origin"] = SchemaHelpers.StrictVec3Schema,
                ["direction"] = SchemaHelpers.StrictVec3Schema,
                ["maxDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["layerMask"] = SchemaHelpers.LayerMaskSchema,
                ["hitTriggers"] = SchemaHelpers.TriggerInteractionSchema,
                ["multiple"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "origin", "direction" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["hits"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "count", "hits" },
        };
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.Raycast(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
