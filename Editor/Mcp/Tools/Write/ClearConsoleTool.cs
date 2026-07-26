using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ClearConsoleTool : IMcpTool
    {
        public string Name => "clear_console";
        public string Description =>
            "Clears the live capture buffer and optionally Unity's internal Console. " +
            "The structured result reports whether the internal reflection call actually succeeded.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["clearUnityConsole"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["bufferEntriesRemoved"] = new JObject { ["type"] = "integer" },
                ["unityConsoleRequested"] = new JObject { ["type"] = "boolean" },
                ["unityConsoleAvailable"] = new JObject { ["type"] = "boolean" },
                ["unityConsoleCleared"] = new JObject { ["type"] = "boolean" },
                ["bufferDomainId"] = new JObject { ["type"] = "string" },
                ["nextSeq"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "bufferEntriesRemoved", "unityConsoleRequested", "unityConsoleAvailable", "unityConsoleCleared", "bufferDomainId", "nextSeq" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var requested = (bool?)args["clearUnityConsole"] ?? false;
            var removed = ConsoleCapture.Clear(requested, out var unityConsoleCleared);
            var payload = new JObject
            {
                ["bufferEntriesRemoved"] = removed,
                ["unityConsoleRequested"] = requested,
                ["unityConsoleAvailable"] = UnityConsoleReader.IsAvailable,
                ["unityConsoleCleared"] = unityConsoleCleared,
                ["bufferDomainId"] = ConsoleCapture.DomainId,
                ["nextSeq"] = ConsoleCapture.CurrentSeq,
            };
            if (requested && !unityConsoleCleared)
                return ToolResult.Error("UNITY_CONSOLE_CLEAR_UNAVAILABLE", "live buffer was cleared, but Unity's internal Console could not be cleared", payload);
            return ToolResult.Ok($"cleared {removed} buffered entr{(removed == 1 ? "y" : "ies")}", payload);
        }
    }
}
