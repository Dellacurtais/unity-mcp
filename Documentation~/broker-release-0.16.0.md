# External broker 0.16.0 release validation

This checklist covers the validation that cannot be performed by the static CI workflow because it requires an installed Unity Editor. The CI workflow itself never launches Unity.

## Release invariants

- The UPM package identity remains `com.maicon.scene-analyzer`.
- The package, C# fallback, and Python broker versions are all `0.16.0`.
- Existing asmdef names and C# namespaces remain unchanged for source and binary compatibility.
- **Embedded** is still the default transport and works without Python.
- Broker mode is opt-in. The broker is a separate operating-system process; Unity only installs, launches, supervises, and connects its outbound bridge.
- Domain Reload, script compilation, and closing the Editor disconnect the bridge but do not terminate a managed broker.
- Unity APIs continue to execute through the Editor-side request processor and main-thread dispatcher.

## Automated gate (no Unity)

Run from the package root:

```text
python -m pip install --requirement Broker~/requirements-dev.txt
python -m pytest Broker~/tests -q
python -m compileall -q Broker~
python Tools~/validate_release.py
dotnet run --project Tools~/CSharpSyntaxCheck/CSharpSyntaxCheck.csproj --configuration Release -- .
git diff --check
```

Expected results:

- The full broker test suite passes, including `test_process_integration.py`, which starts a real Uvicorn subprocess and communicates through real loopback HTTP and WebSocket sockets.
- Python compilation emits no errors.
- The release contract reports `com.maicon.scene-analyzer@0.16.0`, Embedded default, and pinned dependencies.
- Roslyn reports zero syntax errors for every package C# source.
- `git diff --check` exits with code 0.

The repository workflow `.github/workflows/release-static.yml` runs the Python gate on Windows and Linux with Python 3.10 and 3.12, plus the Roslyn syntax gate on .NET 8. It does not install or launch Unity.

## Upgrade from 0.15.x

1. Record the current UPM source and confirm the project references `com.maicon.scene-analyzer`; do not replace it with a new package ID.
2. Update the Git revision or package source to `0.16.0` and let Unity finish its normal package import and script compilation.
3. Open **Window → Unity MCP Transport**. Verify **Mode = Embedded** before changing any transport setting.
4. Start Embedded and call `get_server_health`, `self_test`, and `get_tool_inventory` with the existing endpoint/token. This confirms the compatibility fallback before enabling the broker.
5. Install Python 3.10 or newer, stop Embedded, select **Mode = Broker**, and click **Install**.
6. Verify that installation completes outside the package under the per-user broker root:
   - Windows: `%LOCALAPPDATA%\UnityMcp\Broker`
   - `current.json` points to a versioned installation.
   - `install-status.json` reports `completed`.
   - No files inside the UPM package were modified by installation.
7. Click **Start** and verify `GET http://127.0.0.1:8765/health` reports:
   - `service = unity-mcp-broker`
   - `version = 0.16.0`
   - `schemaVersion = 2`
8. Connect the Editor bridge and repeat `get_server_health`, `self_test`, and `get_tool_inventory` through the broker endpoint.

A failed Install or Repair must leave the previous `current.json` unchanged. Completed installs retain the current installation, an installation referenced by the running process state, and the newest versioned installations up to the retention limit.

## Manual release checklist

### UPM and Embedded compatibility

- [ ] Upgrade a project that already references `com.maicon.scene-analyzer`; verify there is no remove/add migration and no missing asmdef reference.
- [ ] Open a clean project and confirm the package display name is **Unity MCP Transport** while the manifest dependency key remains `com.maicon.scene-analyzer`.
- [ ] With no Python configured, confirm Embedded starts, authenticates, lists tools, executes a read tool, and stops normally.
- [ ] Occupy the Embedded port, attempt Start, release the port, and start again; confirm the failed bind did not leave the listener or request processor stuck.

### Managed installation and supervision

- [ ] Install from a path containing spaces and non-ASCII characters.
- [ ] Confirm Python discovery rejects versions below 3.10 and accepts a configured Python 3.10+ executable.
- [ ] Confirm Install creates a private virtual environment and uses the exactly pinned production requirements.
- [ ] Force dependency installation to fail and confirm status/logs contain the failure, the partial installation is removed, and the previous current installation remains usable.
- [ ] Run Repair and confirm a new versioned installation is activated atomically.
- [ ] Confirm install and broker logs rotate and old versioned installations are pruned without deleting the current or running installation.
- [ ] Start from two Unity Editors at the same time; confirm the process mutex prevents duplicate managed brokers.
- [ ] Replace the stored PID with a live unrelated PID or mismatched start time; confirm Stop refuses to terminate the unverified process.

### Real transport and persistence

- [ ] Confirm `/mcp/` rejects a missing/wrong bearer token, a remote Origin, unsupported media/protocol headers, invalid `Content-Length`, and oversized bodies.
- [ ] Confirm `/bridge` rejects a wrong token and non-loopback Origin.
- [ ] Connect one Editor and verify its tool registry appears in `tools/list`.
- [ ] Execute a read tool end to end and confirm the command travels broker → WebSocket bridge → Unity → broker, with a completed durable request record.
- [ ] Trigger script compilation/Domain Reload while a read-only request is accepted. Confirm the broker PID and `/health` endpoint remain available, the new bridge generation reconnects, and the safe request resumes or returns a retry-safe reconnect error.
- [ ] Trigger Domain Reload after a non-idempotent mutation is accepted but before its result is proven. Confirm the ledger returns `OUTCOME_UNKNOWN` and the broker never blindly repeats the mutation.
- [ ] Start `broker_start_refresh_compile`; confirm the job survives bridge disconnect/reconnect and can be observed with `broker_get_job` or `broker_wait_job`.
- [ ] Restart only the broker process and confirm SQLite restores the active project, registry snapshot, completed request, and reconciled in-flight states.
- [ ] Close and reopen Unity without clicking **Stop**. Confirm the existing managed broker process is discovered and its PID is unchanged.

### Multiple projects

- [ ] Connect two Unity projects and confirm `broker_list_projects` lists both.
- [ ] Activate each project with `broker_activate_project` and verify subsequent Unity tools route only to that project.
- [ ] Reload one Editor and confirm messages from its replaced generation are rejected while the other connection remains valid.

## Rollback

### Immediate transport rollback

1. In **Window → Unity MCP Transport**, click **Stop** only if this Unity instance owns the managed process.
2. Disconnect Broker mode.
3. Select **Mode = Embedded** and start Embedded.
4. Point the MCP client at the same loopback HTTP endpoint and token.

This rollback does not require deleting the SQLite database or managed installations.

### Package rollback

1. Stop the managed broker and switch to Embedded before changing package revisions.
2. Back up `%LOCALAPPDATA%\UnityMcp\Broker\data\broker.sqlite3` and its `-wal`/`-shm` companions while the broker is stopped.
3. Change the Git revision/package source to the previous release. Keep the manifest key `com.maicon.scene-analyzer` unchanged.
4. If the previous package has broker support, use its **Install** or **Repair** action to create an installation matching that package version. Do not hand-edit `current.json` while a broker is running.
5. If the previous package has no external broker support, remain on Embedded. The 0.16.0 broker data and versioned installations may be retained for a later forward upgrade.

A broker that encounters a SQLite schema newer than it supports fails closed instead of modifying the database. Restore the backup or upgrade the package; do not downgrade the schema in place.

## Release evidence

Record the following with the release candidate:

- Commit/tag and `package.json` identity/version.
- CI run URL for `.github/workflows/release-static.yml`.
- Full pytest count and operating systems/Python versions.
- Offline C# syntax result and, on a release workstation, semantic compilation result using Unity's existing Bee response files and bundled Roslyn compiler without launching the Editor.
- Completed manual checklist, Unity versions tested, broker PID before/after Domain Reload, and any retained logs needed to diagnose failures.
