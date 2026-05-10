using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.NavMesh.Tools
{
    public sealed class AddNavMeshModifierTool : IMcpTool
    {
        public string Name => "add_navmesh_modifier";
        public string Description =>
            "Adds a NavMeshModifier (default) or NavMeshModifierVolume (when `volume: true`) to a GameObject. " +
            "NavMeshModifier overrides the area assignment of geometry under the GameObject (or child) — pass `area` (string name OR int index) and `overrideArea: true`. `ignoreFromBuild: true` excludes geometry from the bake entirely. " +
            "NavMeshModifierVolume is a box volume that overrides area within its bounds — pass `center` and `size` plus `area`.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["volume"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["area"] = new JObject(),
                ["overrideArea"] = new JObject { ["type"] = "boolean" },
                ["ignoreFromBuild"] = new JObject { ["type"] = "boolean" },
                ["applyToChildren"] = new JObject { ["type"] = "boolean" },
                ["center"] = new JObject(),
                ["size"] = new JObject(),
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshSurfaceAuthor.AddModifier(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID })
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
