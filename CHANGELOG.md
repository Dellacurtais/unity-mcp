# Changelog

All notable changes to this package will be documented in this file.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.11.0] - 2026-05-10

### Added — NavMesh Tier 1 (6 tools, main asmdef, no extra package)

Built on `UnityEngine.AI.NavMesh` (always present) and `UnityEditor.AI.NavMeshBuilder` (legacy bake, also built-in).

- **`navmesh_sample_position`** — `NavMesh.SamplePosition(point, maxDistance, areaMask)`. Answers "is this point on the walkable area?" + returns the projected position, distance, area mask, normal.
- **`navmesh_calculate_path`** — full path from A to B with status (Complete / Partial / Invalid), corner list, total distance.
- **`navmesh_raycast`** — line-of-sight on the navmesh. Documents the inverted convention (returns BLOCKED when the line crosses non-walkable; CLEAR when the entire line stays on the navmesh) since it's the opposite of `Physics.Raycast`.
- **`navmesh_find_closest_edge`** — nearest walkable-area edge near a point. Useful for cover-finding / boundary AI.
- **`bake_navmesh`** — `NavMeshBuilder.BuildNavMesh()` (legacy global). Sync default; `async: true` for fire-and-forget.
- **`set_navmesh_agent`** — combo write+read: optional setters for destination/isStopped/speed/acceleration/angularSpeed/stoppingDistance/radius/height/baseOffset/areaMask/resetPath, plus always returns full state (isOnNavMesh / hasPath / pathPending / destination / remainingDistance / velocity / pathStatus / corner count).

`areaMask` parsing accepts int (raw bitmask), single area name string ('Walkable' / 'Jump' / custom), or array of area names — same convenience as the LayerMask parser added in 0.9.4.

### Added — NavMesh Tier 2 (4 tools, gated by `com.unity.ai.navigation`)

New asmdef `com.maicon.scene-analyzer.Editor.NavMesh` with `defineConstraints: ["SCENE_ANALYZER_HAS_NAVMESH"]` + `versionDefines` on `com.unity.ai.navigation >= 1.0.0`. Same pattern as PostFx / Timeline — no package, no compile, the 4 tools simply aren't registered. With package present, `NavMeshBootstrap` registers via `[InitializeOnLoad]` + `delayCall`.

- **`list_navmesh_surfaces`** — every NavMeshSurface in the active scene with agentTypeID, collectObjects, layerMask, useGeometry, baked status.
- **`bake_navmesh_surface`** — `surface.BuildNavMesh()`. With `surfaceInstanceId` bakes one; without arg, bakes ALL NavMeshSurfaces in the scene.
- **`add_navmesh_surface`** — adds + configures the component: agentTypeID (default 0 = Humanoid), collectObjects (All/Volume/Children), useGeometry (RenderMeshes/PhysicsColliders), layerMask (int OR layer name OR array), defaultArea, voxel/tile size overrides. Pass `bake: true` to bake immediately.
- **`add_navmesh_modifier`** — adds NavMeshModifier (default) or NavMeshModifierVolume (when `volume: true`). Modifier overrides the area assignment of geometry under the GameObject; Modifier Volume overrides area within a box. `area` accepts int index or name string.

### Authoring helpers

- `Editor/Authoring/NavMeshAuthor.cs` — Tier 1 helpers (queries + bake + agent).
- `Editor/NavMesh/NavMeshSurfaceAuthor.cs` — Tier 2 helpers.
- `Editor/NavMesh/NavMeshBootstrap.cs` — `[InitializeOnLoad]` register-and-go for the gated tools and changeset op kinds.

### Changed

- `ChangesetExecutor` registers 5 new op kinds: `bake_navmesh`, `set_navmesh_agent` (main asmdef) plus `bake_navmesh_surface`, `add_navmesh_surface`, `add_navmesh_modifier` (registered by `NavMeshBootstrap` from the gated asmdef).
- Default context preamble now has a **NavMesh** section listing the queries, bake, agent, and modern per-surface tools — including the inverted-convention warning for `navmesh_raycast`.

### Notes

- All NavMesh queries work in **edit mode** as long as a navmesh is baked. After scene mutations that affect walkable geometry, call `bake_navmesh` (legacy) or `bake_navmesh_surface` (modern) before re-querying.
- "Coloque um inimigo perseguindo o player" workflow now fits in one batch: `add_navmesh_surface` → `bake_navmesh_surface` → `add_component NavMeshAgent` on enemy → `set_navmesh_agent destination=<player pos>`.

## [0.10.0] - 2026-05-10

### Added — ScriptableObject support (5 tools)

Until now the plugin could *find* ScriptableObject assets via `list_assets type=ScriptableObject` and *reference* them in Component fields via `set_property`, but couldn't create, inspect, or write SO assets directly. Six new tools close that gap.

- **`list_scriptable_object_types`** — enumerates every concrete `ScriptableObject` subclass loaded in the editor (project + Unity packages). Each entry shows full type name, assembly, and any `[CreateAssetMenu]` attribute (menu path + default file name) so the agent knows the right-click → Create entry. Filters: `nameContains`, `category` (all/builtin/user), `withMenuOnly: true`. Skips abstract classes, generics, and EditorWindow-derived SOs.
- **`read_scriptable_object`** — dumps every visible SerializedProperty on an SO asset (name, type, array size, value summary). Capped at 80 fields. Replaces the awkward "load via list_assets, somehow inspect" gap.
- **`create_scriptable_object`** — `ScriptableObject.CreateInstance<T>()` + `AssetDatabase.CreateAsset(path)` + optional `initialFields` mapping (any SerializedProperty → value, parsed by the same engine as `set_property`). Auto-creates parent folders. Returns the new asset's instanceId.
- **`set_so_property`** — sets a SerializedProperty on an SO asset. Asset target instead of `set_property`'s GameObject+Component target. Same value coverage (int/bool/float/string/Color/Vector*/LayerMask/Bounds/Rect/Quaternion/enum/ObjectReference).
- **`duplicate_scriptable_object`** — `Object.Instantiate(src)` + `CreateAsset(targetPath)`. Independent copy.

### Added — generic array helper (1 tool, works for Component AND SO)

- **`modify_array_property`** — actions: `append` / `insert` / `remove` / `set` / `resize` / `clear` on any array/list SerializedProperty. Targets either a Component on a GameObject (`instanceId` + `componentType`) OR a ScriptableObject asset (`assetGuidOrPath`). Resolves Unity's internal `Array.size` / `Array.data[N]` mechanics so the agent doesn't have to know that the size is a separate sub-property. Handles the ObjectReference array-deletion quirk (first delete nulls, second removes) automatically. `value` parsing uses the same engine as set_property.

### Changed

- `ComponentEditor.ApplyValue` and `ResolveComponentType` are now `public` so `ScriptableObjectAuthor` can reuse them. `ComponentTypeInspector.IsBuiltinAssembly` is also public for the same reason.
- `ChangesetExecutor` registers 4 new op kinds (`create_scriptable_object`, `set_so_property`, `duplicate_scriptable_object`, `modify_array_property`) — all batchable in a single Undo group.
- Default context preamble updates: SO discovery tools listed in **Discover assets & types**, SO authoring listed in **Create**, and the typical loop block now includes the SO equivalent (`list_scriptable_object_types` → `describe_component_type` → `create_scriptable_object` with initialFields → `set_so_property`) plus the array-helper guidance ("prefer `modify_array_property` over raw `Array.size`/`Array.data[N]` paths").
- `describe_component_type` already worked for any Type via reflection. The preamble now states this explicitly so the agent doesn't need a separate `describe_scriptable_object_type` tool.

### Notes

- Tool count increases by 6.
- All 7 new files have committed `.meta` so the package imports cleanly under `Packages/`.

## [0.9.5] - 2026-05-10

### Fixed

- **Duplicate `case SerializedPropertyType.Bounds` / `Rect` in `ComponentEditor.ApplyValue`** caused CS0152 (the switch had two labels for the same enum value) and broke every editor reload — Unity refused to compile the package after a script change. Two parallel implementations had grown: an early inline-parsed version (mine, narrow shapes) and a later helper-based pair (`ParseBounds` / `ParseRect`) added when the helpers gained `extents` and array/object Rect support. Consolidated to a single case per type using the richer helpers; the inline duplicates are gone.
- The fix was first applied by the agent directly to the user's `Library/PackageCache/.../ComponentEditor.cs` to unblock the editor reload mid-session; this commit replicates it in the source tree so the next package fetch is correct.

## [0.9.4] - 2026-05-10

### Fixed

- **`set_property` on LayerMask fields** — the agent was setting `_collisionMask = -1` (every-layer mask) and got `[OP_FAILED] INVALID_ARG: failed to apply value: UNSUPPORTED_TYPE: LayerMask` because `ComponentEditor.ApplyValue` had no case for `SerializedPropertyType.LayerMask`. LayerMask serialises as int (bitmask). Now accepts: int (raw bitmask, e.g. `-1` for "Everything"), single layer-name string (resolves to `1 << NameToLayer`), or array of layer-name strings (`LayerMask.GetMask(...)`). Same case also covers `Character`.
- While we were in there, also added `Vector4`, `Vector2Int`, `Vector3Int`, `Rect`, and `Bounds` (`{center, size}`) — the previous switch covered scalars/vectors/colors/quaternions/enums/object-refs/strings but threw for these.

### Changed — agent-friendly aliases

Two real misuses surfaced from a session transcript: agent called `look_at` with `target:[...]` (we wanted `targetPosition`/`targetInstanceId`) and `parent_object` with `parentInstanceId` + `keepWorldPosition` (we wanted `newParentId` + `worldPositionStays`). Plugin now accepts both:

- **`look_at`** — accepts `target` as auto-detect alias: integer → instanceId, array/object → world position. The original `targetInstanceId` / `targetPosition` still work.
- **`parent_object`** — accepts `parentInstanceId` and `parent` as aliases for `newParentId`. Accepts `keepWorldPosition` as alias for `worldPositionStays`. Canonical names still preferred but no longer required.

These mismatches were 50/50 plugin/agent issues — strict naming was correct, but tolerant aliases cost almost nothing and avoid retry loops on field-name guessing.

## [0.9.3] - 2026-05-10

### Fixed — MCP server losing the dev across every assembly reload

User-reported: the server "falls down too easily" with every script compile / asset refresh, disrupting the agent. Root: `AutoStartServer` defaulted to false; the dev had to manually click Start in `Window → Scene Analyzer` after every domain reload (Unity tears down the entire AppDomain, the listener dies). Anything we did in 0.8.2 to fail-fast the in-flight requests still left the dev with a dead server until the next manual click.

### Added — sticky auto-start

- **`Settings.ServerStickyRunning`** (persisted via EditorPrefs). Set to `true` automatically by `Bootstrap.StartServer()` when the dev clicks Start; cleared by `Bootstrap.StopServer()` when they click Stop. Once sticky, the server auto-restarts after EVERY assembly reload regardless of `AutoStartServer`. One click survives every reload until you explicitly stop.
- **Bootstrap auto-start now checks `AutoStartServer || ServerStickyRunning`** (was just AutoStartServer). Migration is silent — first-time stickys engage on the next Start click.

### Added — server-state notifications in chat

- **`Bootstrap.OnServerStateChanged` event** — fires on Start / Stop / `OnAfterReload` (when the post-reload static ctor restarts the server). Subscribers get the new state string ("started at port N", "back online at port N after assembly reload", "stopped (sticky cleared)").
- **`CoderChatWindow` subscribes** and adds a system note in the conversation when the server transitions. After a script change → reload → server back, the chat shows `MCP server: back online at port 8765 after assembly reload` — the dev (and the agent) know the previous 503 will succeed on the next call.
- **`OnAfterReload` hook** in Bootstrap fires the notification after the AppDomain re-init completes.

### Tweaked — dispatcher warning threshold 50ms → 200ms

`MainThreadDispatcher` was warning on every closure that took >50ms, which means the new analyzer batch (HierarchyDepth + Geometry + Lighting + DrawCallEstimator + ScriptCost + MissingReference + RequiredField walking the whole snapshot) emitted 5+ warnings per analysis tick. 200ms is the threshold a frame stutter becomes visible to the user; below that, the analyzers' work is expected and noisy to warn about. Genuinely problematic single-call hogs (>200ms) still warn.

### Notes

- The server still goes briefly offline during the reload itself (typically 1-2s) — that's a Unity AppDomain limitation we can't bypass without a separate process. But the gap is now bounded by Unity's own reload time, not by manual user intervention.
- If the dev wants the server permanently off, click Stop — sticky clears, no auto-restart on subsequent reloads. Click Start once, sticky returns.
- `AutoStartServer` toggle is still useful: if true, the server starts the very first time the editor opens this project (before any Start click).

## [0.9.2] - 2026-05-10

### Fixed — OpenAI/Codex strict schema validator rejecting tool registration

User-reported on Codex/GPT: `400 Invalid schema for function 'ext_unity__create_light': In context=('properties', 'color', 'type', '1'), array schema missing items`. The OpenAI tools API enforces a stricter JSON Schema subset than Anthropic — when `type` is an array (multi-type) AND one of the entries is `"array"`, the schema MUST include `items` for the array branch. Our `["color"] = { "type": ["string", "array", "object"] }` shape was rejected.

### Changes

- **New `SchemaHelpers.ColorSchema`** — `oneOf [{type:string}, {type:array, items:{type:number}, minItems:3, maxItems:4}, {type:object}]`. Documented as "hex string '#rrggbb[aa]', RGB[A] array, or {r,g,b,a} object". Standardised the same way `Vec3Schema` and `RotSchema` already are.
- **CreateLightTool** — `color` now uses `SchemaHelpers.ColorSchema` instead of the multi-type-without-items shape that broke Codex.
- **CreateCameraTool / SetCameraPropsTool** — `backgroundColor` upgraded from empty `JObject()` to `ColorSchema` for consistency and informativeness (no validator complaint either way; the empty schema worked but was opaque).
- **SetRenderSettingsTool** — `ambientLight`, `ambientSkyColor`, `ambientEquatorColor`, `ambientGroundColor`, `fogColor` all upgraded to `ColorSchema`.
- **ParentObjectTool** — `newParentId` was `["integer", "null"]` (legitimate JSON Schema nullable but Codex strict mode complains). Simplified to `integer` with a doc note that 0/omit means scene root (the runtime impl already handles both).

### Notes

- Audited the rest of the schema surface with `grep`; no other multi-type fields with array branches remain.
- Anthropic's tool API tolerated all of these because its validator is more permissive — that's why this only surfaced once you flipped the chat panel to Codex/GPT.

## [0.9.1] - 2026-05-10

### Fixed — `read_console` missed pre-existing compile errors

User-reported (with screenshot): `read_console` returned empty / only the dispatcher's own warnings while Unity's Console clearly showed `error CS0122: 'PlayerState.Controller' is inaccessible due to its protection level` and a `warning CS0414` from before the plugin was loaded. Cause: `ConsoleCapture` only subscribes to `Application.logMessageReceivedThreaded` at `[InitializeOnLoad]` time — it captures messages emitted AFTER the static ctor runs. Compile errors that arrived before our load (or that survived a prior assembly reload) live only in Unity's persistent Console buffer and weren't being read.

### Added

- **`Editor/Core/UnityConsoleReader.cs`** — reads Unity's actual Console buffer via reflection over the internal `UnityEditor.LogEntries` API (`GetCount`, `StartGettingEntries` / `EndGettingEntries`, `GetEntryInternal`, `LogEntry.message/file/line/mode`). Surfaces every entry visible to the user, including pre-existing compile errors. Severity is derived from the `mode` bitfield with a textual fallback (`error CS…`, `warning CS…`, `Exception:`) so labelling stays correct across Unity revisions where bit positions drift.

### Changed

- **`read_console` now merges TWO sources by default**:
  1. Unity's persistent Console (full state visible to the user, no `seq`)
  2. Our live capture buffer (with `seq` for delta queries via `since`)
- New `source` arg: `all` (default) / `unity` (LogEntries only) / `buffer` (live capture only). Passing `since=N` automatically scopes to `buffer` since LogEntries has no seq numbering.
- Summary header at the top reports total error / warning / info counts and the selected source. The two sources are clearly delineated in the body so the agent knows which entries persisted vs which were captured live.

### Notes

- LogEntries bit flags aren't part of any public API; the constants used (StickyError, ScriptingError, ScriptingException, ScriptCompileError, AssetImportError, etc.) are best-effort. The textual fallback handles Unity versions where the bit layout shifts.
- `UnityConsoleReader.IsAvailable` returns false if reflection fails (drift in the internal API). When `source=unity` is requested explicitly and the API is unavailable, returns a clear error pointing at `source=buffer` as the alternative.

## [0.9.0] - 2026-05-10

### Added — Console / build verification (3 tools)

- **`read_console`** — reads recent Unity Console messages from a 1000-entry ring buffer maintained at editor load. Captures `Debug.Log/Warning/Error`, uncaught exceptions, AND structured compile errors/warnings (with file:line:column extracted via `CompilationPipeline.assemblyCompilationFinished`, not just the stringified version). Filters: severity (`all` / `warnings_and_errors` / `errors_only`), `since` seq number (anchor a "since this op started" window), `messageContains`, `limit`, `includeStack`.
- **`clear_console`** — clears the captured buffer; with `clearUnityConsole=true` also clears Unity's own Console window via `UnityEditor.LogEntries.Clear` (internal API, tolerated to fail). Useful as a marker before a complex op.
- **`wait_for_compile_clean`** — single-call replacement for `refresh_assets` + `read_console errors_only since=N`. Triggers `AssetDatabase.Refresh`, cooperatively waits for `EditorApplication.isCompiling` (no main-thread block; uses the same TCS + `EditorApplication.update` pattern as 0.8.2's `refresh_assets`), then returns "✓ clean" or the list of compile errors/warnings since the wait started. Includes file:line for compile messages.

### Authoring helper

- `Editor/Core/ConsoleCapture.cs` — `[InitializeOnLoad]` ring buffer subscribing to `Application.logMessageReceivedThreaded` (catches messages from any thread, including the HTTP listener and async tools) and `CompilationPipeline.assemblyCompilationFinished` (structured compile messages). Thread-safe (lock-based). 1000-entry capacity. Per-AppDomain — assembly reload resets the buffer (acceptable; reloads only happen on clean compile, so there are no errors to lose).

### Changed

- Default context preamble adds **Console / build verification** section listing the three new tools, plus a refined recipe for "after creating a new .cs script" — recommends `wait_for_compile_clean` (one call) over the older `refresh_assets` + `read_console` two-step.
- Tool count is now **95** with Timeline / **90** without (was 92/87 in 0.8.2; +3 this release).

### Notes

- The seq counter and buffer reset on every assembly reload. If your `since=N` was captured before a reload, you'll get 0 entries — the reload erased pre-existing messages, which is fine because compile reloads only happen on success.
- Compile errors do NOT trigger a reload (no DLL produced) — those entries persist in the buffer until the next successful compile.
- `wait_for_compile_clean` failure mode on a SUCCESSFUL compile that triggers a reload: the request gets caught by the 0.8.2 graceful-shutdown path (HTTP 503 with "Unity assembly reload — retry in ~5s"), and the agent retries → the second call returns clean immediately. Documented in the tool description.

## [0.8.2] - 2026-05-10

### Fixed — `MCP error -32001: Request timed out` on tool calls

User-reported: trivial reads like `ext_unity__get_project_meta` were returning `MCP error -32001: Request timed out` even though the tool itself executes in <50ms. Diagnosed three blocking patterns interacting:

**(1) `RefreshAssetsTool` was busy-blocking the editor main thread** — `Thread.Sleep(100)` in a loop while waiting for `EditorApplication.isCompiling`. That sleep ran inside a `MainThreadDispatcher.RunAsync` continuation, which means `EditorApplication.update` couldn't fire while the sleep was active, so `MainThreadDispatcher.Pump` couldn't drain other queued tool calls. Concurrent calls stalled until `refresh_assets` returned (up to 30s).

**(2) Pump budget was 32 tasks/frame** (count-based). With the recently-added analyzers and `ChangesetExecutor.OnApplied` re-firing analysis, queues could grow faster than they drain.

**(3) Assembly reload silently killed the listener** — when the user saved a `.cs` script mid-session, `Bootstrap.OnBeforeReload` called `Server.Stop()` but didn't notify in-flight HTTP clients. The MCP client sat on the dead socket until its 60s request timeout fired.

### Changes

- **`IMcpTool` gained `Task<ToolResult> ExecuteAsync(JObject)` with a default that wraps `Execute` via `MainThreadDispatcher.RunAsync`.** Tools that need cooperative async can override directly. Sync tools migrate transparently.
- **`HttpMcpServer.CallToolAsync` now calls `tool.ExecuteAsync(args)`** instead of dispatching `Execute` itself. The `Settings.AllowWrites` guard still runs on the main thread (cheap pre-check) before delegating to ExecuteAsync.
- **`MainThreadDispatcher.Pump` switched to a 5ms time budget** (replaces `const int budget = 32`). Drains as much as possible without taxing the editor; bursts clear in 1-2 frames instead of dozens. Logs a warning when a single closure exceeds 50ms — flags tools doing too much on the main thread.
- **`RefreshAssetsTool` rewritten with cooperative async wait.** No more `Thread.Sleep`. `wait=true` registers an `EditorApplication.update` polling closure that checks `isCompiling` once per frame and completes a `TaskCompletionSource`. Bounded by `Task.WhenAny(tcs.Task, Task.Delay(timeoutMs))` so the timeout still works. The dispatcher pump fires every frame normally; concurrent tool calls drain. Editor doesn't freeze.
- **`HttpMcpServer` tracks active in-flight contexts** and now exposes `Stop(string reason)`. On stop, walks every active response and writes `HTTP/1.1 503 Service Unavailable` + `Connection: close` + the reason body before tearing down the listener. Clients fail fast (~1s) with a meaningful payload instead of hanging on the request timeout.
- **`Bootstrap.OnBeforeReload` calls `Server.Stop("Unity assembly reload — server restarting; retry in ~5s")`** so the MCP client sees the proper 503 with a clear message before the AppDomain dies.
- **`CoderChatWindow`/`CoderApiClient` detect `-32001` / "Request timed out" / "assembly reload" patterns in `tool_call_result` and surface a focused system note** explaining the likely cause and recovery (wait 5s, retry).

### Notes

- Default interface methods require Unity 6 / C# 8+ — already our target.
- The migration is transparent for the other 90+ tools — they keep `Execute` and inherit the default ExecuteAsync.
- Skipped (deferred): MCP `notifications/progress` heartbeat per call, analyzer time-budget caps, per-tool client-side timeout overrides. Not needed once the dispatcher and refresh paths are well-behaved.

## [0.8.1] - 2026-05-10

### Fixed — Coder sessions silently attaching to the wrong dashboard project

User-reported: opening a Coder Chat in Unity created sessions under a "random" dashboard project (typically id 1, an unrelated project from another context) instead of one whose `root_path` matched the Unity workdir. Files written by the agent ended up in the wrong place.

**Root causes (4):**

1. `Settings.CoderProjectId` defaulted to **`1`**, not `0`.
2. The Send pipeline only auto-detected when `id <= 0`, so the default 1 always skipped detection and used the cached id.
3. The "New" button passed `Settings.CoderProjectId` straight to `CreateSessionAsync` without ensuring it matched the workdir.
4. There was no persisted workdir to compare against — when the user opened a different Unity project on the same machine, the cached id stayed pointing at the previous project.

**Fix:**

- `Settings.CoderProjectId` default changed `1 → 0`.
- New persisted fields: `Settings.CoderProjectWorkdir` (the workdir matched at last resolve) and `Settings.CoderProjectName` (cached name for UI).
- `EnsureProjectAsync` now returns a tuple `(int Id, string Name, string RootPath)` so callers can persist all three.
- New `ResolveProjectAsync` private helper centralises the cache-or-resolve logic. Returns the cached id only when the new helper `WorkdirMatches(cachedRoot, currentUnityRoot)` is true; otherwise re-runs `EnsureProjectAsync` and updates Settings.
- `Send()` uses `ResolveProjectAsync` (replacing the duplicated inline auto-detect block).
- `CreateSession()` (called by the "New" button) now ALSO calls `ResolveProjectAsync` and ignores its `projectId` argument — the kept signature is for source-compat.
- `EnsureProject()` (the Auto-detect button path) persists `CoderProjectWorkdir` + `CoderProjectName` on success.

**Migration (one-shot):** `Bootstrap` static ctor checks `Settings.CoderSettingsVersion < 2` and, when there's no workdir cached, zeroes the stale `CoderProjectId` so the next Send re-resolves correctly. Silent — no warnings.

**UI improvements (CoderChatWindow):**

- Below the Project row, a status line: `pinned: #N — <name> @ <workdir>` (or `(none — Send will auto-detect from workdir)` when id=0).
- Mismatch warning HelpBox when the cached workdir differs from the current Unity workdir, telling the dev that Send/Auto-detect will fix it automatically.
- "Clear" button now also clears `CoderProjectWorkdir` and `CoderProjectName`.

### Notes

- The auto-detect note in the transcript now reads `[1/3] no project cached — auto-detecting from Unity workdir` (or `… cached project #N points at '<old>' but Unity is at '<new>' — re-resolving` on drift), so the dev can see exactly which path the resolver took.

## [0.8.0] - 2026-05-10

### Added — Audio (12 tools)

**Tier A — core authoring (3):**
- **`create_audio_source_3d`** — convenience for 3D point sources (engine box, generator, fountain). Defaults to spatialBlend=1, loop=true, playOnAwake=true. Exposes the crucial 3D fields directly: `minDistance`, `maxDistance`, `rolloffMode` (Logarithmic/Linear/Custom), `dopplerLevel`, `spread`. Optional `mixerGroup` for routing.
- **`set_audio_source_props`** — typed setter for every common AudioSource field (volume, pitch, spatialBlend, min/maxDistance, rolloffMode, dopplerLevel, spread, panStereo, reverbZoneMix, priority, mute, loop, playOnAwake, bypass*, clip).
- **`play_audio_source`** — Play/Stop/Pause/UnPause/PlayOneShot. Documented limitation: AudioSource only emits in Play Mode.

**Tier B — mixers (5):**
- **`list_audio_mixers`** — `.mixer` assets in the project.
- **`read_audio_mixer`** — groups (via FindMatchingGroups), snapshots, exposed parameters with current values (introspected via SerializedObject on `m_Snapshots` / `m_ExposedParameters`).
- **`assign_to_mixer_group`** — sets `AudioSource.outputAudioMixerGroup`. Accepts plain group name (e.g. 'SFX') or 'MixerName/GroupName' for disambiguation.
- **`set_mixer_parameter`** — `mixer.SetFloat(exposedName, value)` for ducking, fade, dynamic EQ. Documented limitation: parameter must already be Exposed in the mixer.
- **`play_mixer_snapshot`** — `snapshot.TransitionTo(seconds)` — swap moods (combat → peaceful, normal → muffled-by-water).

**Tier C — convenience (4):**
- **`setup_background_music`** — creates/reuses a singleton `__BGM__` GameObject with 2D loop AudioSource. Stops prior BGM if `replaceExisting=true` (default). Optional mixer routing. Solves "trilha sonora no fundo" in one call.
- **`create_reverb_zone`** — AudioReverbZone with min/maxDistance + preset (Cave, Hall, Forest, Hangar, Underwater, etc.).
- **`add_audio_listener`** — adds AudioListener to a GameObject. `removeOthers=true` (default) prevents Unity's "multiple listeners" warning.
- **`inspect_audio_setup`** — single-shot scene audio audit: AudioListeners (warns if 0 or >1), AudioSources bucketed by clip presence / 2D vs 3D / loop+playOnAwake / mixer group, AudioReverbZones, plus issues section at the bottom.

### Authoring helper

- `Editor/Authoring/AudioAuthor.cs` — every audio op in one place (~480 LOC), with `ResolveMixer` / `ResolveMixerGroup` helpers (supports 'MixerName/GroupName' syntax) and a `LoadClip` helper.

### Changed

- `ChangesetExecutor` registers 9 new audio op kinds (3 read-side: `list_audio_mixers`, `read_audio_mixer`, `inspect_audio_setup` go through tool wrappers only; 9 write ops batchable in `apply_changeset`).
- Default context preamble now has dedicated **Audio — sources & runtime / mixers / convenience** sections, plus a one-line **footstep pattern** pointing at the existing `add_animation_event` + custom `Footsteps.cs` recipe.
- Tool count is now **92** with Timeline / **87** without (was 80/75 in 0.7.1; +12 this release).

### Notes

- `create_audio_source` (basic 2D/3D toggle, from earlier) stays as-is — `create_audio_source_3d` is a separate convenience for the more demanding 3D point-emitter case.
- AudioMixer authoring (creating mixers/groups/snapshots programmatically) intentionally NOT included — Unity's editor APIs for that are limited and erratic. Author the mixer asset in the editor visually; the agent then routes / sets params / transitions snapshots via the new tools.
- Footsteps don't get a dedicated tool because they're solved end-to-end with `add_animation_event` (existing) + a tiny `Footsteps.cs` script the agent writes via filesystem and adds via `add_component` (after `refresh_assets`).

## [0.7.1] - 2026-05-10

### Fixed

- `TimelineAuthor.AddClip` failed to compile (CS1503) — `TimelineClip.asset` is typed `UnityEngine.Object`, not `PlayableAsset`, so passing it directly to the reflective `AssignSource(PlayableAsset, Object)` helper was rejected by the compiler. Cast via `as PlayableAsset` so the helper still works (and gracefully no-ops when the underlying asset isn't a PlayableAsset for some reason).

## [0.7.0] - 2026-05-10

### Added — Physics queries (4)

- **`raycast`** — Physics.Raycast / RaycastAll. Returns instanceId, name, tag, layer, point, normal, distance, collider type per hit. layerMask accepts int OR layer name OR array of names. hitTriggers: UseGlobal / true / false. multiple=true returns every hit sorted by distance.
- **`overlap_query`** — Physics.OverlapBox / OverlapSphere / OverlapCapsule. Lists colliders inside a volume.
- **`sweep_query`** — Physics.BoxCastAll / SphereCastAll / CapsuleCastAll. "What would I hit if I moved this volume."
- **`check_ground`** — convenience: raycast down from a GameObject, return whether something is below within `maxDistance` and the hit's tag + layer + collider type. Answers "is the player on the sidewalk / on a slope / floating in the air?" in one call.

### Added — Spatial / structural / classification (4)

- **`compute_bounds`** — world AABB encapsulating a GameObject and its child renderers (center, size, min, max).
- **`query_scene`** — combined scene filter: tag (exact), layer (int or name), componentTypes (must have ALL), nameContains, pathContains, withinRadius {center, radius}. sortBy: name / distance / depth. Replaces the need for several narrower discovery tools.
- **`classify_scene`** — single-shot scene summary bucketed by tag distribution, layer distribution, top component types (top 30), hierarchy stats (root/leaf count, max depth), spatial bounds + near/far origin counts, prefab status (linked / broken / unlinked). Cheaper than `read_scene` for getting a feel of a large scene.
- **`query_collision_layers`** — reads the per-layer collision matrix (Project Settings → Physics). With `layer` arg returns only the layers that DO collide with that one; without arg returns the full matrix.

### Added — Collider authoring (3)

- **`auto_fit_collider`** — adds one Box/Sphere/Capsule/Mesh collider sized to encapsulate the GameObject's renderer bounds (optionally including children). replaceExisting=true clears prior colliders (except CharacterController). isTrigger flags it as a trigger volume. Works in local space — respects parent transform scaling.
- **`add_compound_colliders`** — adds one primitive collider per child renderer. Compound colliders approximate complex meshes (buildings, vehicles, multi-part models) far better than a single AABB while staying cheap (no convex hull / VHACD pass).
- **`set_physics_material`** — assigns a PhysicsMaterial asset (friction/bounciness) to the Collider on a GameObject.

### Authoring helpers

- `Editor/Authoring/PhysicsAuthor.cs` — every physics query, scene classification, and collider authoring helper. ~520 LOC.

### Changed

- `ChangesetExecutor` registers 3 new collider op kinds: `auto_fit_collider`, `add_compound_colliders`, `set_physics_material` — all batchable.
- Default context preamble now includes **Physics queries**, **Scene classification & spatial query**, and **Collider authoring** sections.
- Tool count is now **80** with Timeline / **75** without (was 69 / 64 in 0.6.0; +11 this release).

### Notes

- Physics queries work in **edit mode** because Unity always builds a physics scene from active colliders. Runtime simulation (Rigidbody motion) only happens in Play Mode, but Physics.Raycast/Overlap/Cast are usable always.
- The agent can now confidently answer scene-comprehension questions like "list every enemy within 50m of the player", "which buildings have no colliders", "how many objects are tagged Untagged but have a Collider" via single `query_scene` / `classify_scene` calls.

## [0.6.0] - 2026-05-10

### Added — Mecanim (Tier 1 + 2)

**Discovery (5):**
- `list_animator_controllers` — `.controller` assets, filter by name/folder.
- `read_animator_controller` — full structure: parameters, layers, per-layer states + transitions (with conditions), AnyState transitions.
- `read_animation_clip` — length, frameRate, looping, curve bindings (path/property/type, capped at 30), animation events.
- `read_avatar` — humanoid avatar info: human-bone → bone-name mapping, twist values, skeleton transform count.
- `read_animator_state` — runtime state per layer: current state hash, normalizedTime, length, weight, parameter values.

**Runtime (2):**
- `set_animator_parameter` — float/int/bool/trigger setter (auto-detects type from controller).
- `play_animator_state` — `Animator.Play(state, layer, normalizedTime)`.

**Authoring (9):**
- `create_animator_controller` — new `.controller` asset (auto-creates parent folders).
- `add_animator_parameter` — Float/Int/Bool/Trigger with optional default.
- `add_animator_state` — with optional motion (clip GUID/path), speed, isDefault.
- `add_animator_transition` — from→to (or AnyState→to) with hasExitTime/duration/offset and conditions array `[{parameter, mode, threshold}]`.
- `add_animator_layer` — name, weight, blendingMode (Override/Additive), optional avatar mask.
- `create_blend_tree` — Simple1D / SimpleDirectional2D / FreeformDirectional2D / FreeformCartesian2D / Direct, with blendParameter (+ Y for 2D).
- `add_blend_tree_motion` — `threshold` for 1D, `position: [x,y]` for 2D.
- `add_animation_event` — function call at time T with optional string/float/int parameter (DontRequireReceiver).
- `assign_controller_to_animator` — sets Animator.runtimeAnimatorController on a scene GameObject.

### Added — Timeline (Tier 3) in secondary asmdef

New `com.maicon.scene-analyzer.Editor.Timeline` asmdef gated by `defineConstraints: ["SCENE_ANALYZER_HAS_TIMELINE"]` with `versionDefines` on `com.unity.timeline >= 1.0.0`. Doesn't break projects without the Timeline package — the asmdef simply doesn't compile, the 5 tools are absent.

- `create_timeline_asset` — new TimelineAsset (.playable).
- `add_timeline_track` — Animation/Audio/Activation/Control/Signal/Marker (or full custom TrackAsset type name).
- `add_timeline_clip` — start/duration on a named track, optional source asset (AnimationClip → AnimationTrack auto-bound; generic reflection assignment for other PlayableAsset shapes).
- `assign_timeline_to_director` — sets `PlayableDirector.playableAsset` on a GameObject (auto-adds the component if missing).
- `play_timeline` — play / stop / pause / resume / evaluate(time) on the director.

### Authoring helpers

- `Editor/Authoring/AnimationAuthor.cs` — all Mecanim helpers in one place (16 ops).
- `Editor/Timeline/TimelineAuthor.cs` — all Timeline helpers (5 ops).
- `Editor/Timeline/TimelineBootstrap.cs` — `[InitializeOnLoad]` registers tools + changeset op kinds when SCENE_ANALYZER_HAS_TIMELINE is defined.

### Changed

- `ChangesetExecutor` registers 11 new Mecanim op kinds (main asmdef) + 5 new Timeline op kinds (Timeline asmdef registers them at editor load).
- Default context preamble updated with **Animation (Mecanim)** and **Timeline** sections, listing every new tool grouped by intent.
- Tool count is now **69** when Timeline is installed (was 48 in 0.5.4; +21 this release: 16 Mecanim + 5 Timeline). Without Timeline: 64.

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
