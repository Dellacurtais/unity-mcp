using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetCameraPropsTool : IMcpTool
    {
        public string Name => "set_camera_props";
        public string Description =>
            "Mutates camera settings on an existing Camera component (fov, clip planes, clear flags, background color, orthographic, depth). Pass only the keys you want to change.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["fieldOfView"] = new JObject { ["type"] = "number" },
                ["nearClipPlane"] = new JObject { ["type"] = "number" },
                ["farClipPlane"] = new JObject { ["type"] = "number" },
                ["clearFlags"] = new JObject { ["type"] = "string" },
                ["backgroundColor"] = SchemaHelpers.ColorSchema,
                ["orthographic"] = new JObject { ["type"] = "boolean" },
                ["orthographicSize"] = new JObject { ["type"] = "number" },
                ["depth"] = new JObject { ["type"] = "number" },
                ["cullingMask"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => GameObjectAuthoringSchemas.TargetResult;

        public ToolResult Execute(JObject args)
        {
            var r = CameraFactory.SetProps(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, SceneAuthoringSupport.TargetResult(r.InstanceID, r.Data));
        }
    }
}
