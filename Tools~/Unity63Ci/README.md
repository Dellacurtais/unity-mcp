# Unity 6.3 validation fixture

The `Unity 6.3 Editor validation` workflow runs real Unity **6000.3.5f2** jobs
against fresh disposable projects. It is separate from C# syntax checking.
Both jobs remain visibly skipped until infrastructure is configured; a skipped
job is not evidence of Unity compatibility.

Configure a dedicated licensed Windows runner with labels `self-hosted`,
`Windows`, `unity-6000.3.5f2`, PowerShell 7, and registry/network access or a warm
UPM cache. Set repository variable `ENABLE_UNITY_63_CI=true` and
`UNITY63_EDITOR_PATH` to the installed `Editor/Unity.exe`. Use an isolated runner
account with default MCP settings; do not reuse an account that auto-connects
production Unity projects. This workflow neither acquires nor activates a Unity
license. License eligibility and runner provisioning belong to the operator.
Do not enable the persistent runner for arbitrary fork pull requests; the
workflow deliberately accepts trusted branch pushes and manual dispatch only.

Profiles:

- `minimal`: this local package plus Test Framework 1.6.0. Runtime dependencies
  must come from `package.json`; missing declared dependencies fail compilation.
- `representative`: additionally URP 17.3.0, Input System 1.17.0, AI Navigation
  2.0.9, Timeline 1.8.10, uGUI 2.0.0, ProBuilder 6.0.9 and common built-in modules.
  These versions match the audited 6.3 project; this is not a matrix of every
  optional package or every 6.3 patch.

```powershell
./Tools~/run_unity63_ci.ps1 -Profile minimal -PrepareOnly
# Or prepare and actually launch Unity in a clean checkout:
./Tools~/run_unity63_ci.ps1 -Profile representative -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.3.5f2/Editor/Unity.exe'
```

`-PrepareOnly` creates the fixture without launching Unity. The script refuses
to overwrite an existing fixture; preserve logs and pass a new `-RunName` for a
fresh run. The package source is copied into `package-under-test`, a sibling of
`project`, with a SHA-256 manifest. This isolates the tested revision and avoids
overlapping the UPM package root with the project's own package cache. It never
migrates an existing game project or deletes its contents.

Keep run names short on Windows (for example `-RunName m2`). Long fixture paths
can reach the legacy 260-character limit inside transitive package imports even
when PowerShell can read those files.

Each profile runs import/compile with version, Console-error and registry gates,
package EditMode tests filtered by the shared `Maicon.SceneAnalyzer` namespace
(Core, Modeling, AI3D, NavMesh, ShaderGraph and PostFx), and separate Editor coroutine smoke tests that actually
enter Play Mode with all four combinations of domain and scene reload enabled
and disabled, verify the configured reload flags and that the Editor domain
respectively changes or remains the same, advance frames, read
hierarchy, exit Play Mode, and trigger a script compile/domain reload before
checking the registry again. The tests restore the fixture's Play Mode settings.
This smoke
does not certify broker recovery, rendering quality or game behavior.

Artifacts include logs, NUnit XML, import self-test output, manifest and resolved
package lock, plus the hashes of package sources actually tested. Nonzero Unity exit codes, missing test XML, zero passed tests,
failed/inconclusive runs and stage timeouts fail the script. Skipped individual
tests are counted in its output and remain visible in XML.

The original 244-case runs used `Maicon.SceneAnalyzer.Editor.Tests` and covered
only the core/optional-tool subset under that namespace. They did not execute
the Modeling or other sibling namespaces and must not be described as the full
package suite. The expanded filter retains explicit/optional skips in the
results; available tests still depend on the profile's installed packages.

Do not point `file:` at an ancestor of the disposable project. On Unity
6000.3.5f2 this layout reproduced URP/ProBuilder MonoScript bindings returning
null despite successful C# compilation, and URP assets with missing scripts.
The sibling package snapshot restored the binding with zero import Console
errors. The import report retains both the registry result and Console errors;
a successful registry self-test alone does not make the import gate pass.

The smoke uses Unity Test Framework's documented
[RecompileScripts yield instruction](https://docs.unity.cn/Packages/com.unity.test-framework@1.6/api/UnityEngine.TestTools.RecompileScripts.html).
`AgentEnvironmentCi.cs` is copied into the disposable fixture only and is not
part of the shipped package's Editor assemblies.

For an isolated HTTP -> broker -> Editor smoke, the same helper also exposes
`Unity63Ci.LiveHost.Start`. Supply a separately launched loopback broker through
`UNITY_MCP_CI_BROKER_URL` (WebSocket `/bridge` URL) and
`UNITY_MCP_CI_BROKER_TOKEN`, then launch the fixture in batch mode with
`-executeMethod Unity63Ci.LiveHost.Start` and **without** `-quit`. Optional
`UNITY_MCP_CI_READY_FILE` receives a JSON ready marker with project/generation
identity; creating `UNITY_MCP_CI_STOP_FILE` stops that Editor cleanly. The host
owns its own processor/bridge/lifecycle monitor, survives domain reload through
per-Editor `SessionState`, and exits after 30 minutes. It never changes global
MCP `Settings`/`EditorPrefs`, and refuses to run outside these disposable fixture
paths. This helper enables a manual/integration smoke; the workflow does not
automatically launch a broker or claim broker recovery coverage.

With that disposable host and private broker running, exercise the real HTTP
path using Python 3 and the same `UNITY_MCP_CI_BROKER_TOKEN` environment variable:

```powershell
python ./Tools~/Unity63Ci/run_live_agent_smoke.py `
  --broker-url http://127.0.0.1:18766 `
  --ready-file ./.validation/live-ready.json `
  --project-path ./.validation/unity63-ci/minimal/project `
  --output-dir ./.validation/live-agent-smoke
```

The smoke opens two MCP sessions, checks discovery pagination/profiles/modules,
compares compact health payloads, sends 20 concurrent writes with one idempotency
key and verifies one actual object, rejects a stale generation, then checks both
a no-op refresh and a real script edit/compilation/reconnect. It removes its
temporary GameObject and leaves `Assets/Ci/AgentSmokeReload.cs` inside the
disposable fixture for repeat runs. The script rejects non-loopback brokers and
project paths outside the disposable fixture directory. It writes a JSON report
and catalog to `--output-dir`; it does not start or stop the host or broker.

`run_live_modeler_smoke.py` accepts the same broker/ready/project/output arguments
and token environment variable. It checks live discovery of the five preparation
tools, creates a disposable cube and editable session, exercises paginated
diagnosis and cleanup preview/apply, verifies stale revisions/hashes are rejected,
normalizes a copy to height 1.8 and independently re-reads its dimensions and
root scale. It removes the scene objects and retains generated assets only in
the disposable fixture. Required single-use confirmation tokens are consumed
only for these authored fixture operations after verifying project identity.

No-op refresh completion uses a cooperative 0.5-second idle observation across
Editor updates, resetting while compilation/import is active or the AssetDatabase
artifact dependency version changes. This is an observed settlement window, not
a guarantee against future work queued by other Editor extensions. Real compile
success additionally requires the durable broker job to observe compilation and
reconnection under a new generation; a first `delayCall` is not sufficient.
