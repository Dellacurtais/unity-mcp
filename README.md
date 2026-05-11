# Scene Analyzer (MCP) — Unity Plugin

Real-time scene analysis, validation, and authoring for Unity, exposed to an AI coding agent (the D:\MCP coder) over MCP **and** chatted with directly from inside the editor.

## What you get

Two windows in Unity:

- **Window → Scene Analyzer**: turns the plugin into an MCP server (`streamable_http` on `127.0.0.1:8765`), shows scene snapshot + issues, and gates writes via a kill switch.
- **Window → Coder Chat (Scene Analyzer)**: chat panel that talks to the D:\MCP coder runtime via SSE. The dev never has to leave Unity. The coder, on the other side, calls back into the plugin's MCP server for tools — both windows work in tandem.

## Capabilities

Inside the Unity Editor:

- **Reads** the active scene (hierarchy, components, transforms, serialized properties)
- **Analyzes** performance (geometry, draw calls, lighting, script cost, hierarchy depth)
- **Validates** the scene (missing references, required fields)
- **Discovers** project assets via an in-memory catalog (prefabs, materials, meshes, audio clips, …)
- **Screenshots** the active SceneView and returns a PNG so the LLM can visually reason about appearance
- **Authors** scene content (primitives, prefab instances, lights, cameras, particle systems, audio sources, post-processing volumes) — with full Undo support
- **Tunes** rendering (camera fov/clip planes, ambient + fog + skybox, color grading, bloom, vignette, tonemapping, etc.)
- **Drives editor menus and static APIs** — `execute_menu_item` invokes any `[MenuItem]` (built-in, your own, or third-party like Probuilder/Odin); `invoke_static_method` calls public/non-public static methods reflectively. Powerful — gated by the same `Allow writes` kill switch.
- **Controls play mode** — enter/exit/pause/step the editor, set `Time.timeScale`. Combined with the runtime command bridge below, this lets the agent close its own iteration loop: enter paused on frame 0 → script a few gameplay actions → screenshot → read log delta → exit.
- **Captures the Game view** — `screenshot_game_view` (active main / overlay camera) and `screenshot_from_pov` (spawn a temporary camera at any position+rotation, render, destroy — no scene mutation). Includes URP/HDRP post-fx when in play mode.
- **Runs scripted gameplay via the McpCommandBridge** — install a small MonoBehaviour, register handlers in your own scripts (`McpCommandBridge.Register("jump", ...)`), then the agent sends timeline scripts (`send_runtime_commands`) with per-step screenshots + log deltas in one round-trip. See [`Runtime/Bridge/README.md`](Runtime/Bridge/README.md).

Reactive: the plugin keeps a live snapshot driven by `ObjectChangeEvents`, with a 250 ms debounce.

## How the agent talks to the plugin

The plugin runs an MCP server (`streamable_http`) on `127.0.0.1:8765`. The agent connects with a bearer token, lists the tools, and calls them.

### Read

| Tool | Purpose |
| --- | --- |
| `read_hierarchy` | Flat list: instanceId, name, parent, components types. Cheap. |
| `read_scene` | Hierarchical tree with transforms; optional component dump. Bounded by depth/maxNodes. |
| `read_component` | Serialized properties of one (or every) component on a GameObject. |
| `get_project_meta` | Unity version, render pipeline, color space, build target. |
| `screenshot_scene_view` | Renders the active SceneView camera and returns a base64 PNG (MCP image content block). |

### Analyze

| Tool | Purpose |
| --- | --- |
| `analyze_performance` | Geometry, draw calls, lighting, script cost, hierarchy depth. |
| `validate_scene` | Missing references, broken scripts, required-field violations. |

### Discover

| Tool | Purpose |
| --- | --- |
| `list_assets` | Paginated list filtered by type / name / folder. |
| `search_assets` | Quick name-substring search with optional type filter. |
| `read_asset_metadata` | Per-asset details (mesh tris, texture size, audio length, …). |
| `find_references_to_asset` | Which scene GameObjects reference a given asset. |

### Author — create

| Tool | Purpose |
| --- | --- |
| `create_primitive` | Cube/Sphere/Capsule/Cylinder/Plane/Quad. (URP/HDRP: follow up with `set_material`.) |
| `create_empty` | Empty GameObject (grouping). |
| `instantiate_prefab` | Instantiate by GUID/path with prefab link preserved. |
| `create_light` | Directional/Point/Spot/Area/Disc light. |
| `create_camera` | Camera, optionally tagged MainCamera. |
| `create_audio_source` | AudioSource with optional clip assignment. |
| `create_particle_system` | Templates: smoke, fire, sparks, dust, trail. |
| `create_volume`* | URP/HDRP Volume + VolumeProfile asset. |

### Author — modify

| Tool | Purpose |
| --- | --- |
| `set_transform` | Position/rotation/scale (world or local). |
| `set_camera_props` | fov, clip planes, clear flags, background, depth, ortho. |
| `set_property` | Generic SerializedProperty setter on any component. |
| `add_component` | Add a Component by type name. |
| `set_material` | Assign material to a Renderer slot. |
| `parent_object` | Reparent (preserve world position or snap). |
| `delete_object` | Destroy GameObject (recursive, with safety threshold). |

### Author — post-processing & render

| Tool | Purpose |
| --- | --- |
| `add_post_effect`* | Add Bloom/Tonemapping/ColorAdjustments/Vignette/etc. to a Volume profile. |
| `set_post_effect_property`* | Set any VolumeParameter (resolves URP/HDRP/SRP-Core types). |
| `apply_color_grading`* | Convenience: exposure / contrast / saturation / hue / color filter in one call. |
| `set_render_settings` | Ambient mode + colors, fog, skybox material, reflection intensity. |

\* = ships in the secondary asmdef gated by SRP Core (see "Render pipelines" below).

### Batch & selection

| Tool | Purpose |
| --- | --- |
| `apply_changeset` | Run N ops as ONE Undo group. Supports `preview: true` for dry-run. Ops can carry a `label` and later ops can reference earlier results via `{ ref: <label> }` (e.g. set `parent` on children to the labeled root). |
| `select_object` | Select in Hierarchy and optionally frame in SceneView. |

### Reflect — menu items & static methods

| Tool | Purpose |
| --- | --- |
| `list_menu_items` | Enumerates `[MenuItem]` attributes in loaded assemblies (filter by path prefix or assembly). Built-in Unity menus live in C++ and are NOT listed here; `execute_menu_item` still runs them when the path is known. |
| `describe_static_method` | Returns parameter and return-type info for every static overload of a method. Use before `invoke_static_method` to learn the right `argTypes` and arg shapes. |
| `execute_menu_item` | Wraps `EditorApplication.ExecuteMenuItem(path)`. Covers every `[MenuItem]` — your own, built-in, or third-party (Probuilder, Odin, asset-store). |
| `invoke_static_method` | Reflectively calls any public/non-public static method. Coerces JSON args to .NET types (Vector3/Quaternion/Color, `UnityEngine.Object` via `instanceId`, enums by name). |

> ⚠️ **Powerful tools.** `execute_menu_item` and `invoke_static_method` can run arbitrary editor code — they're how you reach tools that don't have a dedicated MCP wrapper. Both honour the same `Allow writes` kill switch as the rest of the write surface; turn it off when not authoring. Menu items that open MODAL dialogs (Build Settings, File/Save..., Player Settings) BLOCK the editor — avoid them. Generic and ref/out methods are explicitly unsupported.

### Play mode — lifecycle, capture, monitoring

| Tool | Purpose |
| --- | --- |
| `get_play_state` | `isPlaying / isPaused / isCompiling / frameCount / time / timeScale` snapshot — precheck before any play-mode action. |
| `enter_play_mode` | Enters play mode and (default) waits for the first frame. Supports `paused: true` for frame-0 inspection. Refuses if compiling. Cooperative wait via `playModeStateChanged`. |
| `exit_play_mode` | Exits play mode; waits for `EnteredEditMode`. |
| `set_play_paused` | Toggles `EditorApplication.isPaused` — freeze the frame before a screenshot or sequence of steps. |
| `step_frame` | Advances N frames while paused (auto-pauses if needed). Deterministic frame-by-frame inspection. |
| `set_time_scale` | Sets `Time.timeScale`. Auto-resets to 1.0 on `ExitingPlayMode` so the editor isn't left stuck at slow speed. |
| `screenshot_game_view` | Renders the active game-view camera into a PNG/JPG. In play mode, waits for end-of-frame so URP/HDRP post-fx is included. Edit-mode fallback reflects over `UnityEditor.GameView.m_TargetTexture`. |
| `screenshot_from_pov` | Spawns a hidden temporary camera at `{ position, lookAt | rotationEuler, fov, ... }`, renders it, destroys it. No GameObject persists. Best way to inspect from a custom angle without moving the player. |
| `get_active_cameras` | Lists every enabled `Camera` with instanceId, name, depth, tag, position, forward, URP renderType (when present). |
| `mark_log_anchor` | Returns `ConsoleCapture.CurrentSeq`. Pair with `read_runtime_log_delta` to isolate the logs produced by one action. |
| `read_runtime_log_delta` | Returns console entries since a `sinceSeq` anchor (severity-filtered). Use to correlate a screenshot with the errors/warnings emitted around it. |

### Runtime bridge — scripted gameplay

Requires you to call `McpCommandBridge.Register("name", handler)` in your own scripts to wire commands to your game. See [`Runtime/Bridge/README.md`](Runtime/Bridge/README.md) for the quick-start.

| Tool | Purpose |
| --- | --- |
| `bridge_status` | Whether the bridge GameObject is in the active scene, what handlers the game has registered, queue depth, last dispatch. |
| `bridge_install` | Adds `McpCommandBridge` to the active scene (idempotent). With Undo + scene-dirty. Sets `DontDestroyOnLoad` by default. |
| `list_bridge_handlers` | Names of currently registered handlers. |
| `send_runtime_command` | Invokes one handler. Singleton form — use for ad-hoc debug pokes. |
| `send_runtime_commands` | **Primary.** Sends a timeline of `{ name, payload?, delayFrames?, captureScreenshotAfter?, markLogAnchorBefore? }` steps. The runtime executes them sequentially, captures per-step screenshots and log deltas as requested, and returns everything in one round-trip — including image content blocks the LLM can see inline. Use to script a whole gameplay sequence (load level → move → wait → shoot → screenshot) without N round-trips. |

## Embedded chat panel (Model B)

`Window → Coder Chat (Scene Analyzer)` is a chat client that talks directly to the coder runtime in D:\MCP — no need to leave Unity to converse with the agent.

Architecture in one picture:

```
┌─────────────────────────────────────────────────────────────┐
│                    Unity Editor (host)                      │
│                                                             │
│  Window → Coder Chat ─────────► HTTP/SSE                    │
│  (dev types here)               POST /api/coder/sessions/*  │
│                                       │                     │
│                                       ▼                     │
│                              ┌────────────────┐             │
│                              │ D:\MCP coder   │             │
│                              └────────┬───────┘             │
│                                       │ MCP                 │
│                                       ▼                     │
│  Window → Scene Analyzer ◄──── ext_unity_editor__*          │
│  (MCP server localhost:8765)                                │
└─────────────────────────────────────────────────────────────┘
```

The dev's message goes to the coder, which streams text + tool calls back. When the agent decides to operate on the scene, it calls the plugin's MCP server (also running locally) — no network roundtrip, just two ports on `127.0.0.1`.

### First run

1. Open **Window → Scene Analyzer** → Generate token → Start MCP server.
2. In your D:\MCP dashboard, register the Unity MCP server: transport `streamable_http`, URL `http://127.0.0.1:8765/mcp`, bearer token from step 1.
3. Open **Window → Coder Chat (Scene Analyzer)**.
4. Configure the coder URL (defaults to `http://localhost:3333`) and, if `MCP_AUTH_ENABLED=true`, paste a bearer token issued by `/api/admin/tokens`.
5. Pick agent (`build`, `explore`, `plan`, `admin`) and tool tier (`auto`, `bootstrap`, `navigate`, `full`).
6. Type and Send. Replies stream back; tool calls appear inline. Cancel via the button. New session via the **New** button (clears the local transcript and forces a fresh server-side session on the next message).

### What you see in the panel

- **You / Coder bubbles** — your messages and the assistant's replies.
- **System notes** — plan submissions, ask_user prompts, tasks, notifications.
- **Tool call lines** — each `ext_unity_editor__*` (and any other MCP tool) call is logged inline with name, duration and a truncated result preview.

## Install

**Required dependency: Newtonsoft Json.** This plugin uses `JObject` extensively. The package `com.unity.nuget.newtonsoft-json` must be installed. Without it you'll get a wall of `CS0122 'JObject' is inaccessible` errors and a Burst `Failed to resolve assembly` complaint about the PostFx asmdef.

### Recommended: UPM via Git URL

In Unity: **Window → Package Manager → + → Add package from git URL** →

```
https://github.com/<your-org>/<your-repo>.git?path=extensions/unity-scene-analyzer
```

UPM resolves the `com.unity.nuget.newtonsoft-json` dependency automatically.

### Alternative: copy under `Packages/`

Drop the `unity-scene-analyzer/` folder into your project's `Packages/` directory. Same UPM treatment as above — dependencies are resolved.

### If you copied under `Assets/Plugins/` (or any other Assets path)

UPM does **not** process `package.json` for things under `Assets/`, so you must install Newtonsoft yourself. Either:

1. **Window → Package Manager → '+' → Add package by name →** `com.unity.nuget.newtonsoft-json` (any 3.x, e.g. 3.2.1).
2. Or edit `Packages/manifest.json` and add the line:

   ```json
   "com.unity.nuget.newtonsoft-json": "3.2.1"
   ```

Then let Unity reimport. If you still see compile errors, delete `Library/ScriptAssemblies/` and let Unity rebuild.

### Verifying the install worked

The plugin ships a tiny diagnostics asmdef (`com.maicon.scene-analyzer.Editor.Diagnostics`) with no references — it always compiles and prints a guided error to the Console if Newtonsoft is missing. If you see no errors after import, you're good.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `CS0122 'JObject' is inaccessible due to its protection level` (lots of files) | `com.unity.nuget.newtonsoft-json` not installed; compiler picks up the internal copy from `com.unity.visualscripting` instead. | Install the official Newtonsoft package (see Install above). The `NewtonsoftCheck` diagnostic logs this automatically. |
| `Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly 'com.maicon.scene-analyzer.Editor.PostFx'` (from Burst) | Side-effect of the JObject error: PostFx asmdef can't compile, so its DLL is missing, so Burst's reference walk fails. | Fix Newtonsoft first; both errors clear together. If they persist, delete `Library/ScriptAssemblies/` and reimport. |
| `create_volume` / `add_post_effect` not visible in tool list | Project doesn't have URP or HDRP installed (no SRP Core), so the gated PostFx asmdef is skipped. | Install URP via Package Manager, or use the Built-in-only subset (28 tools). |
| Window → Coder Chat opens but Send fails | D:\MCP coder runtime not running, or wrong URL/token. | Start the runtime (`pnpm dev` etc. depending on your D:\MCP setup), confirm `http://localhost:3333/api/health` (or your configured port). If `MCP_AUTH_ENABLED=true`, paste a token issued by `/api/admin/tokens`. |
| Plugin asmdef compiles but nothing happens on Unity reload | `Allow writes` toggle off, server not started, or token mismatch with the dashboard's MCP server entry. | Open Window → Scene Analyzer, verify Start is green and the token matches what's saved in Admin → MCP Servers. |

## Render pipelines

The plugin ships in several assemblies. All secondary asmdefs register their tools via `delayCall` against `Bootstrap.ToolRegistry`, so deleting a folder cleanly disables only its tools.

- **`com.maicon.scene-analyzer.Editor`** — main editor functionality. Compiles on every Unity 6.1 project regardless of pipeline.
- **`com.maicon.scene-analyzer.Editor.PostFx`** — Volume / post-effect tools. Gated by `defineConstraints: ["SCENE_ANALYZER_HAS_SRP_CORE"]` (defined when `com.unity.render-pipelines.core` 10.0.0+ is installed). On a Built-in-only project this asmdef simply doesn't compile — the plugin still works, just without `create_volume` / `add_post_effect` / `set_post_effect_property` / `apply_color_grading`. Install URP or HDRP to unlock them.
- **`com.maicon.scene-analyzer.Editor.Play`** — play-mode lifecycle + Game-view capture (11 tools). No gating; works on every Unity 6.1+ install regardless of pipeline. Edit-mode Game-view capture uses reflection (`UnityEditor.GameView.m_TargetTexture`) and degrades gracefully on Unity-version drift.
- **`com.maicon.scene-analyzer.Bridge`** (runtime) + **`com.maicon.scene-analyzer.Editor.Bridge`** — the `McpCommandBridge` MonoBehaviour and its 5 editor-side tools. Runtime asmdef ships cross-platform so the bridge can also live in player builds if you want.

Other notes:

- `create_primitive` and `create_particle_system` use Unity's legacy default materials, which appear pink in URP/HDRP. Follow with `set_material` (URP/HDRP-Lit) when on a non-Built-in pipeline.
- `create_light` intensity scale differs across pipelines — Built-in arbitrary 0..8, URP lumens for Point/Spot, HDRP physical units. Consult `get_project_meta` first.

## Safety

- HTTP listener binds to `127.0.0.1` only.
- Bearer token required on every MCP request.
- **Allow writes** toggle in the Scene Analyzer panel kills all write tools when off — including `execute_menu_item` and `invoke_static_method`.
- `delete_object` refuses recursive deletes of large subtrees without `force: true`.
- All write ops register with `Undo`; `apply_changeset` collapses N ops into one undo step.
- Plugin never auto-saves the scene.
- Chat panel never executes commands locally — all execution flows through the coder runtime in D:\MCP.
- `execute_menu_item` and `invoke_static_method` are intentionally unsandboxed: they let the agent drive arbitrary editor code (your own scripts, asset-store tools). The localhost bind + bearer token + `Allow writes` toggle are the only guard rails.

## License

TBD.
