using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Bridge;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Bridge.Tools
{
    public sealed class SendRuntimeCommandsTool : IMcpTool
    {
        public string Name => "send_runtime_commands";
        public string Description =>
            "Runs a deterministic timeline of named bridge commands in a single round-trip. Each step optionally captures a screenshot after the command (waits for end-of-frame), and/or marks a console log anchor before, with the post-step delta returned alongside. " +
            "Use this instead of one-tool-per-action: monte uma jogada inteira (move, jump, wait 30 frames, shoot, screenshot) e mande uma vez só — você recebe todos os PNGs + logs + per-step ok/error agregados. " +
            "Steps run sequentially; delayFrames waits the specified number of frames after the previous step before invoking the next. With stopOnError, the timeline stops at the first failure (default true).";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["commands"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["name"] = new JObject { ["type"] = "string" },
                            ["payload"] = new JObject(),
                            ["delayFrames"] = new JObject { ["type"] = "integer", ["default"] = 0, ["minimum"] = 0, ["maximum"] = 6000 },
                            ["captureScreenshotAfter"] = new JObject { ["type"] = "boolean", ["default"] = false },
                            ["markLogAnchorBefore"] = new JObject { ["type"] = "boolean", ["default"] = false },
                            ["timeoutFrames"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 36000 },
                            ["timeoutSeconds"] = new JObject { ["type"] = "number", ["minimum"] = 0.1, ["maximum"] = 300 },
                        },
                        ["required"] = new JArray { "name" },
                        ["additionalProperties"] = false,
                    },
                    ["minItems"] = 1,
                    ["maxItems"] = 200,
                },
                ["stopOnError"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["returnPerStepResults"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["default"] = 30, ["minimum"] = 1, ["maximum"] = 300 },
            },
            ["required"] = new JArray { "commands" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => BridgeToolSchemas.Commands;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "send_runtime_commands requires async dispatch — framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var commandsToken = args["commands"] as JArray;
            if (commandsToken == null || commandsToken.Count == 0)
                return ToolResult.Error("BAD_REQUEST", "'commands' must be a non-empty array.");
            if (commandsToken.Count > 200)
                return ToolResult.Error("BAD_REQUEST", "commands supports at most 200 steps.");

            var (ok, err) = await BridgeChannel.PrecheckAsync(requirePlayMode: true);
            if (!ok) return ToolResult.Error(err.Split(':')[0], err);

            bool stopOnError = (bool?)args["stopOnError"] ?? true;
            bool returnPerStepResults = (bool?)args["returnPerStepResults"] ?? true;
            double timeoutSeconds = (double?)args["timeoutSeconds"] ?? 30.0;
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 300)
                return ToolResult.Error("BAD_TIMEOUT", "timeoutSeconds must be finite and between 1 and 300");

            // Anchor the script's log delta start so post-step deltas can be
            // attributed even for steps without markLogAnchorBefore (we always
            // record the final delta).
            int scriptStartSeq = ConsoleCapture.CurrentSeq;

            var steps = new List<BridgeStep>(commandsToken.Count);
            foreach (var c in commandsToken)
            {
                if (!(c is JObject cmdObj))
                    return ToolResult.Error("BAD_REQUEST", "each command must be an object.");
                string name = ((string)cmdObj["name"])?.Trim();
                if (string.IsNullOrEmpty(name))
                    return ToolResult.Error("BAD_REQUEST", "each command needs a 'name'.");
                var delayFrames = (int?)cmdObj["delayFrames"] ?? 0;
                var timeoutFrames = (int?)cmdObj["timeoutFrames"] ?? 0;
                var stepTimeoutSeconds = (double?)cmdObj["timeoutSeconds"] ?? 0.0;
                if (delayFrames < 0 || delayFrames > 6000)
                    return ToolResult.Error("BAD_DELAY", "delayFrames must be between 0 and 6000");
                if (timeoutFrames < 0 || timeoutFrames > 36000)
                    return ToolResult.Error("BAD_STEP_TIMEOUT", "timeoutFrames must be between 1 and 36000 when provided");
                if (double.IsNaN(stepTimeoutSeconds) || double.IsInfinity(stepTimeoutSeconds)
                    || stepTimeoutSeconds < 0 || stepTimeoutSeconds > 300)
                    return ToolResult.Error("BAD_STEP_TIMEOUT", "timeoutSeconds must be finite and between 0.1 and 300 when provided");
                steps.Add(new BridgeStep
                {
                    Name = name,
                    PayloadJson = SerializePayload(cmdObj["payload"]),
                    DelayFrames = delayFrames,
                    CaptureScreenshotAfter = (bool?)cmdObj["captureScreenshotAfter"] ?? false,
                    MarkLogAnchorBefore = (bool?)cmdObj["markLogAnchorBefore"] ?? false,
                    TimeoutFrames = timeoutFrames,
                    TimeoutSeconds = (float)stepTimeoutSeconds,
                });
            }

            var script = new BridgeScript
            {
                Steps = steps,
                StopOnError = stopOnError,
                ReturnPerStepResults = returnPerStepResults,
            };

            try
            {
                var context = ToolExecutionContext.Current;
                await BridgeChannel.SubmitAndAwait(
                    script, timeoutSeconds, context != null ? context.CancellationToken : default);
            }
            catch (System.OperationCanceledException)
            {
                return ToolResult.Error("CANCELLED",
                    $"runtime command script #{script.Id} was cancelled; status={script.Status}.", ScriptState(script));
            }
            catch (TimeoutException te)
            {
                return ToolResult.Error("TIMEOUT", te.Message, ScriptState(script));
            }
            catch (InvalidOperationException ioe)
            {
                return ToolResult.Error("DISPATCH_FAILED", ioe.Message, ScriptState(script));
            }

            // Build aggregate result. Content blocks: a JSON summary text, then
            // each step's screenshot (if any) inline as image blocks.
            int total = script.Results.Count;
            int okCount = 0;
            foreach (var r in script.Results) if (r.Ok) okCount++;

            int scriptEndSeq = ConsoleCapture.CurrentSeq;
            var contentBlocks = new JArray();

            var summary = new JObject
            {
                ["totalSteps"] = steps.Count,
                ["executedSteps"] = total,
                ["okSteps"] = okCount,
                ["stoppedEarly"] = total < steps.Count,
                ["scriptStartSeq"] = scriptStartSeq,
                ["scriptEndSeq"] = scriptEndSeq,
                ["script"] = ScriptState(script),
            };

            JArray stepResultsJson = null;
            if (returnPerStepResults)
            {
                stepResultsJson = new JArray();
                for (int i = 0; i < script.Results.Count; i++)
                {
                    var r = script.Results[i];
                    var item = new JObject
                    {
                        ["index"] = i,
                        ["name"] = r.Name,
                        ["ok"] = r.Ok,
                        ["error"] = r.Error,
                        ["durationMs"] = r.DurationMs,
                        ["frameAtStart"] = r.FrameAtStart,
                        ["frameAtEnd"] = r.FrameAtEnd,
                    };
                    if (r.LogAnchorSeq > 0) item["logAnchorSeq"] = r.LogAnchorSeq;

                    if (r.ScreenshotPng != null && r.ScreenshotPng.Length > 0)
                    {
                        string b64 = Convert.ToBase64String(r.ScreenshotPng);
                        item["screenshotSize"] = new JObject
                        {
                            ["width"] = r.ScreenshotWidth,
                            ["height"] = r.ScreenshotHeight,
                            ["bytes"] = r.ScreenshotPng.Length,
                        };
                        // Append a text label + image block per step so the LLM
                        // can correlate visually. Order matches step index.
                        contentBlocks.Add(new JObject
                        {
                            ["type"] = "text",
                            ["text"] = $"[step #{i} '{r.Name}' — frame {r.FrameAtEnd}]",
                        });
                        contentBlocks.Add(new JObject
                        {
                            ["type"] = "image",
                            ["data"] = b64,
                            ["mimeType"] = "image/png",
                        });
                    }
                    stepResultsJson.Add(item);
                }
                summary["steps"] = stepResultsJson;
            }

            string textHeader =
                $"executed {okCount}/{total} step(s){(total < steps.Count ? $" (stopped early after {total}/{steps.Count})" : "")}; " +
                $"log seq {scriptStartSeq}->{scriptEndSeq}";

            // Prepend the summary text so it's the first block agents see.
            var finalBlocks = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = textHeader }
            };
            foreach (var b in contentBlocks) finalBlocks.Add(b);
            // Append the structured JSON as a final text block so callers that
            // care about machine-readable data find it deterministically last.
            finalBlocks.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = summary.ToString(Newtonsoft.Json.Formatting.None),
            });

            return new ToolResult
            {
                IsError = okCount < total || total < steps.Count,
                Text = textHeader,
                Json = summary,
                ContentBlocks = finalBlocks,
            };
        }

        private static JObject ScriptState(BridgeScript script)
        {
            return new JObject
            {
                ["scriptId"] = script.Id,
                ["status"] = script.Status.ToString(),
                ["cancelRequested"] = script.CancelRequested,
                ["terminalError"] = script.TerminalError,
                ["submittedUtcTicks"] = script.SubmittedUtcTicks,
                ["startedUtcTicks"] = script.StartedUtcTicks,
                ["completedUtcTicks"] = script.CompletedUtcTicks,
                ["completedSteps"] = script.Results.Count,
                ["totalSteps"] = script.Steps.Count,
            };
        }

        private static string SerializePayload(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "";
            if (token.Type == JTokenType.String) return (string)token;
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
