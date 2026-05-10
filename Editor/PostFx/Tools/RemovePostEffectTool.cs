using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.PostFx.Tools
{
    public sealed class RemovePostEffectTool : IMcpTool
    {
        public string Name => "remove_post_effect";
        public string Description =>
            "Removes a VolumeComponent (Bloom, Vignette, ColorAdjustments, etc.) from a Volume's profile. Errors if the effect isn't currently present. Pair with list_post_effects to see what's there.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["volumeInstanceId"] = new JObject { ["type"] = "integer" },
                ["effect"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "volumeInstanceId", "effect" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = PostEffectEditor.RemoveEffect(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary);
        }
    }
}
