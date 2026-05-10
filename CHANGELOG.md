# Changelog

All notable changes to this package will be documented in this file.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.4] - 2026-05-10

### Fixed — based on real-session error patterns (session 651 forensics)

- **`add_component` for newly-created scripts**: agent had to retry 4× with `unknown component type 'DayNightCycle'` because the .cs was just written via filesystem and Unity hadn't compiled it. Two fixes:
  1. New tool **`refresh_assets`** — calls `AssetDatabase.Refresh()` and (with `wait: true`, default) blocks until `EditorApplication.isCompiling` flips false. Editor UI freezes briefly during the wait — acceptable trade for determinism. Returns a hint when assembly reload is still in flight.
  2. `add_component`'s `unknown component type` error now suggests the exact recovery sequence (`refresh_assets` → check Console → verify class name → retry) and notes when Unity is actively compiling at error-time.
- **`describe_post_effect` enum values**: agent passed `FilmGrain.type = "Thin"` (invalid) and `Vignette.roundness` (param doesn't exist; real one is `rounded: bool`). The describe output now enumerates the legal values for enum-typed parameters (`enum [Thin1, Thin2, Medium1, Medium2, Large01, Large02]`), shows `[true, false]` for booleans, and tags clamped/min parameter wrappers so the agent picks the right name and value first try.

### Changed — preamble guidance to prevent the same failures next time

- New paragraph: "After creating a NEW `.cs` MonoBehaviour script, call `refresh_assets` (wait=true) BEFORE `add_component`."
- New paragraph: "Avoid redundant tool calls — the dashboard refuses identical tool+args after a few retries. Keep findings in reply text instead of recalling, change at least one arg when re-querying, and switch strategy after errors instead of repeating." Names the `[unity-call #N]` marker explicitly so the agent knows that revalidation works only when input args differ.

### Notes

- Tool count is now **48** (was 47).
- The 0.5.3 bump in `package.json` happened mid-flight without a CHANGELOG entry; this 0.5.4 covers all changes since 0.5.2.

## [0.5.2] - 2026-05-09

### Fixed

- The auto-injected preamble said "Invoke them by name via the skill-runner tool" — but the D:\MCP coder doesn't have a tool literally called `skill-runner`. The actual native skill API is three tools defined in `src/core/coder/skills/skill-tool-builder.ts`: **`get_skills_list`** (discovery), **`get_skill_content`** (inspect a skill body), and **`invoke_skill`** (run it with `{skill_id, input?}` → returns the interpolated body). Updated the preamble to name all three correctly, document their args, and guide the agent on the right invocation pattern (`invoke_skill` with the matching `skill_id` and the user's intent as `input`).

## [0.5.1] - 2026-05-09

### Fixed

- D:\MCP coder's identical-response guard ("refused — IDENTICAL content … seen 1 time in last 15 turns") was rejecting normal Unity workflow patterns. The guard is calibrated for file lookups, but in Unity authoring the agent legitimately re-reads the same object after every mutation: `set_property → read_component → set_property → read_component`. Until the next mutation, the read returns identical bytes — and got blocked.
- Every tool response now carries a per-call uniqueness marker `[unity-call #N @ HH:mm:ss.fff]` appended to the last text content block. Marker is monotonic + timestamped so two queries against the same unchanged state still produce different bytes. Centralized in `HttpMcpServer.CallToolAsync` — all 47 tools get it for free.

## [0.5.0] - 2026-05-09

### Added — GameObject ergonomics

- **`duplicate_object`** — clones a GameObject with all its components and children via `Object.Instantiate`. Default parent = source's parent. Supports `offset` (delta from source position) OR `position`/`rotation`/`scale` (outright override), plus `name`. Returns the clone's instanceId.
- **`look_at`** — `Transform.LookAt` wrapper. Pass `targetInstanceId` (use that GO's world position) or `targetPosition` (raw Vector3). Optional `worldUp`. `flipForCamera: true` rotates 180° on Y for non-camera renderers (Quad/Plane face -Z by default).

### Added — Particles

- **`set_particle_module`** — typed setter for ParticleSystem modules: `main`, `emission`, `shape`, `colorOverLifetime`, `sizeOverLifetime`, `velocityOverLifetime`, `rotationOverLifetime`, `noise`, `trails`, `renderer`, `collision`. Numeric fields accept scalar / `[min,max]` / `{constant}` / `{min,max}` / `{multiplier}`. Color fields accept hex / `[r,g,b,a]` / `{min,max}` / `{gradient:[{time,color},…]}`. Bursts: `bursts:[{time, count}]`. Replaces dozens of brittle `set_property` paths with one ergonomic call per module.
- **`play_particle_preview`** — drives the in-editor preview without entering Play Mode. Actions: `play`, `stop`, `pause`, `clear`, `restart`, `simulate` (advance to `time` seconds and freeze). `withChildren: true` cascades. Lets the agent visually verify a freshly-tuned emitter.

### Added — Post-processing

- **`remove_post_effect`** — drops a VolumeComponent (Bloom, Vignette, ColorAdjustments, etc.) from a Volume's profile. Errors when the effect isn't present.
- **`list_post_effects`** — two modes: with `volumeInstanceId` lists effects on that volume (with active-override count); with `available: true` enumerates every VolumeComponent type loaded across URP/HDRP/SRP-Core.
- **`describe_post_effect`** — schema of a VolumeComponent type without an instance: each `VolumeParameter` field name, wrapper type (`FloatParameter`, `ColorParameter`, `MinFloatParameter`, …) and underlying value type (float, Color, int, …). Pre-step before `set_post_effect_property`.

### Added — authoring helpers

- `Editor/Authoring/ParticleEditor.cs` — module-typed setters with `MinMaxCurve`/`MinMaxGradient` parsers.
- `Editor/Authoring/GameObjectEditor.cs` — `Duplicate`, `LookAt` (+ existing SetActive/Rename/SetTag/SetLayer).
- `Editor/PostFx/PostEffectEditor.cs` — `RemoveEffect`, `ListEffects`, `ListAvailableEffectTypes`, `DescribeEffectType`, `CountActiveOverrides`.

### Changed

- `ChangesetExecutor` registers 5 new op kinds: `duplicate_object`, `look_at`, `set_particle_module`, `play_particle_preview` (main asmdef) + `remove_post_effect` (PostFx asmdef). All compose inside `apply_changeset` for atomic batches with a single Undo group.
- Default context preamble updated with the new tools and a dedicated **Particles** category, plus new Render & post-fx entries (`remove_post_effect`, `list_post_effects`, `describe_post_effect`).
- Tool count is now **47** (was 40 in 0.4.0; +7 in this release).

## [0.4.0] - 2026-05-09

### Added — component lifecycle + reflection-based discovery

- **`remove_component`** — drops a Behaviour/MonoBehaviour/Collider/Rigidbody/Renderer/etc. from a GameObject. Refuses Transform. Supports `nth` to disambiguate when several of the same type are attached. Single Undo step.
- **`set_enabled`** — toggles `Behaviour.enabled` / `Renderer.enabled` / `Collider.enabled` without removing the component. Errors clearly when the type doesn't expose the flag.
- **`set_active`** — `gameObject.SetActive(bool)`.
- **`rename_object`** — sets `gameObject.name` (does not touch the underlying prefab asset).
- **`set_tag`** — sets `gameObject.tag`. Errors when the tag isn't registered in Project Settings → Tags & Layers.
- **`set_layer`** — accepts int (0..31) or layer name string. Pass `recursive: true` to apply to children.
- **`list_component_types`** — enumerates every concrete `UnityEngine.Component` subclass the editor has loaded, across every assembly. Filters by `nameContains`, `category` (`all` / `builtin` / `user`), `limit`. Returns name, fullName, namespace, assembly, isMonoBehaviour, isBuiltin, plus the `[RequireComponent]` list when present. Lets the agent discover what's installable on a GameObject (built-in physics/rendering/audio + every custom MonoBehaviour in the project) before calling `add_component`.
- **`describe_component_type`** — returns the SCHEMA of a Component type without needing a scene instance: serialized fields (name, type, isArray, elementType, isRequired) with attribute metadata (`Range`, `Tooltip`, `Header`, `Min`, `Multiline`, `TextArea`, our `[RequiredField]`, hideInInspector). Optional `includeMethods` lists public instance methods. Ideal pre-step before `set_property` so the agent knows the valid `propertyPath`s.

### Added — authoring helpers

- `Editor/Authoring/ComponentTypeInspector.cs` — reflection over `AppDomain.CurrentDomain.GetAssemblies()` with safe handling of `ReflectionTypeLoadException`. Returns shape-rich `TypeDescriptor` / `FieldDescriptor` records.
- `Editor/Authoring/GameObjectEditor.cs` — SetActive / Rename / SetTag / SetLayer.
- `Editor/Authoring/ComponentEditor.cs` — `RemoveComponent`, `SetEnabled`.

### Changed

- `ChangesetExecutor` handler dictionary now routes 6 new op kinds: `remove_component`, `set_enabled`, `set_active`, `rename_object`, `set_tag`, `set_layer`. They batch into a single Undo group like the existing ops.
- Default context preamble (auto-injected on first message of a new session) was reorganised into bulleted categories AND mentions the new component-discovery loop: `list_component_types → describe_component_type → add_component → set_property`. Helps the agent pick the right tool without trial-and-error.
- Tool count is now **40** (was 32 in 0.3.x; +8 in this release).

## [0.3.15] - 2026-05-09

### Fixed

- MCP server crashed every tool call with `MCP error -32603: GetBool can only be called from the main thread`. Cause: `HttpMcpServer.CallToolAsync` checked `Settings.AllowWrites` (which reads `EditorPrefs.GetBool`) **before** dispatching to the main thread, but the HTTP listener accept loop runs on the .NET thread pool. Unity throws the moment any non-main thread touches `EditorPrefs`. Moved the AllowWrites guard inside the `MainThreadDispatcher.RunAsync` block so the read happens on the editor main thread, alongside the actual tool execution.

## [0.3.14] - 2026-05-09

### Fixed

- `ArgumentException: Can not convert Object to String` thrown from `DispatchEvent` when a question event arrived with `options` as `[{label, value}, …]` instead of plain strings. Direct `(string)JToken` casts blow up on JObject/JArray. Replaced every direct cast in the SSE handler with shape-tolerant helpers (`AsString`, `AsInt`, `AsBool`, `AsDouble`, `OptionLabel`) — they read primitives directly and serialise complex tokens to JSON when needed. Affects every event field in `text`, `tool_call_start`, `tool_call_result`, `plan`, `question`, `tasks_posted`, `session`, `error`.
- One bad event no longer kills the whole SSE stream. `DispatchEvent` now wraps the dispatch body in try/catch and logs the offending event as a system note (`(skipped malformed \`<type>\` event: …)`) so the rest of the response keeps streaming.
- `OptionLabel` extracts a human-readable label from option objects: prefers `label`, falls back to `text`, then `value`, then `id`, finally serialised JSON.

## [0.3.13] - 2026-05-09

### Added

- **Show / Hide system messages** toggle in the Conversation header. When off, the chat hides ChatRole.System bubbles ([1/3]/[2/3]/[3/3] progress lines, `(thinking)` markers, preamble previews, generic internal notes) so the user sees just User ↔ Coder turns. Persistent via `Settings.CoderShowSystemMessages` (default on).
- **Auto-scroll on new messages**. The conversation now snaps to the latest bubble whenever a new message arrives OR the streaming assistant text grows. Tracked via `_lastMessageCount` + `_lastStreamingTextLen` per paint so partial-stream updates also follow. A `↓` button next to the toggle force-scrolls to the bottom on demand.
- Re-anchors after toggling the system filter (so the list height change doesn't strand the user mid-history).

## [0.3.12] - 2026-05-09

### Added

- **Reply to agent prompts inside the Unity chat**. The `ask_user`/`submit_plan`/`submit_tasks` events now drive proper interactive UI:
  - **Question** card (yellow tint) — renders the agent's question, shows option buttons when the agent supplied them, and a free-form text area + Send/Skip when allowFreeform.
  - **Plan** card (blue tint) — shows the submitted plan with optional note, Approve / Reject buttons.
  - **Tasks** card (green tint) — Approve / Reject for `submit_tasks`.
  - Each card POSTs to the matching endpoint (`/answer`, `/plan-response`, `/tasks-response`) and clears itself on success.
- New CoderApiClient methods: `AnswerQuestion`, `RespondToPlan`, `RespondToTasks`.
- New ChatSession state: `CurrentQuestion`, `CurrentPlan`, `CurrentTasks`, populated by the SSE handler.

### Changed

- **Chat message rendering rewritten**. Messages now use word-wrapped labels with computed heights so long text doesn't get clipped to a single line. Each role renders with a tinted background bubble (User=blue, Coder=gray, System=pale yellow, Error=red). Tool-call result text wraps too.
- Window default minSize bumped from 360×480 to 420×560.
- The Conversation scroll view now `ExpandHeight(true)` so it grows to fill the window, instead of being capped at 280px.
- Input area minHeight bumped from 60 to 70.
- HelpBox in Settings clarifies: "if the new project doesn't show in the dashboard, F5 the dashboard tab" — covers the most common cause of "I don't see my project" (just a stale browser cache, since `GET /api/projects` reads `SELECT * FROM projects` with no filters).

## [0.3.11] - 2026-05-09

### Fixed

- Context preamble was only injected when the session was auto-created **inside** Send (the `sessionId == 0` branch). If the user clicked the **New** button first to create the session and only then sent a message, Send saw `sessionId > 0`, skipped auto-create, and didn't inject — the agent never saw the Unity context, skills list, or `ext_unity_editor__*` tool inventory.
- Replaced the `justCreatedSession` boolean with a thread-safe `_pendingPreambleFor` set on the client. Both creation paths (auto-create on Send AND the New-button fire-and-forget) now mark the new session id as pending. The next Send to that exact id consumes the flag and injects the preamble — regardless of which button created the session. Continuing existing sessions or switching to one via "Set" still goes through unchanged.

## [0.3.10] - 2026-05-09

### Fixed

- Missing `.meta` for `Editor/Chat/CoderProviderInfo.cs` (added in 0.3.9). Without it Unity ignored the file under the immutable `Packages/` path, so `Bootstrap.cs` couldn't resolve the type and emitted `CS0246: 'CoderProviderInfo' not found`. Generated the meta and re-verified the whole tree — every folder + every file now has its `.meta` committed.

## [0.3.9] - 2026-05-09

### Added

- **Live providers/models list** from `GET /api/coder/providers`. The Coder Chat panel's Provider and Model dropdowns now reflect what's actually configured + has API keys + use-for-coder enabled in the dashboard, instead of the previous hardcoded `anthropic/openai/google/ollama` list. Provider entries show flags `[no key]` and `(default)`. Selecting a provider repopulates the Model dropdown (with context-window size as a hint). Auto-fetched on first window open + a manual **Refresh** button.
- **🧠 Think mode** toggle in the message composer (right next to Send). When on, every outgoing message body carries `thinking: true`, which the D:\MCP coder maps to the active provider's extended-thinking primitive: Anthropic `thinking.budget_tokens`, OpenAI `reasoning_effort: high`, Gemini `thinkingConfig`. Per-message flag — flip it on/off as needed; persisted via EditorPrefs across editor restarts. Status note in the transcript shows when a message went out with think mode on.
- `Bootstrap.CoderProviders` cache + `OnCoderProvidersChanged` event so the window refreshes when the cache updates.
- New DTO file `Editor/Chat/CoderProviderInfo.cs` (`CoderProviderInfo` + `CoderModelInfo`).

### Changed

- `SendAsync` signature gained a `bool thinking` parameter; the calling pipeline (`Send`) snapshots `Settings.CoderThinking` on the main thread and passes it through, consistent with the other Settings values.
- The `[3/3] POST …` progress note now appends `(🧠 think mode)` when the flag is on.

## [0.3.8] - 2026-05-09

### Fixed

- Send pipeline used to swallow exceptions silently (`_ = Task.Run(...)` with no try/catch). If anything failed before the HTTP request — EditorPrefs read on a non-main thread, URL parse, etc. — neither the Console nor the chat transcript reported it. The user just saw "nothing happened." Wrapped every Task.Run continuation (Send, CreateSession, EnsureProject) in try/catch + `Debug.LogException` + chat-transcript error. Also `_cts = null` now lives in a `finally` block so a cancelled run doesn't wedge `IsRunning`.
- All `Settings.*` reads needed for the Send pipeline are now snapshotted on the main thread before `Task.Run`, instead of being touched from background threads.

### Added

- Per-step progress notes in the transcript: `[1/3] ensuring D:\MCP project…`, `[2/3] creating session…`, `[3/3] POST /api/coder/sessions/N/messages…`. When something goes wrong you can see exactly which hop failed.
- **Test connection** button next to the URL field — does a `GET /api/projects` and reports the HTTP status + the project count it can see. Quick way to verify the runtime is up and the URL/port/token are correct without needing to send a real message.

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
