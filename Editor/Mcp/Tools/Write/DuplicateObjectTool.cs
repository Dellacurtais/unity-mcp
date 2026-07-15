using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class DuplicateObjectTool : IMcpTool
    {
        public string Name => "duplicate_object";
        public string Description =>
            "Clones a GameObject with all its components and children (Object.Instantiate). Defaults the parent to the source's parent. Apply `offset` to bump position relative to source, or `position`/`rotation`/`scale` to override outright. Use `name` to rename the clone (default: '<source> (Copy)'). Returns the clone's instanceId. Single Undo step.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["offset"] = SchemaHelpers.Vec3Schema,
                ["position"] = SchemaHelpers.Vec3Schema,
                ["rotation"] = SchemaHelpers.RotSchema,
                ["scale"] = SchemaHelpers.Vec3Schema,
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = GameObjectEditor.Duplicate(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
