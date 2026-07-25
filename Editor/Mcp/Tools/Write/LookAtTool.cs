using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class LookAtTool : IMcpTool
    {
        public string Name => "look_at";
        public string Description =>
            "Aims a GameObject at a target via Transform.LookAt — works on cameras (Z+ forward), but also on any object. Pass either targetInstanceId (use that GO's world position) or targetPosition (raw Vector3). Optional worldUp (default Vector3.up). For non-camera renderers like Quad whose face is -Z, set flipForCamera=true to add a 180° spin so the visible side faces the target.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["targetInstanceId"] = new JObject { ["type"] = "integer" },
                ["targetPosition"] = SchemaHelpers.Vec3Schema,
                ["worldUp"] = SchemaHelpers.Vec3Schema,
                ["flipForCamera"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => GameObjectAuthoringSchemas.TargetResult;

        public ToolResult Execute(JObject args)
        {
            var r = GameObjectEditor.LookAt(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, SceneAuthoringSupport.TargetResult(r.InstanceID, r.Data));
        }
    }
}
