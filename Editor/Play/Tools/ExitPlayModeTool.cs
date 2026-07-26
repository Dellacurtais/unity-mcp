using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class ExitPlayModeTool : IMcpTool
    {
        public string Name => "exit_play_mode";
        public string Description =>
            "Exits Unity play mode and waits for the editor to settle in edit mode. " +
            "No-op if already in edit mode. Unity discards the play-mode Undo stack on exit, so any runtime mutations are gone.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["default"] = 10, ["minimum"] = 1, ["maximum"] = 60 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;
        public JObject OutputSchema => PlayToolSchemas.Exit;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "exit_play_mode requires async dispatch — framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            double timeoutSeconds = (double?)args["timeoutSeconds"] ?? 10.0;
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 60)
                return ToolResult.Error("BAD_TIMEOUT", "timeoutSeconds must be finite and between 1 and 60");

            var state = await MainThreadDispatcher.RunAsync(() => new
            {
                EditorApplication.isPlaying,
                EditorApplication.isPlayingOrWillChangePlaymode,
            });
            bool alreadyEdit = !state.isPlaying && !state.isPlayingOrWillChangePlaymode;
            if (state.isPlaying != state.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("TRANSITIONING", "a play-mode transition is already in flight; wait for it to settle before exiting");
            if (alreadyEdit)
            {
                return ToolResult.Ok("already in edit mode (no-op)",
                    new JObject { ["entered"] = false, ["alreadyEditMode"] = true });
            }
            await MainThreadDispatcher.RunAsync(() => EditorApplication.ExitPlaymode());

            bool entered = await PlayModeWaiter.WaitFor(PlayModeStateChange.EnteredEditMode, timeoutSeconds);
            if (!entered)
            {
                return ToolResult.Error("TIMEOUT",
                    $"editor did not return to edit mode within {timeoutSeconds}s.");
            }
            return ToolResult.Ok("exited play mode — back in edit mode",
                new JObject { ["entered"] = false });
        }
    }
}
