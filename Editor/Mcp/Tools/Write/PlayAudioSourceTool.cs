using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class PlayAudioSourceTool : IMcpTool
    {
        public string Name => "play_audio_source";
        public string Description =>
            "Play Mode only. Drives an AudioSource: play, stop, pause, unpause/resume, or playOneShot. " +
            "playOneShot also takes `clip` (GUID/Assets path) and optional non-negative `volumeScale`.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "play", "stop", "pause", "unpause", "resume", "playOneShot" }, ["default"] = "play" },
                ["clip"] = new JObject { ["type"] = "string" },
                ["volumeScale"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 1 },
            },
            ["required"] = new JArray { "instanceId" },
            ["oneOf"] = new JArray
            {
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["instanceId"] = new JObject { ["type"] = "integer" },
                    },
                    ["required"] = new JArray { "instanceId" },
                    ["additionalProperties"] = false,
                },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["instanceId"] = new JObject { ["type"] = "integer" },
                        ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "play", "stop", "pause", "unpause", "resume" } },
                    },
                    ["required"] = new JArray { "instanceId", "action" },
                    ["additionalProperties"] = false,
                },
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["instanceId"] = new JObject { ["type"] = "integer" },
                        ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "playOneShot" } },
                        ["clip"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                        ["volumeScale"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                    },
                    ["required"] = new JArray { "instanceId", "action", "clip" },
                    ["additionalProperties"] = false,
                },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => RuntimeDescriptor(Name, false);
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.PlayAudio(args);
            if (r.Ok) return ToolResult.Ok(r.Summary, r.Data);
            return r.Error != null && r.Error.StartsWith("PLAY_MODE_REQUIRED:")
                ? ToolResult.Error("PLAY_MODE_REQUIRED", r.Error.Substring("PLAY_MODE_REQUIRED:".Length).Trim())
                : ToolResult.Error("OP_FAILED", r.Error);
        }

        private static ToolDescriptor RuntimeDescriptor(string name, bool idempotent) => new ToolDescriptor
        {
            Name = name,
            Category = "audio",
            Capabilities = McpCapability.RuntimeCommand,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.PlayModeOnly,
            IsIdempotent = idempotent,
        };
    }
}
