# Changelog

All notable changes to this package will be documented in this file.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.7] - 2026-05-09

### Added

- **Auto-injected Unity context preamble on the first message of every new session.** When the chat client auto-creates a session (because Settings.CoderSessionId == 0), it prepends a short Markdown block to the user's first message describing: the Unity workdir, the full set of `ext_unity_editor__*` MCP tools available, and the two installed skills (`unity-scene-audit`, `unity-scene-author`) with their trigger conditions. The agent can then act immediately without first probing tools/list or guessing skill names — relevant for D:\MCP's name-based skill invocation tool.
- `Settings.CoderAutoInjectContext` (default true) — kill switch.
- `Settings.CoderCustomPreamble` — optional verbatim override of the default preamble.
- Coder Chat panel: new "Context preamble" foldout with the toggle, the override textarea, a Preview-default button (dumps the resolved default into the transcript as a system note), and a Clear-override button.
- `CoderApiClient.BuildDefaultPreamble(unityRoot)` — public helper used by both the runtime injection and the preview button.

### Notes

- Injection runs **only** on the first POST after a session is freshly created. Continuing an existing session (Settings.CoderSessionId > 0) sends the user's message unchanged — the agent already has the prior turns in its history.
- Subsequent runs of `/api/coder/sessions/<N>/messages` aren't bloated with the preamble.

## [0.3.6] - 2026-05-09

### Added

- **Auto-detect / auto-create D:\MCP project** for the Unity workdir. The chat client now calls `GET /api/projects`, matches `root_path` against `Bootstrap.UnityProjectRoot` (= `Path.GetDirectoryName(Application.dataPath)`), and reuses the existing project's id when found. If no match, it `POST /api/projects` with `{ root_path, name, description }` and stores the new id in `Settings.CoderProjectId`. Path comparison is case-insensitive and normalises Windows backslashes vs forward slashes.
- `Bootstrap.UnityProjectRoot` static property (captured at editor load) — safe to read from background tasks.
- "Auto-detect" button in the Coder Chat panel next to the Project field — runs the discover/create flow without sending a message. Clears the field with the "Clear" button if the dev wants to re-trigger detection on next Send.

### Changed

- `Send` flow when `CoderProjectId == 0`: auto-ensure project → auto-create session → POST message. Three calls behind one click; the dev never has to open the dashboard to set up project + session.
- `Send` flow when `CoderProjectId > 0`: respects the explicit id, only auto-creates the session.
- Coder Chat HelpBox updated to describe the auto-detect path.

## [0.3.5] - 2026-05-09

### Fixed

- Coder Chat session creation. Earlier mapping of the D:\MCP coder API was wrong: `POST /api/coder/sessions/:id/messages` does **not** create sessions implicitly — the server returns `Session 0 not found`. Now the chat client calls `POST /api/coder/sessions` first (with `project_id`, `title`, `provider`, optional `model`/`agent`), captures the returned `id`, then sends the message.
- The "New" button now hits the create-session endpoint for real instead of zeroing local state. Auto-creation also kicks in when the user clicks Send with `sessionId == 0`.
- Session text field re-syncs after a successful create/auto-create so the dev sees the assigned id without clicking Refresh.

### Added

- Settings: `CoderProjectId` (default 1, must exist in the dashboard), `CoderProvider` (anthropic/openai/google/ollama), `CoderModel` (optional override).
- Coder Chat panel exposes those new fields under "Coder server" foldout: Project / Provider dropdown / Model text. HelpBox below explains the auto-create flow.

## [0.3.4] - 2026-05-09

### Fixed

- `SchemaHelpers` was declared `internal`, which broke compilation of the secondary `com.maicon.scene-analyzer.Editor.PostFx` asmdef (CS0122) since it lives in a separate assembly that references — but cannot see internals of — the main asmdef. Promoted to `public`. `ReadHelpers`, `NewtonsoftCheck`, and `PostFxBootstrap` stay `internal` because they're only consumed within their own asmdef.

## [0.3.3] - 2026-05-09

### Fixed

- `ParticleTemplates.cs`: `ParticleSystemShapeType.Edge` doesn't exist in Unity (CS0117). Renamed to the correct enum value `ParticleSystemShapeType.SingleSidedEdge` so the `trail` template compiles. Side effect: clears the cascade Burst error `Failed to resolve assembly com.maicon.scene-analyzer.Editor.PostFx` (PostFx depends on main, main wasn't producing a DLL).

## [0.3.2] - 2026-05-09

### Fixed

- Shipped `.meta` files for every folder, script, asmdef, README/CHANGELOG, and `package.json`. UPM treats anything under `Packages/` as immutable — without committed metas, Unity logs `Asset ... has no meta file, but it's in an immutable folder. The asset will be ignored.` for every file (~95 errors) and the plugin's assemblies never compile, so the menu items don't register. With metas in place, the package now imports cleanly via Git URL.

## [0.3.1] - 2026-05-09

### Added

- `com.maicon.scene-analyzer.Editor.Diagnostics` asmdef with zero references — its `NewtonsoftCheck` runs at editor load and surfaces a guided error if `com.unity.nuget.newtonsoft-json` is missing or shadowed, instead of letting users wade through `CS0122 'JObject inaccessible'` and Burst `Failed to resolve assembly` cascades.
- README troubleshooting matrix covering the Newtonsoft install paths (UPM Git URL, Packages folder, Assets/Plugins manual install via Package Manager) plus the Burst stale-DLL recovery (`Library/ScriptAssemblies/` clear).

## [0.3.0] - 2026-05-09

### Added

- **Embedded chat panel** (`Window → Coder Chat (Scene Analyzer)`) — talks to the D:\MCP coder runtime via SSE so the dev never has to leave Unity.
  - HTTP/SSE client (`Editor/Chat/CoderApiClient.cs`) handling text / thinking / tool_call_start / tool_call_result / plan / question / tasks_posted / session / error / done / message_complete events with `Last-Event-ID` replay.
  - In-memory `ChatSession` + UI panel with role bubbles, inline tool-call traces, cancel button, profile + agent picker.
- `screenshot_scene_view` MCP tool — returns a PNG of the active SceneView as an MCP `image` content block.
- `apply_changeset` placeholder refs: any op can carry a `label` and later ops can reference the resulting instanceId via `{ "ref": "<label>" }` anywhere in their args. Lets a single batch chain parent → children without intermediate read calls.

### Changed

- **PostFx split into a secondary asmdef** (`com.maicon.scene-analyzer.Editor.PostFx`) gated by `SCENE_ANALYZER_HAS_SRP_CORE`. The main plugin now compiles on Built-in-only projects without SRP Core; post-fx tools light up only when URP or HDRP is installed.
- `ChangesetExecutor` switched from a hard-coded switch to a handler dictionary (`RegisterHandler`), so secondary asmdefs can plug in their op kinds at editor load.
- MCP server: notifications (no `id`) now reply with empty body per JSON-RPC 2.0 spec instead of returning a fake result envelope.
- Tool result protocol: `ToolResult.Image(...)` factory + optional `ContentBlocks` array on `ToolResult` so tools can emit MCP `image` / `resource` content types when needed.
- Tool descriptions: documented URP/HDRP material gotchas on `create_primitive`, `create_particle_system`, and the pipeline-dependent intensity scale on `create_light`.

### Fixed

- Tool registry: removed an unused `added` local in `PostEffectEditor.AddEffect` that triggered a compiler warning.
- Batch SSE `notifications/initialized` no longer poisons the response array.

## [0.2.0] - 2026-05-09

### Added

- Read tools: `read_scene` (hierarchical), `read_component` (serialised properties).
- Discover tools: `search_assets`, `read_asset_metadata`, `find_references_to_asset`.
- Author/create tools: `create_primitive`, `create_empty`, `create_camera`, `create_audio_source`, `create_particle_system` (smoke/fire/sparks/dust/trail templates), `create_volume`.
- Author/modify tools: `set_transform`, `set_camera_props`, `set_property`, `add_component`, `set_material`, `parent_object`, `delete_object`.
- Post-processing tools: `add_post_effect`, `set_post_effect_property`, `apply_color_grading` (URP & HDRP).
- Render tweaks: `set_render_settings` (ambient/fog/skybox/reflection).
- Authoring helpers: `CameraFactory`, `AudioSourceFactory`, `ParticleTemplates`, `VolumeFactory`, `PostEffectEditor`, `RenderSettingsEditor`, `RendererMaterialEditor`.
- `ChangesetExecutor` now accepts every new op kind, so the agent can author cameras, audio, particles, post-fx, materials, and render settings inside a single Undo group.

### Changed

- Tool registry expanded from 9 (MVP) to 31 tools.

## [0.1.0] - 2026-05-09

### Added

- Initial scaffold for the Scene Analyzer (MCP) plugin.
- Core: `SceneSnapshot`, `ChangeWatcher` (reactive with 250 ms debounce), `AnalysisRunner`, `AssetCatalog`.
- MCP server: `streamable_http` over `System.Net.HttpListener`, JSON-RPC 2.0, bearer auth, main-thread dispatcher.
- Analyzers: `GeometryAnalyzer`, `MissingReferenceAnalyzer`.
- Authoring: `ChangesetExecutor` with single-Undo-group batches, factories for primitives / prefabs / lights.
- MVP tools: `read_hierarchy`, `analyze_performance`, `validate_scene`, `list_assets`, `instantiate_prefab`, `create_light`, `apply_changeset`, `select_object`, `get_project_meta`.
- `SceneAnalyzerWindow` editor UI: token management, server start/stop, write kill switch, recent-ops log.
- Runtime `[RequiredField]` attribute.
