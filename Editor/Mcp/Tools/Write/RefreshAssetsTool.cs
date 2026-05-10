using System;
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
            "When `wait: true` (default), polls until `EditorApplication.isCompiling` flips false (or `timeoutSeconds` is hit). " +
            "Even after this returns, the assembly reload may still be in flight; if `add_component` still fails, retry once after a short delay.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["wait"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "Block until compilation finishes (or timeout). Editor UI may briefly freeze." },
                ["timeoutSeconds"] = new JObject { ["type"] = "number", ["default"] = 30 },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var wait = (bool?)args["wait"] ?? true;
            var timeoutSeconds = (float?)args["timeoutSeconds"] ?? 30f;

            AssetDatabase.Refresh();

            if (!wait)
                return ToolResult.Ok(EditorApplication.isCompiling
                    ? "AssetDatabase.Refresh() requested; compilation in progress (call again with wait=true to block)"
                    : "AssetDatabase.Refresh() requested (no compilation triggered)");

            // Compilation runs in worker processes; isCompiling flips from native
            // even when the main thread is blocked. The editor UI freezes briefly
            // — acceptable trade-off for a deterministic post-script-creation step.
            var start = EditorApplication.timeSinceStartup;
            while (EditorApplication.isCompiling)
            {
                if (EditorApplication.timeSinceStartup - start > timeoutSeconds)
                    return ToolResult.Error("TIMEOUT",
                        $"compilation still running after {timeoutSeconds}s — check Console for compile errors");
                System.Threading.Thread.Sleep(100);
            }
            var elapsed = EditorApplication.timeSinceStartup - start;
            return ToolResult.Ok(
                $"refresh + compile completed in {elapsed:F1}s. " +
                "Note: assembly reload may still be queued — if add_component fails immediately after this, retry once.");
        }
    }
}
