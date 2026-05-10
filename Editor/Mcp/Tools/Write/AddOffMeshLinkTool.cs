using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddOffMeshLinkTool : IMcpTool
    {
        public string Name => "add_offmesh_link";
        public string Description =>
            "Adds an OffMeshLink (built-in legacy NavMesh component) to a GameObject. Connects two transforms so agents can traverse a non-walkable gap (jump, ladder, teleport). " +
            "instanceId is the host GO. startInstanceId / endInstanceId reference the transforms to use as endpoints — when omitted, the host transform is used as start and end remains null until the user assigns it. " +
            "area accepts a string name ('Jump'/'Walkable') or an int index — single value, NOT a mask. biDirectional defaults to true. costOverride < 0 disables the override. " +
            "Prefer add_navmesh_link when com.unity.ai.navigation is installed (modern equivalent).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["startInstanceId"] = new JObject { ["type"] = "integer" },
                ["endInstanceId"] = new JObject { ["type"] = "integer" },
                ["costOverride"] = new JObject { ["type"] = "number" },
                ["biDirectional"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["activated"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["autoUpdatePositions"] = new JObject { ["type"] = "boolean" },
                ["area"] = new JObject(),
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = NavMeshAuthor.AddOffMeshLink(args);
            return r.Ok
                ? ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID })
                : ToolResult.Error("OP_FAILED", r.Error);
        }
    }
}
