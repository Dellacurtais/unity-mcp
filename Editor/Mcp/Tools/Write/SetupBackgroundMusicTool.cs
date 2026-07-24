using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetupBackgroundMusicTool : IMcpTool
    {
        public string Name => "setup_background_music";
        public string Description =>
            "Convenience: creates or reuses a singleton GameObject named '__BGM__' with a 2D, looping, top-priority AudioSource playing the given clip. " +
            "When `replaceExisting=true` (default), stops any prior BGM source first. Optional `mixerGroup` routes through your audio mixer (e.g. 'Music').";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clipGuidOrPath"] = new JObject { ["type"] = "string" },
                ["volume"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 0.5 },
                ["pitch"] = new JObject { ["type"] = "number", ["minimum"] = -3, ["maximum"] = 3, ["default"] = 1.0 },
                ["mixerGroup"] = new JObject { ["type"] = "string" },
                ["mixerGuidOrPath"] = new JObject { ["type"] = "string" },
                ["replaceExisting"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "clipGuidOrPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.SetupBackgroundMusic(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data ?? new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
