# Changelog

All notable changes to this package will be documented in this file.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
