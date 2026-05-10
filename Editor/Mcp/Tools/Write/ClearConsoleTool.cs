using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ClearConsoleTool : IMcpTool
    {
        public string Name => "clear_console";
        public string Description =>
            "Clears the captured Console buffer. With clearUnityConsole=true also clears Unity's own Console window via internal API (UnityEditor.LogEntries.Clear). " +
            "Useful as a marker before a complex operation: clear → run ops → read_console (sees only what happened during the op).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clearUnityConsole"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var alsoUnity = (bool?)args["clearUnityConsole"] ?? false;
            var removed = ConsoleCapture.Clear(alsoUnity);
            return ToolResult.Ok(
                $"cleared {removed} buffered entries{(alsoUnity ? " and Unity Console" : "")}. " +
                $"Next captured entry will be seq #{ConsoleCapture.CurrentSeq}.");
        }
    }
}
