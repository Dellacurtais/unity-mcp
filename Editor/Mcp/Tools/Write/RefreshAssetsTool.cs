using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class RefreshAssetsTool : IMcpTool
    {
        public string Name => "refresh_assets";
        public string Description =>
            "Calls `AssetDatabase.Refresh()` so Unity notices files you just created via the project filesystem (e.g. a `.cs` script written by the agent). " +
            "MUST be called after creating a new MonoBehaviour script and BEFORE `add_component` on its type — otherwise `add_component` returns `unknown component type` because the script hasn't been compiled into an assembly yet. " +
            "AssetDatabase.Refresh imports changed assets synchronously, can trigger asset garbage collection, and starts script compilation asynchronously. " +
            "When `wait: true` (default), polls cooperatively (no main-thread block, other tools keep responding) until `EditorApplication.isCompiling` flips false (or `timeoutSeconds` is hit). " +
            "Even after this returns, the assembly reload may still be in flight; if `add_component` still fails, retry once after a short delay.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["wait"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "Wait for compilation to finish (cooperative — does NOT freeze the editor)." },
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["minimum"] = 0.1, ["maximum"] = 600, ["default"] = 30 },
            },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["refreshed"] = new JObject { ["type"] = "boolean" },
                ["waited"] = new JObject { ["type"] = "boolean" },
                ["compiling"] = new JObject { ["type"] = "boolean" },
                ["elapsedSeconds"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
            },
            ["required"] = new JArray { "refreshed", "waited", "compiling", "elapsedSeconds" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;

        /// Sync fallback exists only to satisfy IMcpTool. The framework calls
        /// ExecuteAsync; if anything ever bypasses it, fail fast with a clear
        /// message instead of silently blocking the dispatcher again.
        public ToolResult Execute(JObject args)
        {
            return ToolResult.Error("INTERNAL_ERROR",
                "refresh_assets requires async dispatch — the framework should call ExecuteAsync. " +
                "If you're seeing this, the MCP server is on a stale code path; reload the editor.");
        }

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var wait = (bool?)args["wait"] ?? true;
            var timeoutSeconds = (float?)args["timeoutSeconds"] ?? 30f;

            // Refresh on main thread (cheap). Capture wall-clock start there too.
            double startTime = await MainThreadDispatcher.RunAsync(() =>
            {
                AssetDatabase.Refresh();
                return EditorApplication.timeSinceStartup;
            });

            if (!wait)
            {
                bool stillCompiling = await MainThreadDispatcher.RunAsync(() => EditorApplication.isCompiling);
                double elapsed = await MainThreadDispatcher.RunAsync(() => EditorApplication.timeSinceStartup) - startTime;
                return ToolResult.Ok(stillCompiling
                    ? "AssetDatabase.Refresh() completed asset import; script compilation is still in progress"
                    : "AssetDatabase.Refresh() completed asset import; no script compilation is currently running", new JObject
                {
                    ["refreshed"] = true,
                    ["waited"] = false,
                    ["compiling"] = stillCompiling,
                    ["elapsedSeconds"] = Math.Max(0d, elapsed),
                });
            }

            // Cooperative wait via EditorApplication.update polling.
            // Crucially: the polling closure runs on the editor main thread but
            // returns immediately each tick, so EditorApplication.update keeps
            // firing and MainThreadDispatcher.Pump keeps draining other tools.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.CallbackFunction onUpdate = null;
            onUpdate = () =>
            {
                if (!EditorApplication.isCompiling)
                {
                    EditorApplication.update -= onUpdate;
                    tcs.TrySetResult(true);
                }
            };

            await MainThreadDispatcher.RunAsync(() =>
            {
                if (!EditorApplication.isCompiling)
                {
                    // No compile triggered — fast-path
                    tcs.TrySetResult(true);
                    return;
                }
                EditorApplication.update += onUpdate;
            });

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (winner != tcs.Task)
            {
                // Timeout — best-effort cleanup of the polling hook.
                await MainThreadDispatcher.RunAsync(() =>
                {
                    EditorApplication.update -= onUpdate;
                });
                return ToolResult.Error("TIMEOUT",
                    $"compilation still running after {timeoutSeconds}s — check Console for compile errors.");
            }

            // Wait one extra delayCall tick so any pending assembly reload has a
            // chance to fire and commit the new types before we return.
            var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await MainThreadDispatcher.RunAsync(() =>
            {
                EditorApplication.delayCall += () => settled.TrySetResult(true);
            });
            await Task.WhenAny(settled.Task, Task.Delay(2000));

            double elapsed = await MainThreadDispatcher.RunAsync(() => EditorApplication.timeSinceStartup) - startTime;
            return ToolResult.Ok($"refresh completed and script compilation is not running after {elapsed:F1}s", new JObject
            {
                ["refreshed"] = true,
                ["waited"] = true,
                ["compiling"] = false,
                ["elapsedSeconds"] = Math.Max(0d, elapsed),
            });
        }
    }
}
