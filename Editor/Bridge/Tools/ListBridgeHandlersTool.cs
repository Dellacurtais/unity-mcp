using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Bridge.Tools
{
    public sealed class ListBridgeHandlersTool : IMcpTool
    {
        public string Name => "list_bridge_handlers";
        public string Description =>
            "Returns the names of all handlers registered with McpCommandBridge.Register(name, ...). " +
            "Use to discover what commands the running game has wired up before calling send_runtime_commands. " +
            "An empty list means the game's MonoBehaviours haven't registered anything yet — typically because Start() hasn't run (not in play mode) or no one calls Register().";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;
        public JObject OutputSchema => BridgeToolSchemas.Handlers;

        public ToolResult Execute(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return ToolResult.Error("NOT_PLAYING", "bridge handlers only exist in Play Mode");
            var inst = McpCommandBridge.Instance;
            if (inst == null)
                return ToolResult.Error("BRIDGE_NOT_INSTALLED",
                    "no McpCommandBridge in the active scene — call bridge_install first.");

            var arr = new JArray();
            foreach (var n in inst.RegisteredHandlerNames) arr.Add(n);

            return ToolResult.Ok($"{arr.Count} handler(s) registered",
                new JObject { ["handlers"] = arr });
        }
    }
}
