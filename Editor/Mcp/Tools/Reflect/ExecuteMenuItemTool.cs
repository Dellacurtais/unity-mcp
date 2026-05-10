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
                    ["description"] = "Top-level menu path, e.g. 'GameObject/Create Empty'.",
                },
                ["verifyEnabledFirst"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = true,
                    ["description"] = "Call Menu.GetEnabled first; if false, return MENU_DISABLED_OR_MISSING without invoking.",
                },
            },
            ["required"] = new JArray { "menuPath" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var menuPath = (string)args["menuPath"];
            if (string.IsNullOrEmpty(menuPath))
                return ToolResult.Error("BAD_ARGS", "menuPath is required");
            bool verify = (bool?)args["verifyEnabledFirst"] ?? true;

            bool enabled = Menu.GetEnabled(menuPath);
            if (verify && !enabled)
            {
                return ToolResult.Error("MENU_DISABLED_OR_MISSING",
                    $"Menu '{menuPath}' is disabled or does not exist (Unity's API conflates both — cross-check via list_menu_items).",
                    new JObject { ["menuPath"] = menuPath, ["wasEnabled"] = false });
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
    }
}
