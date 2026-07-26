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

        public bool IsWrite => true;
        public JObject OutputSchema => PlayToolSchemas.TimeScale;

        private static bool _resetSubscriberInstalled;

        public ToolResult Execute(JObject args)
        {
            if (args["value"] == null)
                return ToolResult.Error("BAD_REQUEST", "missing required number 'value'");
            if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("TRANSITIONING", "wait for the play-mode transition to settle");
            if (!EditorApplication.isPlaying)
                return ToolResult.Error("NOT_PLAYING", "set_time_scale only works during play mode — call enter_play_mode first.");
            var numeric = (double)args["value"];
            if (double.IsNaN(numeric) || double.IsInfinity(numeric) || numeric < 0 || numeric > 100)
                return ToolResult.Error("BAD_VALUE", "value must be finite and between 0 and 100");
            float value = (float)numeric;

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
                if (change != PlayModeStateChange.ExitingPlayMode) return;
                EditorApplication.playModeStateChanged -= handler;
                _resetSubscriberInstalled = false;
                if (!Mathf.Approximately(Time.timeScale, 1f)) Time.timeScale = 1f;
            };
            EditorApplication.playModeStateChanged += handler;
        }
    }
}
