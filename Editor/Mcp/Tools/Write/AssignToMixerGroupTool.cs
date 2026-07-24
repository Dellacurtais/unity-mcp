using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AssignToMixerGroupTool : IMcpTool
    {
        public string Name => "assign_to_mixer_group";
        public string Description =>
            "Routes an AudioSource through a mixer group. mixerGroup accepts the group name (e.g. 'SFX') or 'MixerName/GroupName' (e.g. 'MainMixer/Music'). When ambiguous, pass mixerGuidOrPath to disambiguate.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["mixerGroup"] = new JObject { ["type"] = "string" },
                ["mixerGuidOrPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "instanceId", "mixerGroup" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.AssignToMixerGroup(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data);
        }
    }
}
