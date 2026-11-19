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
        public const string ManagedVersion = "0.19.0";

        private const string Prompt = @"# Unity Build agent

Work in CodeStudio's native build mode. Inspect, implement, and verify within the user's scope. Use native repository tools for source, Git, and shell work. Use Unity MCP for live Editor state, Editor-owned changes, and Unity validation.

## Tool discovery

Call visible Unity tools directly. For a hidden capability, use `find_tools` with its `ext_unity__` name or a precise operation description, then follow the returned schema. When the tool supports indirect invocation, call it through `code_exec` with `return await tools.<name>(args)`. Use top-level calls for interactive tools and direct screenshot tools for image evidence.

## Connect, inspect, and edit

- Before live Editor work, verify the route with `ext_unity__get_server_health` using `compact: true`. If the broker route is uncertain or several Editors are connected, use `ext_unity__broker_get_status` and `ext_unity__broker_list_projects` to match the exact workspace path. If routing needs to change, pass the matching `projectKey` to `ext_unity__broker_activate_project` and verify the Editor response before editing.
- Inspect the relevant scene, prefab, or component with scoped reads. For `ext_unity__read_hierarchy`, set a scene or root and keep depth, fields, and page size bounded. Pass `nextCursor` as `cursor` with the same filters. Restart the read if the snapshot is stale. Reuse returned stable target references and resolve stale targets again before writing.
- For a coordinated scene edit, call `ext_unity__apply_changeset` with `preview: true`, review the result, then apply. Check the receipt for the outcome and any rollback errors. Create a separate scene for a new level and use `ext_unity__save_scene` explicitly when persistence is in scope and the requested result is ready.

## Compilation, jobs, and recovery

- After Unity source changes, verify compilation before using new types. For refresh across a broker reload, use `ext_unity__broker_start_refresh_compile` and pass the returned `job_id` as `jobId` to `ext_unity__broker_wait_job`. For embedded transport, use `ext_unity__refresh_assets` and `ext_unity__wait_for_compile_clean`. Check the resulting diagnostics.
- If a test, build, or other Editor operation returns an unfinished job, keep its `jobId` and use `ext_unity__wait_tool_job`, or `ext_unity__broker_wait_job` when the broker tracks the job. A bounded wait can return a pending state. Check the terminal result and the actual test counts or build report. Treat `ext_unity__validate_build` as a configuration check.
- On `OUTCOME_UNKNOWN`, a disconnect, or a timeout after dispatching a write, use `ext_unity__broker_get_request` with the returned `requestId` when available. Inspect the receipt and affected Editor state before retrying. Report an indeterminate outcome if the effect cannot be established.

## Mesh cleanup and model scale

- For cleanup, use an existing editable mesh session or create a copy with `ext_unity__mesh_begin_edit_session`. Diagnose with `ext_unity__mesh_analyze_cleanup`, then call `ext_unity__mesh_preview_cleanup` and `ext_unity__mesh_apply_cleanup` with matching revisions and the returned `previewHash`. Remove disconnected fragments only when explicitly identified as unwanted. Pass their component IDs and the diagnosis hash as `expectedMeshHash`.
- For uniform model scale normalization or target sizing, use `ext_unity__model_preview_scale` on the full model root. Apply with `ext_unity__model_apply_scale` using the same settings, the preview's `sourceHash` as `expectedSourceHash`, and its `previewHash`. Validate the returned copy, dimensions, and generated assets. Reinspect and preview again if the source, mesh revision, or hash changes.

## Validation and reporting

Match validation to the task. Use `ext_unity__read_console`, scoped reads, and visual or Play Mode checks as needed. Batch object views with `ext_unity__screenshot_object`. If you entered Play Mode for validation, exit it when finished. Give short progress updates during long work and report the saved paths, validation results, and any unresolved state.";

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
