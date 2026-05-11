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

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "exit_play_mode requires async dispatch — framework should call ExecuteAsync.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            double timeoutSeconds = (double?)args["timeoutSeconds"] ?? 10.0;

            bool alreadyEdit = await MainThreadDispatcher.RunAsync(() =>
                !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode);
            if (alreadyEdit)
            {
                return ToolResult.Ok("already in edit mode (no-op)",
                    new JObject { ["alreadyEditMode"] = true });
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
