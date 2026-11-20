# Unity MCP Transport

[![Release static validation](https://github.com/Dellacurtais/unity-mcp/actions/workflows/release-static.yml/badge.svg)](https://github.com/Dellacurtais/unity-mcp/actions/workflows/release-static.yml)

Let AI agents (Claude Code, Codex, any [Model Context Protocol](https://modelcontextprotocol.io) client) inspect and author a Unity project through the Editor itself: scenes, prefabs, assets, packages, builds, tests, Play Mode and more, with stable object identities, Undo-safe writes, capability gates and explicit saves.

The package (`com.maicon.scene-analyzer`) runs inside the Unity Editor. A Python **broker** serves the MCP endpoint and keeps it alive while Unity compiles, reloads its domain or restarts, so agents never lose their connection or their work.

> Leia em português: [Documentation~/README.pt-BR.md](Documentation~/README.pt-BR.md)

## Highlights

- **Hundreds of Editor tools** for scenes, prefabs, assets and importers, Package Manager, builds, tests, UI, input, rendering, 2D, terrain, animation, audio, profiling and Play Mode, plus package-gated modules (URP/HDRP, Cinemachine, Timeline, ProBuilder, Addressables, ...). `get_tool_inventory` lists the exact surface of your project.
- **Safe by default**: loopback-only, bearer token, capability switches, optional two-step confirmation for high-risk tools, Undo-aware mutations, and nothing is saved unless a save tool is called.
- **Reload-proof broker**: a per-user supervisor keeps the broker running across Domain Reloads and Editor restarts, restarts it when it stops answering, and cleans up orphaned Unity processes.
- **No lost long calls**: a call that is still running after 45 s answers `status: running` with a `requestId`; `broker_wait_request` returns the real result later.
- **Editor lifecycle from outside**: close (only with clean scenes), reopen and restart the Editor, and see the native dialogs that block it.

## Requirements

| | |
| --- | --- |
| Unity | 6000.1 through 6000.6. Developed and tested on **6000.6.7f1**; Windows is the primary platform (see [Supported contract](#supported-contract)) |
| Dependency | `com.unity.nuget.newtonsoft-json` 3.x (resolved automatically by UPM) |
| Python | 3.10 or newer, for the broker. Unity creates a private virtual environment for it. |

## Install

In Unity, open **Window → Package Manager → + → Add package from git URL** and enter:

```text
https://github.com/Dellacurtais/unity-mcp.git
```

Append `#vX.Y.Z` to pin a release tag, for example `https://github.com/Dellacurtais/unity-mcp.git#v0.19.1`. To work on the package itself, clone the repository into your project's `Packages/` folder instead (an *embedded* package). Do not copy it under `Assets/`: UPM then ignores its `package.json`, and `com.unity.nuget.newtonsoft-json` has to be installed by hand.

## Quick start

1. Open **Window → Unity MCP Transport**.
2. Under **Managed Python broker**, click **Install**. Unity finds Python (`py -3`, `python3` or `python`, or the executable you pick), copies the broker to `%LOCALAPPDATA%\UnityMcp\Broker\installations\<version>`, and installs its pinned dependencies in a private virtual environment. The package folder is never modified.
3. Click **Start**. From now on Unity starts the broker by itself whenever the Editor loads and none is running.
4. Under **AI clients**, click **Install** beside **Claude Code** or **ChatGPT/Codex**. This writes the endpoint into the client's user configuration and stores the token in the per-user environment variable `UNITY_MCP_BROKER_TOKEN`. Restart the client.
5. Ask the agent to call `get_server_health`, then `self_test`.

`GET http://127.0.0.1:8765/health` (no token needed) reports the broker version, whether it is persistent, its supervisor, the bridge state of the active project and the event-loop lag.

## Connecting MCP clients

The endpoint is `http://127.0.0.1:8765/mcp/` (Streamable HTTP, JSON responses) and every request needs `Authorization: Bearer <token>`. The token lives in the per-user environment variable `UNITY_MCP_BROKER_TOKEN`; Unity, the broker and the clients all read it from there.

**Claude Code** (`~/.claude.json`, written by the **Install** button):

```json
{
  "mcpServers": {
    "unity-mcp": {
      "type": "http",
      "url": "http://127.0.0.1:8765/mcp/",
      "headers": { "Authorization": "Bearer ${UNITY_MCP_BROKER_TOKEN}" },
      "headersHelper": "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:/Users/<you>/AppData/Local/UnityMcp/Broker/bin/token-header.ps1\""
    }
  }
}
```

The `headersHelper` prints the current token on every connection and after a 401, so regenerating the token never requires restarting Claude Code. Sessions that were already open pick it up after `/mcp` → Reconnect.

**Codex / ChatGPT desktop** (`~/.codex/config.toml`):

```toml
[mcp_servers.unity]
url = "http://127.0.0.1:8765/mcp/"
bearer_token_env_var = "UNITY_MCP_BROKER_TOKEN"
```

Any other MCP client works the same way: Streamable HTTP POST to the endpoint with the bearer token. Clients that send a newer `MCP-Protocol-Version` are offered `2025-06-18`.

## How the broker stays up

```text
MCP clients ──HTTP──▶ broker (main.py) ◀──WebSocket── Unity bridge (in each Editor)
                         ▲
                 supervisor.py (one per Windows user)
```

- **Supervisor.** `supervisor.py` holds a named mutex (one per user), runs the broker as a child in persistent mode, checks `/health` every 5 s and restarts the broker when it exits or fails four checks in a row, with backoff. The broker lives in a kill-on-close job object, so nothing it started outlives the supervisor. It moves itself to a newer installation when `current.json` changes, and **Stop** asks it to quit through `runtime/supervisor.stop`. The supervisor does not depend on any Editor: closing or reopening Unity does not take the endpoint down.
- **Watchdog.** Inside the broker, a thread outside the event loop dumps all stacks to `logs/broker.watchdog.log` when the loop stalls for 15 s and exits after 90 s, so the supervisor replaces a hung broker.
- **Domain Reload and Editor restarts.** Reads and idempotent tools wait for Unity to reconnect (up to 300 s) instead of failing; writes wait while nothing has been sent yet. A project whose Editor has been closed for a while fails fast with `UNITY_OFFLINE` and a hint.
- **Sessions and tokens.** If the broker restarts, an existing `Mcp-Session-Id` is adopted instead of answered with 404, because some clients never re-initialize. Unity adopts the per-user token on every load; the broker accepts it even if it was started with an older value.
- **Background Unity processes** (Asset Import Workers, batch-mode Editors) do not open MCP connections. Set `UNITY_MCP_ALLOW_BATCH=1` when you really want MCP in batch mode.
- **Logs and state** are under `%LOCALAPPDATA%\UnityMcp\Broker`: `logs/broker.log`, `logs/supervisor.log`, `logs/broker.stderr.log`, `logs/orphans.log`, `runtime/supervisor.json` and `data/broker.sqlite3` (the request and job ledger).

Manual mode remains supported: install `Broker~/requirements.txt` in any environment, set `UNITY_MCP_BROKER_TOKEN`, then run `python Broker~/supervisor.py` (or `python Broker~/main.py` for a broker without supervision) and click **Connect** in Broker mode. See [Broker~/README.md](Broker~/README.md).

## Long-running calls and jobs

MCP clients give up on calls that stay silent (Claude Code after 5 minutes, Codex after 60 s by default), so the broker never holds an answer longer than 45 s:

1. A call still running after 45 s answers `status: running` with `requestId` (and `unityJobId` when Unity started a job). It keeps running.
2. Call `broker_wait_request {"requestId": "..."}`. It waits up to 55 s and returns the original call's own result, or `status: running` again. Never repeat the original call.
3. Lost the id? `broker_list_requests` and `broker_list_jobs` list recent work, and `get_test_job` without `jobId` returns the latest test run.

Builds, tests, reimports, Package Manager operations and Addressables builds are Unity jobs: the broker follows them for up to 6 hours. Run long menus (for example a custom build menu) with `execute_menu_item {"menuPath": "...", "deferred": true}`, which returns a job instead of freezing the Editor before it can answer.

While a job owns the Editor (tests, builds, reimports, refresh/compile), other writes answer `RESOURCE_BUSY` with the job id at once instead of queueing for the whole job.

A job can be `Queued`, `Running`, `CancelRequested`, `Completed`, `Failed`, or `Cancelled`. Some Unity APIs are synchronous once started; their preview/result explicitly states when cancellation is not possible.

Receipts are written atomically to the project's `Library/UnityMcp/jobs.json`. Interrupted jobs restore as failed with `OUTCOME_UNKNOWN`, requiring inspection of their effects; they are not silently replayed. A completed operation retains its known result even when cancellation arrives late. This journal is recovery evidence, not resumable execution of Unity's native APIs.

## Editor lifecycle

| Tool | What it does |
| --- | --- |
| `broker_editor_status` | Works without the Editor: for each known project, whether its Editor process runs, the bridge state (ready, reloading, blocked), open native dialogs with their buttons, and free commit memory. |
| `broker_close_editor` | Quits through the Editor's own `editor_quit` (File > Exit). Refuses with dirty or untitled scenes (unless `saveDirtyScenes: true`), a dirty prefab, Play Mode, a build, compilation or a running job. Never kills the process. |
| `broker_open_editor` | Starts the Editor for a project that connected before, with the same `Unity.exe`, only when nothing has it open and enough memory is free. |
| `broker_restart_editor` | Close followed by open, as one job. |
| `broker_press_dialog_button` | Presses one exactly named button of a native dialog that blocks a known Editor. Ask a human which button first. |

All of them preview only, unless called with `confirm: true`. Close, open and restart return a job; follow it with `broker_wait_job`.

## Process cleanup

The supervisor runs `Broker~/orphans.py` every 10 minutes, and `broker_cleanup_orphans` runs it on demand (`apply: false` only lists). It decides by process tree and start time, never by name alone:

- It closes a Unity helper (AssetImportWorker, UnityShaderCompiler, bee, ILPP, anything under an Editor's `Editor\Data`) or a stale broker only when no live Editor or supervisor is above it.
- It never closes an Editor, Unity Hub or anything under it, or the licensing client, and it leaves anything younger than two minutes alone.
- It removes old broker installations that nothing references or runs from.

Set `UNITY_MCP_BROKER_ORPHAN_CLEANUP=report` to only log decisions, or `off` to disable it. Run it by hand with `python Broker~/orphans.py` (add `--apply` to act).

## Configuration

Unity passes the main settings to the broker; the variables below override them for a manually started broker or supervisor.

| Variable | Default | Meaning |
| --- | --- | --- |
| `UNITY_MCP_BROKER_TOKEN` | — | Bearer token. Also the per-user variable clients read. |
| `UNITY_MCP_BROKER_HOST` / `_PORT` | `127.0.0.1` / `8765` | Loopback address of the endpoint. |
| `UNITY_MCP_BROKER_SYNC_WAIT` | `45` | Seconds before a call answers `status: running`. |
| `UNITY_MCP_BROKER_MAX_WAIT_CALL` | `55` | Longest single `broker_wait_request` / `broker_wait_job`. |
| `UNITY_MCP_BROKER_JOB_WAIT` | `21600` | How long a Unity job is followed for a pending call. |
| `UNITY_MCP_BROKER_TIMEOUT` | `900` | Per-dispatch timeout for a Unity tool to answer. |
| `UNITY_MCP_BROKER_RECONNECT_GRACE` | `300` | How long calls wait for Unity to reconnect. |
| `UNITY_MCP_BROKER_STALL_DUMP` / `_STALL_EXIT` | `15` / `90` | Watchdog thresholds in seconds (`0` disables). |
| `UNITY_MCP_BROKER_ADOPT_SESSIONS` | `1` | Adopt unknown session ids after a restart. |
| `UNITY_MCP_BROKER_ORPHAN_CLEANUP` | `apply` | `apply`, `report` or `off`. |
| `UNITY_MCP_BROKER_HISTORY_DAYS` | `7` | Retention of read payloads in the ledger. |
| `UNITY_MCP_BROKER_WRITE_HISTORY_DAYS` | `14` | Retention of write payloads (receipts are kept). |
| `UNITY_MCP_BROKER_SESSION_IDLE_SECONDS` / `_MAX_SESSIONS` | `7200` / `1024` | Session expiry and capacity. |
| `UNITY_MCP_BROKER_FOCUS_ASSIST` | `1` | Briefly focus Unity when a tool needs Editor updates. |
| `UNITY_MCP_ALLOW_BATCH` (Unity) | unset | `1` lets batch-mode Editors open the bridge. |
| `UNITY_MCP_PYTHON` (Unity) | unset | Python used to install the broker. |

## Editor windows

The package adds these Editor windows:

- **Window → Unity MCP Transport** installs, starts and stops the managed broker, connects the Unity bridge, shows bridge identity/state, writes the AI client entries and exposes safety switches.
- **Window → Asset Modeler (Unity MCP)** opens task-focused cleanup, size normalization and save-copy flows. Advanced modeling and semantic body splitting remain available. See [the cleanup/size workflow](Documentation~/asset-modeler-preparation.md).
- **Window → Asset Modeler → Rig & Animation Studio** separates rig diagnostics, skin weights, skeleton/bind, clips/poses, retarget, IK/bake, and auto-rig into seven focused pages with Scene View tooling.

The MCP surface covers scene and asset inspection, safe authoring, project settings, multi-scene and Prefab workflows, importers, Package Manager, builds, tests, UI, input, rendering, 2D, terrain, animation, audio, profiling, Play Mode, runtime gameplay commands, mesh analytics, and optional Unity ecosystem packages.

## Supported contract

| Area | Supported contract |
| --- | --- |
| Unity | 6000.1 through 6000.6. Developed and tested on 6000.6.7f1; 6000.3.5f2 has a CI fixture; earlier branches compile through version symbols and are not revalidated on every release |
| Identity on 6000.6 | `EntityId` and `SceneHandle` internally; the integer `instanceId`/`sceneHandle` fields stay for compatibility as opaque session handles (see the [6000.6 migration note](Documentation~/unity-6000.6-migration.md)) |
| MCP | `2025-06-18` preferred; `2025-03-26` and `2024-11-05` accepted; newer `MCP-Protocol-Version` headers are offered `2025-06-18` |
| Transport | A supervised FastAPI broker: Streamable HTTP for clients, an outbound WebSocket bridge from each Editor |
| Authentication | Bearer token required |
| Compatibility | Existing tool names and legacy `instanceId` inputs remain supported additively |
| Identity | Stable `ToolTargetRef` using GlobalObjectId, asset GUID/local file ID, scene identity, hierarchy path, component type, and ordinal |
| Persistence | Mutations mark the affected scenes/assets dirty; only explicit save/build tools persist changes |
| High-risk operations | Capabilities plus a single-use confirmation token bound to tool, normalized arguments, project, and expiry |

## Operational tools

The registry is the source of truth. Do not maintain or rely on a hand-written tool count.

| Tool | Purpose |
| --- | --- |
| `get_tool_inventory` | Registry-generated, paginated inventory. Each row includes module, assembly, category, mode, capabilities, risk, idempotence, preview relationship, confirmation policy, and optional schemas. Filters include module, category, capability, risk, mode, confirmation, and write-only. |
| `get_server_health` | Reports package/server version, supported protocol versions, registry count/hash, loaded modules, compilation state, dispatcher queue depth, in-flight requests, retained jobs, domain reload time, capability policy, and optional-package compatibility. |
| `self_test` | Read-only validation of registry uniqueness/count/hash, input/output schemas, descriptors, mutability, high-risk confirmation, optional-module references, package/server version agreement, and protocol invariants. |

Example inventory calls:

```json
{ "module": "core", "limit": 250 }
```

```json
{ "capability": "Build", "confirmationRequired": true, "includeSchemas": true }
```

Run `self_test` after package installation, package changes, or a domain reload. Run `get_server_health` before long jobs or when an optional tool family is missing.

## Module inventory

The exact per-tool table is generated by `get_tool_inventory`; the table below documents module boundaries and support policy without freezing a count in Markdown.

| Module | Assembly or package gate | Mode | Capabilities / maximum risk | Preview and confirmation | Coverage |
| --- | --- | --- | --- | --- | --- |
| Core MCP and operations | main editor assembly | Any | Read; Low | None | Protocol negotiation, registry inventory, health, self-test, schemas, authorization, cancellation, jobs |
| Read / discover | Main editor assembly | Any | Read; Low | None | Hierarchy, scenes, components, assets, references, console and project metadata |
| Scene authoring | Main editor assembly | Edit Mode | SceneWrite, AssetWrite, Destructive; High | Destructive operations require confirmation | Create/modify/delete objects, materials, audio, animation, physics, render settings, changesets |
| Project | Main editor assembly | Mostly Edit Mode | Read, ProjectSettings; High | Project settings changes use preview/apply and confirmation | Editor context, compilation, tags/layers, quality and project settings |
| Scene | Main editor assembly | Edit Mode | SceneWrite; Medium | Save is explicit | Loaded scenes, create/open/close/activate/save, multi-scene identity |
| Prefab | Main editor assembly | Edit Mode | SceneWrite, AssetWrite, Destructive; High | Apply/revert/unpack follow descriptor confirmation policy | Prefab Stage, read/create/variant, overrides and unpack |
| Assets/importers/VCS | Main editor assembly | Edit Mode | AssetWrite, Destructive; High | Delete/import changes support preview/confirm or explicit apply | Dependencies, copy/move/delete, import settings, reimport jobs, editability |
| Packages | Main editor assembly | Edit Mode | PackageManager; Critical | `preview_*` then `confirm_*` with single-use token | List/search/add/remove packages and job polling |
| Build | Main editor assembly | Edit Mode | Build; Critical | `preview_build` then confirmed `start_build` | Profiles, validation, build jobs, cancellation and BuildReport |
| uGUI | `com.unity.ugui >= 1.0.0` | Edit Mode | Read, SceneWrite, Reflection; Critical | Event binding requires confirmation | Canvas/elements/layout/RectTransform/events/button binding |
| Input System | `com.unity.inputsystem >= 1.7.0` | Edit Mode | Read, AssetWrite, SceneWrite; Medium | Normal write authorization | InputActionAsset/maps/actions/bindings/control schemes/PlayerInput |
| Unity Test Framework | `com.unity.test-framework >= 1.1.0` | Any | Read, PlayControl; Medium | No destructive confirmation | List/run/poll/cancel EditMode and PlayMode tests |
| System tools | Main editor assembly | Edit Mode unless read-only | Read, SceneWrite, AssetWrite, ProjectSettings; High | Project settings follow confirmation policy | Rendering, materials, lighting, LOD, Physics2D, sprites, Tilemap, Terrain, UI Toolkit, animation assets, frame timing |
| Play | Play assembly | Any / Play Mode | PlayControl; Medium | Capability gate | Play lifecycle, pause/step/time scale, cameras, screenshots, runtime logs |
| Runtime bridge | Runtime + editor bridge assemblies | Play Mode | RuntimeCommand; Medium | Capability gate and bounded/cancellable scripts | Handler discovery and scripted gameplay sequences |
| Mesh analytics | Editor mesh analytics assembly + optional Python sidecar | Any | Read, AssetWrite; Medium | Importer mutations use normal write authorization | Mesh regions, sampling, highlights, sidecar segmentation and health |
| Asset Modeler | Editor/runtime modeling assemblies | Edit Mode | Read, SceneWrite, AssetWrite, Destructive; High | Smart split and auto-rig apply only after reviewed hashes and high-risk confirmation | Editable mesh contracts, channel fidelity, body-part analysis, rig diagnostics, BoneWeight1 painting/transfer, bind poses, pose libraries, clip transforms, retarget, native IK/bake and local/AI-assisted auto-rig |
| PostFx | `com.unity.render-pipelines.core >= 10.0.0` | Edit Mode | Read, SceneWrite, AssetWrite; Medium | Normal write authorization | Volumes and post-effect profiles |
| Timeline | `com.unity.timeline >= 1.0.0` | Edit/Play | Read, SceneWrite, AssetWrite; Medium | Normal write authorization | Timeline assets, tracks, clips, bindings and playback |
| NavMesh | `com.unity.ai.navigation >= 1.0.0` | Edit Mode | Read, SceneWrite; Medium | Normal write authorization | Surfaces, bake, modifiers and links |
| Cinemachine | `com.unity.cinemachine >= 3.0.0` | Edit Mode | Read, SceneWrite; Medium | Normal write authorization | Context, camera/brain discovery, camera creation/configuration and validation |
| TextMeshPro | `com.unity.ugui >= 2.0.0` or legacy `com.unity.textmeshpro >= 3.0.0` | Edit Mode | Read, SceneWrite; Medium | Normal write authorization | TMP UI/world text, font assets, creation/configuration and validation |
| Shader Graph | `com.unity.shadergraph >= 17.0.0` | Edit Mode | Read, AssetWrite, Destructive; Critical | Source replacement requires confirmation and expected SHA-256 | Graph context, source/structure/validation and guarded replacement |
| ProBuilder | `com.unity.probuilder >= 5.0.0` | Edit Mode | Read, SceneWrite; Medium | Normal write authorization | Shape creation, mesh refresh/context |
| Localization | `com.unity.localization >= 1.5.0` | Edit Mode | Read, AssetWrite; Medium | Normal write authorization | Locales, string tables, localized values and validation |
| XR Interaction Toolkit | `com.unity.xr.interaction.toolkit >= 3.0.0` | Edit Mode | Read, SceneWrite; Medium | Normal write authorization | XR context, interactors/interactables, creation/configuration and validation |
| Netcode for GameObjects | `com.unity.netcode.gameobjects >= 2.0.0` | Edit Mode | Read, SceneWrite; Medium | Normal write authorization | NetworkManager, NetworkObject/NetworkTransform, context and validation |
| Addressables | `com.unity.addressables >= 1.21.0` | Edit Mode | Read, AssetWrite, Destructive, Build; Critical | Entry moves/relabels and builds require confirmation; build has preview/job polling | Settings/groups/entries/validation/key audit/content build |
| URP | `com.unity.render-pipelines.universal >= 17.0.0` | Any | Read; Low | None | Active URP asset and renderer context |
| HDRP | `com.unity.render-pipelines.high-definition >= 17.0.0` | Any | Read; Low | None | Active HDRP asset and settings context |

`get_server_health.packageCompatibility` reports every known gate as `loaded`, `package_missing`, `version_unsupported`, `available_not_loaded`, or `loaded_incompatible` with installed and minimum versions.

## Asset Modeler — Smart Body Split

Select a character root, `SkinnedMeshRenderer`, or static mesh and open **GameObject → Asset Modeler → Smart Body Split...**. Choose **Core (6 parts)** for head, torso, left/right arms and left/right legs, or **Hands and Feet (10 parts)** to also extract both hands and both feet. Rigged meshes prioritize Humanoid/Generic bone weights; static meshes use a lower-confidence geometric fallback and require visual review.

The colored Scene View preview is read-only. **Create parts** writes new `.asset` meshes, a `BodyPartSet`, a split manifest, and a sibling hierarchy of renderers sharing the original skeleton. The source FBX/shared mesh is never mutated. Positions, normals, tangents, colors, UV0–UV7, submeshes/material slots, all bone influences, bind poses, and every blend-shape frame are copied and validated before the result is committed.

Agent flow:

1. `mesh_analyze_body_parts` returns the semantic proposal plus `sourceHash` and `previewHash`.
2. `mesh_preview_smart_split` opens the same proposal in the Scene View.
3. `mesh_apply_smart_split` requires both hashes and the normal high-risk confirmation policy.
4. `mesh_validate_split` reads the persisted fidelity manifest.

The detailed architecture, future topology/deformation phases, and acceptance gates live in `Documentation~/asset-modeler-complete-spec.md`.

## Rig & Animation Studio

Open **Window → Asset Modeler → Rig & Animation Studio** and select a character root, a `SkinnedMeshRenderer`, or one of its bones. The seven pages share the same services used by the 19 `rig_*` / advanced `animation_*` MCP tools.

- Inspector draws the skeleton and per-bone weight heatmap and samples clips without leaving the character in a modified pose.
- Skin Weights blocks imported meshes until **Create editable skin copy** is used; each complete stroke is one cancellable Undo, preserves `BoneWeight1`, supports locked bones/vertices/regions, and uses cached baking plus triangle-BVH/vertex-grid picking instead of scanning the full mesh on every pointer event.
- Skeleton & Bind captures/restores bind snapshots, validates rebuilt bind poses by baked geometry, safely prunes unused renderer slots, and transfers weights through topology or a closest-surface BVH with region/mirror controls.
- Clips & Poses captures transform/Humanoid poses and writes standalone `.anim` assets with trim, reverse, Generic/Humanoid mirror, loop, root-motion, quaternion-correct additive, sequential merge and curve-remap operations.
- Retarget creates complete-state-hashed Humanoid/Generic profiles, requires explicit review per bone, provides pose-error metrics and supports batch clip bake.
- IK & Bake provides drift-free native Two Bone, Aim and arbitrary-chain FABRIK solvers, placement presets and full-chain bake. When optional `com.unity.animation.rigging` is present, the same binding creates and validates the equivalent Two Bone, Multi-Aim or Chain IK constraint without a compile-time dependency.
- Auto Rig creates a reviewed 19-landmark proposal with per-landmark confidence gates, generates anatomically biased/diffused weights, attempts a validated Humanoid Avatar with honest Generic fallback, and produces a deformation test clip. Its opt-in AI bridge exports a versioned, hash-guarded request with mesh summary and multi-view references, then validates the returned proposal without silently sending assets or selecting a provider.

The implementation matrix, gates, limitations, and validation record are maintained in `Documentation~/rig-animation-studio-implementation-spec.md`.

## Safety model

- The broker binds only to `127.0.0.1`.
- Every MCP request requires a bearer token.
- `Origin`, `Accept`, `Content-Type`, protocol version, JSON-RPC envelope, body size, concurrency, deadlines, and notifications are validated centrally.
- Reads and mutations use a request coordinator; mutations, refresh, builds, tests, reimports and package jobs share an Editor-wide mutation gate. Cancellation control remains available while ordinary requests wait.
- `notifications/cancelled`, deadlines, jobs, bridge scripts, sidecar requests, and pending main-thread work support cooperative cancellation.
- Capability switches separately gate writes, reflection, destructive operations, packages, builds, project settings, Play control, and runtime commands.
- High-risk confirmation is disabled by default so authorized agents execute tools directly. Enable **Confirm high-risk tools** under **Window → Unity MCP Transport → Safety** to require a second call for destructive, reflection, Package Manager, project-settings, and build operations.
- Confirmation tokens are single-use and bound to exact normalized arguments and the current project.
- Reflection has explicit allow/deny policy for filesystem, process, modal menu, and other unsafe surfaces.
- Mutations use Undo-aware APIs, record Prefab instance overrides, and dirty the actual affected scene or asset.
- Stable targets are preferred; stale explicit identities fail without rebinding to a replacement at the same path. `instanceId` remains a session-local compatibility fallback. Authorization and target identity are checked again after waiting for mutation access.
- No normal mutation implicitly calls `SaveAssets` or saves a scene. Use `save_scene` / `save_all_scenes` explicitly.

## Editor activation and multiple projects

With **Autonomous editor activation** enabled, Unity temporarily opens and focuses the required internal Scene/Game view and keeps editor updates flowing. If an attention-dependent request still does not reach Unity's main thread within two seconds, the managed Windows broker makes a best-effort activation of the Unity process and restores the previous foreground window only when Unity still owns it. Set `UNITY_MCP_BROKER_FOCUS_ASSIST=0` for a manually launched broker to disable the operating-system fallback.

Several Unity Editors may connect to the broker at once, but exactly one project is active for MCP routing. Use `broker_list_projects` and `broker_activate_project` to select it explicitly. Every command is bound to the active Domain Reload generation; messages from replaced generations are rejected.

For runtime gameplay handlers, see [`Runtime/Bridge/README.md`](Runtime/Bridge/README.md).

## Troubleshooting

| Symptom | Check | Resolution |
| --- | --- | --- |
| 401 after reopening the Editor | `GET /health`, the per-user `UNITY_MCP_BROKER_TOKEN` | Update the package: Unity now adopts the per-user token. Reinstall the Claude Code entry from **AI clients** to get the `headersHelper`, then `/mcp` → Reconnect. |
| A call answered `status: running` | The `requestId` in the answer | Call `broker_wait_request` with it; do not repeat the call. |
| Write answered `RESOURCE_BUSY` | The `jobId` in the answer | A test, build, reimport or refresh job owns the Editor. Wait with `wait_tool_job`, then retry. |
| Editor frozen, calls answer `UNITY_OFFLINE` with `dialogs` | `broker_editor_status` | A native dialog (for example *Script Updating Consent*) blocks the main thread. Answer it in Unity, or ask a human and use `broker_press_dialog_button`. |
| Broker stopped answering | `logs/supervisor.log`, `logs/broker.watchdog.log` | The supervisor restarts it within about 25 s; the watchdog log holds the stacks of the stall. |
| Leftover `UnityShaderCompiler` / `AssetImportWorker` processes | `broker_cleanup_orphans` (lists) | Run it with `apply: true`, or `python Broker~/orphans.py --apply`. |
| Tool family is missing | `get_server_health.packageCompatibility` | Install a supported package version and wait for the domain reload/delayed bootstrap. |
| Package is installed but module is `available_not_loaded` | `self_test` and Unity Console | Wait for compilation/domain reload; resolve any assembly errors. |
| `JObject` accessibility/compiler errors | Newtonsoft package | Install `com.unity.nuget.newtonsoft-json` 3.x through UPM. |
| Request rejected | Server health, token, Origin, headers, protocol version | Match the configured token and use Streamable HTTP POST with JSON and a supported MCP protocol header. |
| Broker endpoint is offline | Managed broker state, `GET /health`, and **Logs** | Click **Start** after a successful managed install, or start `python Broker~/main.py` manually with a non-empty matching token. |
| Bridge keeps reconnecting | Broker URL/token and panel bridge error | Match `UNITY_MCP_BROKER_TOKEN`, keep the broker on loopback, and use `ws://127.0.0.1:8765/bridge`. |
| Mutation result is uncertain after reload | `broker_get_request` | Do not repeat a non-idempotent write automatically when the ledger reports `OUTCOME_UNKNOWN`; reconcile the Unity state first. |
| Confirmation required | **Window → Unity MCP Transport → Safety** and tool error metadata | Disable **Confirm high-risk tools** for direct execution, or repeat the exact call with the returned `_confirmationToken` before it expires. |
| Target no longer resolves | Refresh the read result | Prefer the returned `target.globalObjectId`; use `instanceId` only inside the current editor session. |
| Changes are not on disk | Dirty scenes/assets | This is intentional. Call explicit save tools after review. |
| Build/test/import job appears stuck | Health `jobs`, `activeEditorActivityLeases`, and broker request state | The broker waits automatically; inspect `broker_get_job`, or use `wait_tool_job`/`cancel_tool_job` after opting out with `_meta.autoWaitJob=false`. |
| Sidecar unavailable | `mesh_sidecar_health` | Start the configured localhost sidecar and verify its Python dependencies. |

## Development

Repository layout:

| Path | Contents |
| --- | --- |
| `Editor/`, `Runtime/` | The Unity package (C# 9, one assembly per optional module, gated by `versionDefines`). |
| `Tests/` | EditMode tests (`Maicon.SceneAnalyzer.Editor.Tests` and per-module test assemblies). |
| `Broker~/` | Python broker, supervisor, watchdog and orphan cleanup, with its pytest suite. Unity ignores folders ending in `~`. |
| `Sidecar~/` | Optional FastAPI mesh-segmentation sidecar. |
| `Tools~/` | Release validator, C# syntax check and the Unity 6.3 CI harness. |
| `Documentation~/` | Design notes, specs and contracts. |

Checks that run without Unity (the same as CI):

```bash
python -m pip install -r Broker~/requirements-dev.txt
python -m pytest Broker~/tests -q
python Tools~/validate_release.py
dotnet run --project Tools~/CSharpSyntaxCheck/CSharpSyntaxCheck.csproj --configuration Release -- .
```

Unity tests: open a project that references the package, then run the EditMode tests from **Window → General → Test Runner**. Tests of the Editor-wide mutation gate report *Ignored* when they run through `run_tests` over MCP (the test job owns the gate); run them from the Test Runner window or in batch mode. The Unity 6.3 validation fixture is described in [Tools~/Unity63Ci/README.md](Tools~/Unity63Ci/README.md).

### Release verification

Before publishing a version:

1. Bump `package.json`, `Broker~/broker_version.py`, `SceneAnalyzerVersion.Fallback` and `RELEASE_VERSION` in `Tools~/validate_release.py`, and add the `CHANGELOG.md` entry.
2. Import the package in Unity 6000.6 and 6000.3 (and 6000.1/6000.2 when version-gated code changed); run `self_test` and verify zero errors.
3. Run EditMode tests; run PlayMode/bridge tests when the project includes runtime fixtures.
4. Exercise Built-in, URP and HDRP projects, and each optional package both absent and present.
5. Verify high-risk calls execute directly by default; then enable **Confirm high-risk tools** and verify `CONFIRMATION_REQUIRED` tokens are exact-argument-bound and single-use.
6. Verify a mutation in an additive non-active scene and in a Prefab instance records the right dirty scene/override without saving.
7. Run the broker and sidecar pytest suites for the services you ship.
8. Verify the endpoint survives a real script compile/Domain Reload and an Editor restart, and that a durable refresh job reaches a terminal state.
9. Capture `get_server_health`, `broker_get_status` and `get_tool_inventory` output as release evidence.

## Documentation

- [Agent environment](Documentation~/agent-environment-implementation.md) and [bounded read contracts](Documentation~/agent-read-contracts.md)
- [Unity 6000.6 migration](Documentation~/unity-6000.6-migration.md) (`EntityId`, `SceneHandle`, opaque MCP handles)
- [Broker internals](Broker~/README.md) and the historical [0.16.0 broker release checklist](Documentation~/broker-release-0.16.0.md)
- [Runtime command bridge](Runtime/Bridge/README.md) and [mesh sidecar](Sidecar~/README.md)
- [Changelog](CHANGELOG.md)

## Contributing

Bug reports, ideas and pull requests are welcome. Start with [CONTRIBUTING.md](.github/CONTRIBUTING.md); everyone taking part agrees to the [Code of Conduct](.github/CODE_OF_CONDUCT.md). Please report security problems privately as described in [SECURITY.md](.github/SECURITY.md), not in public issues.

## License

[MIT](LICENSE.md). Third-party Python dependencies of the broker and the sidecar keep their own licenses.
