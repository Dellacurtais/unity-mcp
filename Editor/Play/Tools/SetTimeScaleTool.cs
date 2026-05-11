using System;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class SetTimeScaleTool : IMcpTool
    {
        public string Name => "set_time_scale";
        public string Description =>
            "Sets Time.timeScale (0 = frozen, 1 = normal, 2 = double speed, etc.). " +
            "Useful for slow-mo inspection or fast-forwarding through boring intros. " +
            "Time.timeScale persists across play-mode exit (long-standing Unity quirk) so this tool installs a one-shot subscriber that restores 1.0 on ExitingPlayMode — guards against leaving the editor stuck at slow speed.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["value"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 100 },
            },
            ["required"] = new JArray { "value" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        private static bool _resetSubscriberInstalled;

        public ToolResult Execute(JObject args)
        {
            if (args["value"] == null)
                return ToolResult.Error("BAD_REQUEST", "missing required number 'value'");
            float value = Mathf.Clamp((float)(double)args["value"], 0f, 100f);

            float previous = Time.timeScale;
            Time.timeScale = value;

            EnsureResetSubscriber();

            return ToolResult.Ok(
                $"Time.timeScale {previous:F2} -> {value:F2}",
                new JObject
                {
                    ["previous"] = previous,
                    ["current"] = value,
                    ["resetOnExit"] = true,
                });
        }

        private static void EnsureResetSubscriber()
        {
            if (_resetSubscriberInstalled) return;
            _resetSubscriberInstalled = true;

            Action<PlayModeStateChange> handler = null;
            handler = (change) =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode)
                {
                    // Reset only if we actually tampered (i.e. not at 1.0). The
                    // user might have set it manually in the inspector to 1.0 —
                    // that's already the safe state, no-op.
                    if (!Mathf.Approximately(Time.timeScale, 1f))
                    {
                        Time.timeScale = 1f;
                    }
                }
            };
            EditorApplication.playModeStateChanged += handler;
            // Subscription is process-lived; no Unsubscribe needed. Static
            // field gets reset on assembly reload, so we'll re-install once
            // after each reload — idempotent.
        }
    }
}
