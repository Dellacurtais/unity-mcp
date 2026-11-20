using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Bridge.Tools
{
    public sealed class BridgeStatusTool : IMcpTool
    {
        public string Name => "bridge_status";
        public string Description =>
            "Reports whether McpCommandBridge is installed in the scene, whether its runtime instance is active, registered handlers, queue depth and last dispatch.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;
        public JObject OutputSchema => BridgeToolSchemas.Status;

        public ToolResult Execute(JObject args)
        {
            var inst = McpCommandBridge.Instance;
            var installedBridges = UnityObjectIds.FindAll<McpCommandBridge>(FindObjectsInactive.Include);
            if (installedBridges.Length > 1)
                return ToolResult.Error("BRIDGE_DUPLICATES", $"found {installedBridges.Length} McpCommandBridge components; keep exactly one");
            var installed = installedBridges.Length == 1 ? installedBridges[0] : inst;
            if (installed == null)
            {
                return ToolResult.Ok("bridge NOT installed — call bridge_install in Edit Mode", new JObject
                {
                    ["installed"] = false, ["runtimeActive"] = false, ["sceneObjectInstanceId"] = JValue.CreateNull(),
                    ["registeredHandlers"] = new JArray(), ["queuedScripts"] = 0, ["activeScripts"] = 0,
                    ["runningScriptId"] = 0, ["lastDispatch"] = JValue.CreateNull(),
                });
            }
            if (inst == null)
            {
                return ToolResult.Ok("bridge installed in the scene; enter Play Mode to activate runtime handlers", new JObject
                {
                    ["installed"] = true, ["runtimeActive"] = false, ["sceneObjectInstanceId"] = installed.gameObject.GetMcpInstanceId(),
                    ["registeredHandlers"] = new JArray(), ["queuedScripts"] = 0, ["activeScripts"] = 0,
                    ["runningScriptId"] = 0, ["lastDispatch"] = JValue.CreateNull(),
                });
            }

            var handlers = new JArray(inst.RegisteredHandlerNames);
            var last = inst.Last;
            JToken lastDispatch = last.LastStepName == null
                ? (JToken)JValue.CreateNull()
                : new JObject
                {
                    ["stepName"] = last.LastStepName, ["frame"] = last.LastFrameCount,
                    ["ok"] = last.LastOk, ["error"] = last.LastError,
                    ["scriptId"] = last.LastScriptId, ["scriptStatus"] = last.LastScriptStatus.ToString(),
                };

            return ToolResult.Ok(
                $"bridge active; {handlers.Count} handler(s) registered; {inst.QueuedScripts} script(s) queued",
                new JObject
                {
                    ["installed"] = true, ["runtimeActive"] = true,
                    ["sceneObjectInstanceId"] = inst.gameObject.GetMcpInstanceId(),
                    ["registeredHandlers"] = handlers, ["queuedScripts"] = inst.QueuedScripts,
                    ["activeScripts"] = inst.ActiveScripts, ["runningScriptId"] = inst.RunningScriptId,
                    ["maxQueuedScripts"] = inst.maxQueuedScripts,
                    ["defaultHandlerTimeoutFrames"] = inst.defaultHandlerTimeoutFrames,
                    ["defaultHandlerTimeoutSeconds"] = inst.defaultHandlerTimeoutSeconds,
                    ["lastDispatch"] = lastDispatch,
                });
        }
    }
}
