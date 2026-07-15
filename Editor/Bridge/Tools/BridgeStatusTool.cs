using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Bridge.Tools
{
    public sealed class BridgeStatusTool : IMcpTool
    {
        public string Name => "bridge_status";
        public string Description =>
            "Returns whether the McpCommandBridge component is present in the active scene, what handlers the game has registered, and the queue depth. " +
            "Call as a precheck before send_runtime_commands.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var inst = McpCommandBridge.Instance;
            if (inst == null)
            {
                return ToolResult.Ok("bridge NOT installed — call bridge_install",
                    new JObject
                    {
                        ["installed"] = false,
                        ["registeredHandlers"] = new JArray(),
                        ["queuedScripts"] = 0,
                        ["activeScripts"] = 0,
                        ["runningScriptId"] = 0,
                    });
            }

            var handlers = new JArray();
            foreach (var name in inst.RegisteredHandlerNames) handlers.Add(name);

            var last = inst.Last;
            JToken lastDispatch = last.LastStepName == null
                ? (JToken)JValue.CreateNull()
                : new JObject
                {
                    ["stepName"] = last.LastStepName,
                    ["frame"] = last.LastFrameCount,
                    ["ok"] = last.LastOk,
                    ["error"] = last.LastError,
                    ["scriptId"] = last.LastScriptId,
                    ["scriptStatus"] = last.LastScriptStatus.ToString(),
                };

            return ToolResult.Ok(
                $"bridge installed; {handlers.Count} handler(s) registered; {inst.QueuedScripts} script(s) queued",
                new JObject
                {
                    ["installed"] = true,
                    ["sceneObjectInstanceId"] = inst.gameObject.GetInstanceID(),
                    ["registeredHandlers"] = handlers,
                    ["queuedScripts"] = inst.QueuedScripts,
                    ["activeScripts"] = inst.ActiveScripts,
                    ["runningScriptId"] = inst.RunningScriptId,
                    ["maxQueuedScripts"] = inst.maxQueuedScripts,
                    ["defaultHandlerTimeoutFrames"] = inst.defaultHandlerTimeoutFrames,
                    ["defaultHandlerTimeoutSeconds"] = inst.defaultHandlerTimeoutSeconds,
                    ["lastDispatch"] = lastDispatch,
                });
        }
    }
}
