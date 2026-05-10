using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateCameraTool : IMcpTool
    {
        public string Name => "create_camera";
        public string Description =>
            "Creates a Camera GameObject. When isMain=true, untags any existing MainCamera and tags the new one. Use set_camera_props later to tweak fov/clip planes/background.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "Camera" },
                ["isMain"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["fieldOfView"] = new JObject { ["type"] = "number", ["default"] = 60.0 },
                ["nearClipPlane"] = new JObject { ["type"] = "number", ["default"] = 0.3 },
                ["farClipPlane"] = new JObject { ["type"] = "number", ["default"] = 1000.0 },
                ["clearFlags"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Skybox", "SolidColor", "Depth", "Nothing" } },
                ["backgroundColor"] = SchemaHelpers.ColorSchema,
                ["orthographic"] = new JObject { ["type"] = "boolean" },
                ["orthographicSize"] = new JObject { ["type"] = "number" },
                ["depth"] = new JObject { ["type"] = "number" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["rotation"] = SchemaHelpers.RotSchema,
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = CameraFactory.Create(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
