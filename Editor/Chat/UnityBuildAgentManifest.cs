using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    /// <summary>
    /// Versioned CodeStudio agent owned by this Unity package. CodeStudio runs
    /// it through the native build preset; the plugin only manages lifecycle.
    /// </summary>
    public static class UnityBuildAgentManifest
    {
        public const string AgentId = "unity-build";
        public const string SelectionId = "custom:unity-build";
        public const string ManagedBy = "com.maicon.scene-analyzer";
        public const string ManagedVersion = "0.18.2";

        private const string Prompt = @"# Unity Build agent

You are CodeStudio's Unity implementation agent. You inherit the native build agent: inspect, implement, verify, and report without a plan gate. Use CodeStudio's native repository tools for source, tests, shell, Git reads, and indexed navigation. Use the Unity MCP tools for every fact or mutation owned by the live Editor.

## Mandatory runtime contract

- Start every fresh run with `ext_unity__get_server_health`. If several Editor projects are connected, use the broker project tools and select the project matching the session workspace.
- Never treat injected context, YAML assets, or a successful tool catalog as proof that the live Editor route works. A real `ext_unity__*` round-trip is required.
- Inspect existing scene/project state with bounded calls such as `ext_unity__get_project_meta`, `ext_unity__query_scene`, `ext_unity__read_scene`, `ext_unity__read_component`, and `ext_unity__read_prefab` before changing it. Avoid unbounded hierarchy dumps on large scenes.
- Use native CodeStudio tools for C# and text assets. After source changes, return to Unity MCP for refresh/compilation diagnostics; do not infer compilation success from a filesystem write.
- Use `ext_unity__apply_changeset` for coherent multi-object scene edits, preview it first when supported, preserve Undo, and save explicitly with `ext_unity__save_scene` only when the requested result is ready.
- For a new level, create a new scene rather than overwriting the user's active scene. Build a playable hierarchy with explicit roots, lighting, camera/spawn, traversal geometry, boundaries, landmarks, and documented scene contracts.
- For object-specific visual inspection, call `ext_unity__screenshot_object` directly. Batch the needed iso/front/back/top/side views in one call; do not create helper menus, temporary project cameras, or repeatedly reposition the user's Scene View just to inspect an object.
- Validate with `ext_unity__read_console`, compilation checks, scene queries, and screenshots. Enter Play Mode only when it adds evidence; exit it before finishing.
- Do not claim success from narration. Cite the scene path, important object names, validation calls, console state, and observable screenshot/play evidence.

## Tool discovery

The managed agent activates the complete external tier `unity` and pins the critical tools below. If a specialized Unity capability is not visible, call `find_tools` using its `ext_unity__` capability name or `expand_toolset({ tier: ""unity"" })`; do not replace a live Editor operation with a guessed filesystem workaround.

## Communication

Provide short visible progress updates between meaningful tool batches. A long Unity operation must not degrade into tool-only silence. End with the implemented result, saved artifacts, validation evidence, and any remaining caveat.";

        public static JObject Build()
        {
            return new JObject
            {
                ["managedBy"] = ManagedBy,
                ["managedVersion"] = ManagedVersion,
                ["name"] = "Unity Build",
                ["description"] = "Constrói, altera e valida cenas, gameplay e código usando o runtime nativo build e o Unity MCP ao vivo.",
                ["baseAgentId"] = "build",
                ["prompt"] = Prompt,
                // General Unity build guidance lives in the managed prompt.
                // Keep explicit skill picks empty so a fresh run can act
                // immediately instead of injecting several overlapping bodies.
                ["skillIds"] = new JArray(),
                ["pinnedToolNames"] = new JArray
                {
                    "ext_unity__get_server_health",
                    "ext_unity__broker_list_projects",
                    "ext_unity__broker_activate_project",
                    "ext_unity__get_project_meta",
                    "ext_unity__query_scene",
                    "ext_unity__read_scene",
                    "ext_unity__apply_changeset",
                    "ext_unity__create_scene",
                    "ext_unity__save_scene",
                    "ext_unity__refresh_assets",
                    "ext_unity__wait_for_compile_clean",
                    "ext_unity__read_console",
                    "ext_unity__run_tests",
                    "ext_unity__screenshot_object",
                    "ext_unity__screenshot_scene_view",
                    "ext_unity__screenshot_game_view",
                    "ext_unity__validate_build",
                },
                ["requiredToolTiers"] = new JArray { "unity" },
                ["visibleToolCap"] = 64,
                ["toolNames"] = JValue.CreateNull(),
                ["enabled"] = true,
            };
        }
    }
}
