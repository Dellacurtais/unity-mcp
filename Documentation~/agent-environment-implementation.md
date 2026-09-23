# Agent environment implementation — Unity 6.3

Implementation following [the 2026-09-23 audit](agent-environment-audit-2026-09-23.md). These changes are in the source checkout, under **Unreleased**. Package version remains 0.18.6 until a coordinated release. The installed broker and game projects are not automatically updated by editing this checkout.

## Routing and concurrent agents

`POST /mcp/` initialization returns `Mcp-Session-Id`. Clients should retain this header on subsequent requests. `broker_activate_project` pins the project for that session; clients that omit the session header retain the legacy global selection behavior. A client can instead set `params._meta.projectKey` on an individual call. Pin the intended project before authoring and include `params._meta.expectedGenerationId` when a write must apply to an observed Editor generation.

Each active request gets a unique execution ID. Two clients can both use JSON-RPC ID `2` without sharing results or cancelling one another's operation. `notifications/cancelled` resolves through the client's session and the project captured when the request started. Clients using the legacy no-session mode do not receive this isolation.

The broker admits at most 1,024 sessions (24-hour idle TTL), 128 active operations and 32 pending calls per client. Saturation returns `BROKER_BUSY` before creating a new request receipt. Close unused sessions with `DELETE /mcp/`.

Concurrent calls sharing the same project and idempotency key join one operation. A write whose send may have reached Unity remains `OUTCOME_UNKNOWN` after a lost ACK or response. Do not automatically replay an inconclusive mutation; inspect `broker_get_request`, its job and the affected objects first.

## Exclusive Editor work and truthful recovery

All request processors share one Editor mutation gate. Build, test, asset-reimport, Addressables-build and package jobs hold it for their execution; refresh controls use the same gate. Read tools and priority cancellation controls remain available. This serializes MCP mutations, but does not prevent the human user, unrelated Editor extensions or Unity's own background activity from changing the project.

Authorization and target resolution run again after acquiring the gate. Explicit stable identities fail when stale instead of falling back to a same-name replacement. Native component targets retain their specific component identity. Changesets defer ScriptableObject file saves until commit and compensate written bytes if a later commit step fails; result metadata states what was and was not rolled back. Unity Undo is not a general filesystem transaction.

Known results survive cancellation that arrives after the operation returns. Cancellation before execution prevents the operation; cancellation during an irreversible or non-cooperative native operation cannot promise rollback. Refresh cancellation waits for an Editor acknowledgement and is rejected after native refresh starts.

The project journal `Library/UnityMcp/jobs.json` keeps active jobs and recent terminal receipts using an atomic replacement. Following a domain/process restart, interrupted work becomes failed with `OUTCOME_UNKNOWN`, not completed or automatically retried. It cannot resume Unity native APIs or recover deleted `Library` contents. Journal errors appear in `get_server_health.jobs.persistenceError` and degrade health.

Refresh/compile broker jobs complete after reload only when a successful compilation event exists for the preceding generation and a fresh bridge reports ready. An ordinary reconnect is not proof that tests or builds completed. A refresh without compilation requires a stable idle interval across Editor updates; returning from `AssetDatabase.Refresh` or its first `delayCall` is insufficient. This is an observed quiet interval, not a guarantee against later external imports. Heartbeat and main-thread timestamps distinguish a connected socket from an Editor capable of processing work.

## Smaller discovery and reads

Broker `tools/list` defaults to 100 descriptors per page (maximum 250). Follow `nextCursor` until absent. Registry hashes and descriptor indexes are cached; they are not rebuilt for every call. Cursors bind project, registry version and filters. `params._meta.profile` accepts `all`, `core`, `read`, `authoring`, `testing`, `modeling` and `admin`; `pageSize` and `module` refine discovery.

Profiles are discovery filters, not permissions. Broker controls remain discoverable in every profile. Existing capability switches and confirmation policy remain authoritative. Clients should refresh discovery after a registry change; the broker honestly advertises `listChanged=false` until notification support is implemented.

`read_hierarchy` defaults to 250 nodes, 256 KiB and depth 64, with optional root/scene/field selection and generation-bound cursors. Snapshot invalidation does not scan the scene. `get_tool_inventory` materializes schemas only for the requested page. `get_server_health({"compact":true})` omits long listings while retaining checks and counts. See [read contracts](agent-read-contracts.md) for exact input/output behavior.

## Ledger storage and retention

Responses exceeding 256 KiB are stored in a sibling `<database>.responses` directory as SHA-256-addressed gzip JSON; the SQLite ledger retains a small reference. Replay verifies the artifact and transparently returns the original MCP response. Missing or corrupt artifacts preserve the terminal receipt and report unavailable evidence; they never trigger replay of a mutation.

Automatic maintenance removes only completed read-response payloads by age or byte budget. Request rows, idempotency keys, mutation responses, pending work and inconclusive outcomes remain. Expired read payloads report `RESPONSE_EXPIRED`. Mutation history may therefore grow beyond the read budget. Old database free pages are not reclaimed by this policy; no `VACUUM` or purge of the user's existing ledger was performed.

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `UNITY_MCP_BROKER_RESPONSE_INLINE_BYTES` | 262144 | Inline-response threshold |
| `UNITY_MCP_BROKER_HISTORY_DAYS` | 7 | Completed-read payload age limit; 0 disables age expiry |
| `UNITY_MCP_BROKER_HISTORY_BYTES` | 268435456 | Retained completed-read payload budget; 0 disables byte-budget expiry |
| `UNITY_MCP_BROKER_HISTORY_INTERVAL` | 300 | Maintenance interval in seconds |

Set both history limits to zero to disable pruning. Schema version 3 identifies the new response encoding and blocks older brokers from interpreting artifact references as responses. Back up the database and its artifact directory together before upgrading; rollback requires restoring compatible data as well as the broker source.

## Validation and rollout

Validation uses disposable projects under `.validation/unity63-ci/`, the installed **Unity 6000.3.5f2**, and a private loopback broker/database. No validation operation targets a game project. The [CI harness](../Tools~/Unity63Ci/README.md) includes import/registry checks, EditMode tests, Play Mode and script recompilation/reload. Its automated runner must have that Editor installed and a valid license.

These checks establish protocol, code and controlled Editor behavior; they do not establish gameplay correctness or production process activation.

Local evidence, 2026-09-23:

- Broker: 64 tests passed on Python 3.14, including HTTP/WebSocket process integration, concurrent idempotency, client isolation, missing ACK, stalled sends, reconnection evidence, schema migration and retention.
- Focused Unity safety/authoring/contracts/jobs/hierarchy: 102 passed, 2 optional skips, 0 failures. Subsequent snapshot-provenance run: 34 passed, 0 failures. These overlap; they are not 136 distinct tests.
- Original core/optional-tool EditMode subset (`Maicon.SceneAnalyzer.Editor.Tests`) on the minimal project: **242 passed, 0 failed, 2 skipped** (244 total). The same subset on the representative project: **243 passed, 0 failed, 1 skipped** (244 total). The differing skips reflect optional package availability. These were not full-package runs: Modeling, AI3D, NavMesh, ShaderGraph and PostFx use sibling test namespaces and were excluded. The CI filter now uses the shared `Maicon.SceneAnalyzer` root; the expanded run is recorded separately without overwriting these XML files.
- Expanded representative EditMode run (`Maicon.SceneAnalyzer`), on the unchanged 999-file package snapshot: **350 passed, 1 failed, 4 skipped** (355 total; Unity exit 2). This run **failed**. Modeling contributed 67 passes and 3 skips for the absent optional Animation Rigging package; AI3D 26 passes, NavMesh 6 passes, PostFx 6 passes, ShaderGraph 2 passes and 1 failure, and the original core subset 243 passes and 1 Addressables skip. The failure is `ShaderGraphToolTests.Write_RejectsMarkerOnlySourceBeforeMutationAndRollsBackWhenUnityImportFails`, assertion at `Tests/Editor/ShaderGraph/ShaderGraphToolTests.cs:121` (expected true, received false). No modeling or ShaderGraph implementation was changed to make this evidence pass. The minimal profile has not been rerun with the expanded filter.

The 67 Modeling passes cover the existing tests only. They do not certify proposed loose-geometry cleanup or real mesh-size normalization features, nor prove preservation guarantees absent from those tests.

The subsequent Asset Modeler implementation supersedes that failed full-package result without replacing its evidence: representative snapshot `representative-m4` passed **410 tests, 0 failures, 4 optional skips** (414 total). It includes cleanup, rig-preserving model copies, scale normalization, UI and MCP regressions, including reuse of complete nested target references through the actual processor. The ShaderGraph test now separately verifies a valid roundtrip and rollback after a genuinely invalid semantic graph, instead of expecting a valid graph to fail import. The broker suite also passed **65/65** after adding model-scale tools to the modeling profile. See [Asset Modeler preparation](asset-modeler-preparation.md) for scope, limits and the final evidence paths; these runs do not certify production activation.

- Play/reload smoke: **6/6 passed in each profile**, including all four combinations of Domain Reload and Scene Reload, real advancing frames, a hierarchy read in Play Mode, and script compilation/reload. Both final import gates passed with zero Console errors.
- Release and modeling static gates passed. Roslyn syntax check: 424 C# files, 0 syntax errors; actual Unity compilation is covered separately.
- Real HTTP client -> private broker -> Unity 6000.3.5f2: **28 checks passed** with the final shared idle tracker, including exact fixture identity, two sessions, 20 simultaneous same-key create calls yielding one actual hierarchy object, stale-generation rejection, module/profile pagination, no-op refresh, direct `wait_for_compile_clean`, and successful read after compile/reload. The object was deleted through MCP, the disposable host exited cleanly, and the private broker was stopped.
- This integration test exposed and reproduced a premature `refresh_completed(compiled:false)` receipt while compilation was queued. After the fix, the receipt instead contains `compilation_finished` plus reconnection evidence, and the broker reports a new generation. The original failed report is preserved alongside the passing report.

Measurements from the minimal project's actual HTTP responses (281 Unity tools + 8 broker tools):

| Response | Full form | Reduced form | Scope |
| --- | ---: | ---: | --- |
| Tool discovery | 622,951 bytes across 6 pages | 53,691 bytes, 30 core tools | 91.4% fewer bytes when the client requests core only |
| Health | 22,866 bytes | 4,444 bytes | 80.6% fewer bytes with `compact=true` in the final run; full size varies with recent jobs |
| Hierarchy | Not compared to a different scene | 1,724 bytes | One-node page with minimal fields, including MCP envelope |

Catalog pagination alone does not reduce the total when a client retrieves every page. The largest 50-tool page was 118,299 bytes; the proposed 64 KiB common-page goal is not a guaranteed catalog byte cap. Cached descriptor lookup measured a median 0.0001 ms in 1,000 local lookups; this excludes serialization, network and Unity execution and must not be presented as end-to-end latency.

The first representative fixture exposed a test-infrastructure defect: placing a Unity project inside its own local package's resolved directory caused URP/ProBuilder MonoScript bindings to be missing despite compiled types existing. Its 23 failed tests and import exceptions remain in the original logs. The harness now snapshots package sources and `.meta` files into a sibling directory, records SHA-256 hashes, and checks Console errors as well as registry self-test results. With the same optional packages installed, URP's script binding resolved correctly and the import reported zero Console errors. No URP package removal or log suppression was used.

Retained evidence is under `.validation/`: final `live-agent-smoke-final/live-agent-smoke-report.json`, original `live-agent-smoke-before-refresh-fix.json`, `catalog-lookup-benchmark.json`, `live-unity-final.log`, minimal `editmode-final-fixed.xml` / `play-reload-final.xml`, and representative-isolated `editmode.xml` / `play-reload-smoke.xml` under `unity63-ci/*/artifacts`. The expanded representative run is preserved separately as `editmode-all-namespaces.xml` and `editmode-all-namespaces.log`; its package source hashes match `package-source-sha256.json` both before and after execution. Disposable generated files are intentionally not committed. The repeatable HTTP test is maintained in `Tools~/Unity63Ci/run_live_agent_smoke.py`.

For a managed-broker rollout, first choose a coordinated package/broker release version and back up the current ledger with its artifacts. The installed broker is a separate copied runtime; editing this checkout does not replace it. Its current version check alone cannot distinguish this Unreleased source from an already installed 0.18.6 runtime. Install/repair the coordinated runtime and verify schema/version, project identity and a controlled smoke before treating the live environment as upgraded. No production runtime installation, schema migration, commit or publication was performed in this task.

## Remaining boundaries

This implementation establishes bounded discovery, serialized mutations and conservative recovery. It does not create an operating-system sandbox around reflection or arbitrary Editor extensions, authenticate separate human identities per MCP session, or provide conflict-free parallel writes to different scenes. The single shared mutation gate intentionally trades write parallelism for simpler guarantees.

Large response artifacts currently optimize ledger storage while preserving the existing MCP wire response. An authenticated on-demand media/resource API, strict semantic schemas for every remaining legacy tool, incremental scene diffs, and certification of other 6.3 patches require separate work. There is no claim that all targets or response-latency goals in the audit are achieved.
