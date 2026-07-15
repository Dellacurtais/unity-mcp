using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetMaterialTool : IMcpTool
    {
        public string Name => "set_material";
        public string Description =>
            "Assigns a material asset (by GUID or path) to a Renderer slot on a GameObject. Idempotent; respects multi-material renderers via slotIndex.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["materialGuid"] = new JObject { ["type"] = "string" },
                ["slotIndex"] = new JObject { ["type"] = "integer", ["default"] = 0, ["minimum"] = 0 },
            },
            ["required"] = new JArray { "instanceId", "materialGuid" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = RendererMaterialEditor.SetMaterial(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
