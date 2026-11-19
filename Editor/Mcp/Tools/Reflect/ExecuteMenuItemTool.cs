using System;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect
{
    /// Wraps EditorApplication.ExecuteMenuItem(path). Covers Unity built-in
    /// menus (which list_menu_items cannot enumerate, since they live in C++)
    /// AND every user-defined or third-party [MenuItem]. Powerful: an arbitrary
    /// menu can do anything the editor can — gated by Settings.AllowWrites.
    public sealed class ExecuteMenuItemTool : IMcpTool
    {
        public string Name => "execute_menu_item";

        public string Description =>
            "Invokes UnityEditor.EditorApplication.ExecuteMenuItem(menuPath). Examples: " +
            "'GameObject/Create Empty', 'GameObject/3D Object/Cube', 'Tools/MyBuild/Bake All', " +
            "'Window/Probuilder'. CAVEATS: (1) Unity's API returns false for both 'menu not " +
            "found' and 'menu found but disabled' — set verifyEnabledFirst=true (default) to " +
            "fail-fast via Menu.GetEnabled before invocation. (2) Menu items that open MODAL " +
            "dialogs (Build Settings, File/Save..., Player Settings) BLOCK the editor — only " +
            "call dialog-free menu paths. (3) Effects of coroutine-based menus are not awaited; " +
            "follow up with read tools to verify. (4) Requires AllowWrites=true.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["menuPath"] = new JObject
                {
                    ["type"] = "string",
                    ["minLength"] = 3,
                    ["maxLength"] = 1024,
                    ["description"] = "Exact slash-delimited menu command path, e.g. 'GameObject/Create Empty'.",
                },
                ["verifyEnabledFirst"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = true,
                    ["description"] = "Call Menu.GetEnabled first; if false, return MENU_DISABLED_OR_MISSING without invoking.",
                },
                ["deferred"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "Run the menu as a job after this call has answered, for menus that block the Editor " +
                        "for long (player builds, bakes). Returns a jobId at once; the job completes when the menu returned " +
                        "and the Editor is idle again (no build, compilation or import). Follow it with wait_tool_job.",
                },
            },
            ["required"] = new JArray { "menuPath" },
            ["additionalProperties"] = false,
        };

        // Either the immediate result (menuPath/wasEnabled/executed) or, with deferred=true,
        // the job record (jobId/status/...).
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["menuPath"] = new JObject { ["type"] = "string" },
                ["wasEnabled"] = new JObject { ["type"] = "boolean" },
                ["executed"] = new JObject { ["type"] = "boolean" },
                ["jobId"] = new JObject { ["type"] = "string" },
                ["status"] = new JObject { ["type"] = "string" },
            },
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            args = args ?? new JObject();
            var menuPathToken = args["menuPath"];
            if (menuPathToken == null || menuPathToken.Type != JTokenType.String)
                return ToolResult.Error("BAD_ARGS", "menuPath must be an exact slash-delimited string");
            var menuPath = ((string)menuPathToken).Trim();
            if (args["verifyEnabledFirst"] != null && args["verifyEnabledFirst"].Type != JTokenType.Boolean)
                return ToolResult.Error("BAD_ARGS", "verifyEnabledFirst must be boolean");
            if (!ReflectionPolicy.ValidateMenuPath(menuPath, out var policyError))
                return ToolResult.Error("MENU_BLOCKED", policyError,
                    new JObject { ["menuPath"] = menuPath });
            bool verify = (bool?)args["verifyEnabledFirst"] ?? true;

            bool enabled = Menu.GetEnabled(menuPath);
            if (verify && !enabled)
            {
                return ToolResult.Error("MENU_DISABLED_OR_MISSING",
                    $"Menu '{menuPath}' is disabled or does not exist (Unity's API conflates both — cross-check via list_menu_items).",
                    new JObject { ["menuPath"] = menuPath, ["wasEnabled"] = false });
            }

            if (args["deferred"] != null && args["deferred"].Type != JTokenType.Boolean)
                return ToolResult.Error("BAD_ARGS", "deferred must be boolean");
            if ((bool?)args["deferred"] ?? false)
            {
                // Running a build inline froze the main thread before this call could answer,
                // so the client timed out. As a job it starts only after the answer is out.
                var job = ToolJobManager.Create("menu", new JObject { ["menuPath"] = menuPath });
                ToolJobManager.Start(job, (record, token) => RunDeferredAsync(record, menuPath, token));
                Bootstrap.RecordEntry($"[mcp] execute_menu_item '{menuPath}' deferred as job {job.Id}");
                var queued = job.ToJson();
                queued["menuPath"] = menuPath;
                queued["wasEnabled"] = enabled;
                return ToolResult.Ok($"menu '{menuPath}' queued as job {job.Id}; follow it with wait_tool_job", queued);
            }

            Bootstrap.RecordEntry($"[mcp] execute_menu_item '{menuPath}'");
            bool ok = EditorApplication.ExecuteMenuItem(menuPath);

            var payload = new JObject
            {
                ["menuPath"] = menuPath,
                ["wasEnabled"] = enabled,
                ["executed"] = ok,
            };

            if (!ok)
            {
                return ToolResult.Error("MENU_NOT_EXECUTED",
                    $"ExecuteMenuItem returned false for '{menuPath}' (handler refused, disabled at invocation, or path not registered).",
                    payload);
            }

            return ToolResult.Ok($"executed '{menuPath}'", payload);
        }

        /// Runs after the job got the Editor write gate (so after the MCP call that created it
        /// released it), one more half second later so the answer has left the Editor. The job
        /// ends once the menu returned and the Editor stayed idle for two seconds, which covers
        /// menus that schedule their real work (a build) with delayCall.
        private static async Task<JToken> RunDeferredAsync(ToolJobRecord record, string menuPath, CancellationToken token)
        {
            var finished = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            await MainThreadDispatcher.RunAsync(() =>
            {
                var runAt = EditorApplication.timeSinceStartup + 0.5;
                EditorApplication.CallbackFunction start = null;
                start = () =>
                {
                    if (EditorApplication.timeSinceStartup < runAt) return;
                    EditorApplication.update -= start;
                    if (token.IsCancellationRequested) { finished.TrySetCanceled(); return; }
                    ToolJobManager.Report(record, 0.2, "menu running");
                    var startedAt = EditorApplication.timeSinceStartup;
                    bool executed;
                    try { executed = EditorApplication.ExecuteMenuItem(menuPath); }
                    catch (Exception exception) { finished.TrySetException(exception); return; }
                    var menuSeconds = EditorApplication.timeSinceStartup - startedAt;
                    var idleSince = -1d;
                    EditorApplication.CallbackFunction settle = null;
                    settle = () =>
                    {
                        var now = EditorApplication.timeSinceStartup;
                        if (BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling || EditorApplication.isUpdating)
                        {
                            idleSince = -1d;
                            ToolJobManager.Report(record, 0.6, "Editor busy with work the menu started");
                            return;
                        }
                        if (idleSince < 0d) { idleSince = now; return; }
                        if (now - idleSince < 2d) return;
                        EditorApplication.update -= settle;
                        finished.TrySetResult(new JObject
                        {
                            ["menuPath"] = menuPath,
                            ["executed"] = executed,
                            ["menuSeconds"] = Math.Round(menuSeconds, 2),
                            ["totalSeconds"] = Math.Round(now - startedAt, 2),
                        });
                    };
                    EditorApplication.update += settle;
                };
                EditorApplication.update += start;
            }, token);
            JObject result;
            using (token.Register(() => finished.TrySetCanceled()))
                result = await finished.Task;
            if (!(bool)result["executed"])
                throw new ToolJobFailureException(
                    $"ExecuteMenuItem returned false for '{menuPath}' (handler refused, disabled at invocation, or path not registered).",
                    result);
            return result;
        }
    }
}
