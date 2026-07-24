using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class AddAudioListenerTool : IMcpTool
    {
        public string Name => "add_audio_listener";
        public string Description =>
            "Adds an AudioListener to a GameObject (typically the main camera). " +
            "removeOthers=true (default) removes AudioListeners from any other GO so Unity doesn't warn about multiple listeners. Idempotent — no-op if the target already has one.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["removeOthers"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = Name,
            Category = "audio",
            Capabilities = McpCapability.SceneWrite | McpCapability.Destructive,
            Risk = McpRiskLevel.High,
            ConfirmationPolicy = McpConfirmationPolicy.Required,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = true,
        };
        public ToolResult Execute(JObject args)
        {
            var r = AudioAuthor.AddListener(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            return ToolResult.Ok(r.Summary, r.Data);
        }
    }
}
