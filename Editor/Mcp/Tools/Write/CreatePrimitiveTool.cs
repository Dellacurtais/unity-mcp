using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreatePrimitiveTool : IMcpTool
    {
        public string Name => "create_primitive";
        public string Description =>
            "Creates a Unity primitive GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad). Registers a single Undo step. NOTE on URP/HDRP: Unity's built-in primitive material is the legacy Standard shader which appears pink in URP/HDRP. Follow up with `set_material` (preferably URP/HDRP-Lit) when using a non-Built-in pipeline — check with `get_project_meta` first.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" } },
                ["name"] = new JObject { ["type"] = "string" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["rotation"] = SchemaHelpers.RotSchema,
                ["scale"] = SchemaHelpers.Vec3Schema,
            },
            ["required"] = new JArray { "kind" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => GameObjectAuthoringSchemas.TargetResult;

        public ToolResult Execute(JObject args)
        {
            var r = PrimitiveFactory.Create(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, SceneAuthoringSupport.TargetResult(r.InstanceID, r.Data));
        }
    }
}
