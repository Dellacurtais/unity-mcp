using System.Collections.Generic;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Bridge.Tools
{
    public sealed class SendRuntimeCommandTool : IMcpTool
    {
        public string Name => "send_runtime_command";
        public string Description =>
            "Invokes one named handler registered via McpCommandBridge.Register. " +
            "Singleton form of send_runtime_commands — use it for one-off debug pokes. " +
            "For anything multi-step (e.g. 'jump, wait 30 frames, shoot, then screenshot'), prefer send_runtime_commands which batches into a deterministic timeline in a single round-trip.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string" },
                ["payload"] = new JObject(),
                ["waitForDispatch"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["default"] = 5, ["minimum"] = 0.1, ["maximum"] = 60 },
            },
            ["required"] = new JArray { "name" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "send_runtime_command requires async dispatch — framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            string name = (string)args["name"];
            if (string.IsNullOrEmpty(name))
                return ToolResult.Error("BAD_REQUEST", "'name' is required.");

            var (ok, err) = BridgeChannel.Precheck(requirePlayMode: true);
            if (!ok) return ToolResult.Error(err.Split(':')[0], err);

            string payloadJson = SerializePayload(args["payload"]);
            double timeoutSeconds = (double?)args["timeoutSeconds"] ?? 5.0;

            var script = new BridgeScript
            {
                Steps = new List<BridgeStep>
                {
                    new BridgeStep { Name = name, PayloadJson = payloadJson }
                },
                StopOnError = true,
                ReturnPerStepResults = true,
            };

            try
            {
                await BridgeChannel.SubmitAndAwait(script, timeoutSeconds);
            }
            catch (System.TimeoutException te)
            {
                return ToolResult.Error("TIMEOUT", te.Message);
            }

            if (script.Results.Count == 0)
                return ToolResult.Error("DISPATCH_FAILED", "no result produced (script did not start).");

            var r = script.Results[0];
            if (!r.Ok)
                return ToolResult.Error(r.Error ?? "HANDLER_FAILED",
                    $"handler '{name}' failed: {r.Error}");

            return ToolResult.Ok(
                $"'{name}' ok in {r.DurationMs:F1}ms (frames {r.FrameAtStart}->{r.FrameAtEnd})",
                new JObject
                {
                    ["ok"] = true,
                    ["durationMs"] = r.DurationMs,
                    ["frameAtStart"] = r.FrameAtStart,
                    ["frameAtEnd"] = r.FrameAtEnd,
                });
        }

        private static string SerializePayload(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "";
            if (token.Type == JTokenType.String) return (string)token;
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
