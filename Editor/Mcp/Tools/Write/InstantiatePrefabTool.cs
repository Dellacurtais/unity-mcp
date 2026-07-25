using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class InstantiatePrefabTool : IMcpTool
    {
        public string Name => "instantiate_prefab";
        public string Description =>
            "Instantiates a prefab asset into the active scene at the given pose. Maintains the prefab link (use unpack later if you need to break it). Registers a single Undo step. Returns the new GameObject's instanceId.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["guidOrPath"] = new JObject { ["type"] = "string", ["description"] = "Asset GUID or path under Assets/." },
                ["name"] = new JObject { ["type"] = "string" },
                ["parent"] = new JObject { ["type"] = "integer", ["description"] = "Parent GameObject instanceId." },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["rotation"] = SchemaHelpers.RotSchema,
                ["scale"] = SchemaHelpers.Vec3Schema,
            },
            ["required"] = new JArray { "guidOrPath" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => GameObjectAuthoringSchemas.TargetResult;

        public ToolResult Execute(JObject args)
        {
            var result = PrefabFactory.Instantiate(args);
            if (!result.Ok) return ToolResult.Error("OP_FAILED", result.Error);

            return ToolResult.Ok(result.Summary, SceneAuthoringSupport.TargetResult(result.InstanceID, result.Data));
        }
    }
}
