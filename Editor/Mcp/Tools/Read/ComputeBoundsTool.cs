using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ComputeBoundsTool : IMcpTool
    {
        public string Name => "compute_bounds";
        public string Description =>
            "Returns the axis-aligned world bounds (center, size, min, max) of a GameObject's renderers. recursive=true (default) encapsulates child renderers too. Returns an error when no Renderer is found.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["recursive"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["recursive"] = new JObject { ["type"] = "boolean" },
                ["bounds"] = new JObject { ["type"] = "object" },
            },
            ["required"] = new JArray { "target", "recursive", "bounds" },
        };
        public ToolResult Execute(JObject args)
        {
            var r = PhysicsAuthor.ComputeBounds(args);
            return r.Ok ? ToolResult.Ok(r.Summary, r.Data) : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
