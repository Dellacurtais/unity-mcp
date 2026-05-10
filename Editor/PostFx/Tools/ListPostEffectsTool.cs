using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.PostFx.Tools
{
    public sealed class ListPostEffectsTool : IMcpTool
    {
        public string Name => "list_post_effects";
        public string Description =>
            "Two modes. With `volumeInstanceId`: lists effects currently on that Volume's profile (with active-override count per effect). With `available: true` (no volume needed): enumerates every VolumeComponent type loaded in the editor across URP/HDRP/SRP-Core — discovery before add_post_effect.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["volumeInstanceId"] = new JObject { ["type"] = "integer" },
                ["available"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            if ((bool?)args["available"] == true)
            {
                var arr = new JArray();
                int count = 0;
                foreach (var name in PostEffectEditor.ListAvailableEffectTypes())
                {
                    arr.Add(name);
                    count++;
                }
                return ToolResult.Ok($"{count} VolumeComponent type(s) available",
                    new JObject { ["count"] = count, ["types"] = arr });
            }

            // List effects on a specific volume
            if (args["volumeInstanceId"] == null || args["volumeInstanceId"].Type == JTokenType.Null)
                return ToolResult.Error("BAD_ARGS",
                    "either pass available:true or volumeInstanceId");
            var r = PostEffectEditor.ListEffects(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary);
        }
    }
}
