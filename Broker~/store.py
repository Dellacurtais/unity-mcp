from __future__ import annotations

import json
import sqlite3
import threading
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Any
from uuid import uuid4
from responses import ResponseVault


# Version 3 stores optional response-vault references in response_json. Older
# brokers must reject this database instead of returning markers as MCP replies.
CURRENT_SCHEMA_VERSION = 3


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


class BrokerStore:
    def __init__(self, path: Path, *, response_inline_bytes: int = 256 * 1024) -> None:
        path = path.expanduser().resolve()
        path.parent.mkdir(parents=True, exist_ok=True)
        self._path = path
        self._vault = ResponseVault(path.with_name(path.name + ".responses"), response_inline_bytes)
        self._connection = sqlite3.connect(str(path), check_same_thread=False)
        self._connection.row_factory = sqlite3.Row
        self._lock = threading.RLock()
        try:
            with self._lock:
                # Takes effect only on a new database; older ones are converted by compact_storage().
                self._connection.execute("PRAGMA auto_vacuum=INCREMENTAL")
                self._connection.execute("PRAGMA busy_timeout=5000")
                self._connection.execute("PRAGMA journal_mode=WAL")
                self._connection.execute("PRAGMA synchronous=NORMAL")
                self._connection.execute("PRAGMA foreign_keys=ON")
                self._migrate()
        except Exception:
            self._connection.close()
            raise

    def close(self) -> None:
        with self._lock:
            self._connection.close()

    def compact_storage(self) -> dict[str, int]:
        """Return free database pages to the filesystem.

        A database created before incremental auto-vacuum is rebuilt once with VACUUM, which
        copies only live rows. Afterwards free pages are released without a rebuild.
        """
        with self._lock:
            before = self._database_bytes()
            rebuilt = self._connection.execute("PRAGMA auto_vacuum").fetchone()[0] != 2
            if rebuilt:
                self._connection.commit()
                self._connection.execute("PRAGMA auto_vacuum=INCREMENTAL")
                self._connection.execute("VACUUM")
            else:
                self._release_free_pages(None)
            self._connection.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchall()
            return {"bytesBefore": before, "bytesAfter": self._database_bytes(), "rebuilt": int(rebuilt)}

    def _database_bytes(self) -> int:
        page_size = self._connection.execute("PRAGMA page_size").fetchone()[0]
        return int(self._connection.execute("PRAGMA page_count").fetchone()[0]) * int(page_size)

    def _release_free_pages(self, limit: int | None) -> int:
        if self._connection.execute("PRAGMA auto_vacuum").fetchone()[0] != 2:
            return 0
        before = self._connection.execute("PRAGMA freelist_count").fetchone()[0]
        if not before:
            return 0
        statement = "PRAGMA incremental_vacuum" if limit is None else f"PRAGMA incremental_vacuum({int(limit)})"
        self._connection.execute(statement).fetchall()
        return int(before - self._connection.execute("PRAGMA freelist_count").fetchone()[0])

    def schema_version(self) -> int:
        with self._lock:
            row = self._connection.execute(
                "SELECT value FROM metadata WHERE key='schema_version'"
            ).fetchone()
            return int(row["value"]) if row else 0

    def _migrate(self) -> None:
        try:
            self._connection.execute("BEGIN IMMEDIATE")
            self._connection.execute(
                "CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)"
            )
            version_row = self._connection.execute(
                "SELECT value FROM metadata WHERE key='schema_version'"
            ).fetchone()
            if version_row:
                try:
                    source_version = int(version_row["value"])
                except (TypeError, ValueError) as exception:
                    raise RuntimeError("Broker database schema version is invalid") from exception
            else:
                legacy_tables = self._connection.execute(
                    "SELECT name FROM sqlite_master WHERE type='table' "
                    "AND name NOT IN ('metadata', 'sqlite_sequence')"
                ).fetchall()
                source_version = 1 if legacy_tables else 0
            if source_version < 0 or source_version > CURRENT_SCHEMA_VERSION:
                raise RuntimeError(
                    f"Broker database schema {source_version} is newer than supported schema "
                    f"{CURRENT_SCHEMA_VERSION}; upgrade the package or restore a compatible database."
                )

            statements = (
                """CREATE TABLE IF NOT EXISTS projects (
                    project_key TEXT PRIMARY KEY, project_name TEXT NOT NULL,
                    project_path TEXT NOT NULL, process_id INTEGER NOT NULL,
                    process_start_utc TEXT NOT NULL, generation_id TEXT NOT NULL,
                    package_version TEXT NOT NULL, registry_count INTEGER NOT NULL,
                    registry_hash TEXT NOT NULL, state TEXT NOT NULL,
                    connected INTEGER NOT NULL, last_seen_utc TEXT NOT NULL
                )""",
                """CREATE TABLE IF NOT EXISTS registry_snapshots (
                    project_key TEXT NOT NULL, registry_hash TEXT NOT NULL,
                    tools_json TEXT NOT NULL, created_utc TEXT NOT NULL,
                    PRIMARY KEY(project_key, registry_hash)
                )""",
                """CREATE TABLE IF NOT EXISTS requests (
                    request_id TEXT PRIMARY KEY, idempotency_key TEXT,
                    project_key TEXT, generation_id TEXT, tool_name TEXT NOT NULL,
                    args_hash TEXT NOT NULL, read_only INTEGER NOT NULL,
                    idempotent INTEGER NOT NULL, state TEXT NOT NULL,
                    response_json TEXT, error TEXT, created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                )""",
                """CREATE TABLE IF NOT EXISTS jobs (
                    job_id TEXT PRIMARY KEY, kind TEXT NOT NULL, project_key TEXT,
                    state TEXT NOT NULL, progress REAL NOT NULL, message TEXT NOT NULL,
                    result_json TEXT, error TEXT, created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                )""",
                """CREATE TABLE IF NOT EXISTS events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT, project_key TEXT,
                    generation_id TEXT, event_type TEXT NOT NULL,
                    payload_json TEXT NOT NULL, created_utc TEXT NOT NULL
                )""",
            )
            for statement in statements:
                self._connection.execute(statement)

            registry_info = self._connection.execute(
                "PRAGMA table_info(registry_snapshots)"
            ).fetchall()
            registry_primary_key = [
                row["name"]
                for row in sorted(registry_info, key=lambda item: item["pk"])
                if row["pk"]
            ]
            if registry_primary_key != ["project_key", "registry_hash"]:
                self._connection.execute("DROP TABLE IF EXISTS registry_snapshots_v2")
                self._connection.execute(
                    """CREATE TABLE registry_snapshots_v2 (
                        project_key TEXT NOT NULL, registry_hash TEXT NOT NULL,
                        tools_json TEXT NOT NULL, created_utc TEXT NOT NULL,
                        PRIMARY KEY(project_key, registry_hash)
                    )"""
                )
                self._connection.execute(
                    """INSERT OR REPLACE INTO registry_snapshots_v2(
                        project_key, registry_hash, tools_json, created_utc
                    ) SELECT project_key, registry_hash, tools_json, created_utc
                      FROM registry_snapshots"""
                )
                self._connection.execute("DROP TABLE registry_snapshots")
                self._connection.execute(
                    "ALTER TABLE registry_snapshots_v2 RENAME TO registry_snapshots"
                )

            self._connection.execute("DROP INDEX IF EXISTS requests_idempotency")
            self._connection.execute(
                """CREATE UNIQUE INDEX IF NOT EXISTS requests_project_idempotency
                   ON requests(project_key, idempotency_key)
                   WHERE idempotency_key IS NOT NULL"""
            )
            self._connection.execute("CREATE INDEX IF NOT EXISTS requests_retention ON requests(read_only, state, updated_utc)")
            self._connection.execute("CREATE INDEX IF NOT EXISTS events_retention ON events(created_utc)")
            self._connection.execute(
                "INSERT INTO metadata(key, value) VALUES('schema_version', ?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (str(CURRENT_SCHEMA_VERSION),),
            )
            self._connection.commit()
        except Exception:
            self._connection.rollback()
            raise

    def reconcile_startup(self) -> None:
        with self._lock:
            now = utc_now()
            self._connection.execute(
                "UPDATE projects SET state='offline', connected=0, last_seen_utc=?",
                (now,),
            )
            self._connection.execute(
                "UPDATE requests SET state='waiting_for_reconnect', updated_utc=? "
                "WHERE state IN ('accepted_by_broker', 'queued_for_unity', 'dispatched')",
                (now,),
            )
            self._connection.execute(
                "UPDATE requests SET state=CASE WHEN read_only=1 OR idempotent=1 "
                "THEN 'waiting_for_reconnect' ELSE 'outcome_unknown' END, "
                "error=CASE WHEN read_only=1 OR idempotent=1 THEN error "
                "ELSE COALESCE(error, 'Broker restarted after sending the request to Unity; outcome is unknown') END, "
                "updated_utc=? WHERE state IN ('sending_to_unity', 'accepted_by_unity', 'running_in_unity')",
                (now,),
            )
            self._connection.execute(
                "UPDATE jobs SET state='waiting_for_reconnect', "
                "message='Broker restarted; waiting for Unity bridge', updated_utc=? "
                "WHERE state IN ('queued', 'dispatched', 'running', 'cancel_requested')",
                (now,),
            )
            self._connection.commit()

    def load_registry_snapshots(self) -> list[dict[str, Any]]:
        with self._lock:
            rows = self._connection.execute(
                "SELECT project_key, registry_hash, tools_json, created_utc FROM registry_snapshots AS r "
                "WHERE r.rowid=(SELECT latest.rowid FROM registry_snapshots AS latest "
                "WHERE latest.project_key=r.project_key ORDER BY latest.created_utc DESC, latest.rowid DESC LIMIT 1)"
            ).fetchall()
            latest: dict[str, dict[str, Any]] = {}
            for row in rows:
                record = dict(row)
                record["tools"] = json.loads(record.pop("tools_json"))
                latest[str(record["project_key"])] = record
            return list(latest.values())

    def set_active_project(self, project_key: str | None) -> None:
        with self._lock:
            if project_key is None:
                self._connection.execute("DELETE FROM metadata WHERE key='active_project'")
            else:
                self._connection.execute(
                    "INSERT INTO metadata(key, value) VALUES('active_project', ?) "
                    "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    (project_key,),
                )
            self._connection.commit()

    def get_active_project(self) -> str | None:
        with self._lock:
            row = self._connection.execute(
                "SELECT value FROM metadata WHERE key='active_project'"
            ).fetchone()
            return str(row["value"]) if row else None

    def upsert_project(self, identity: Any, state: str, connected: bool) -> None:
        with self._lock:
            self._connection.execute(
                """
                INSERT INTO projects(
                    project_key, project_name, project_path, process_id,
                    process_start_utc, generation_id, package_version,
                    registry_count, registry_hash, state, connected, last_seen_utc
                ) VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(project_key) DO UPDATE SET
                    project_name=excluded.project_name,
                    project_path=excluded.project_path,
                    process_id=excluded.process_id,
                    process_start_utc=excluded.process_start_utc,
                    generation_id=excluded.generation_id,
                    package_version=excluded.package_version,
                    registry_count=excluded.registry_count,
                    registry_hash=excluded.registry_hash,
                    state=excluded.state,
                    connected=excluded.connected,
                    last_seen_utc=excluded.last_seen_utc
                """,
                (
                    identity.project_key, identity.project_name, identity.project_path,
                    identity.process_id, identity.process_start_utc, identity.generation_id,
                    identity.package_version, identity.registry_count, identity.registry_hash,
                    state, 1 if connected else 0, utc_now(),
                ),
            )
            self._connection.commit()

    def mark_project_state(self, project_key: str, generation_id: str, state: str, connected: bool) -> None:
        with self._lock:
            self._connection.execute(
                "UPDATE projects SET state=?, connected=?, last_seen_utc=? "
                "WHERE project_key=? AND generation_id=?",
                (state, 1 if connected else 0, utc_now(), project_key, generation_id),
            )
            self._connection.commit()

    def list_projects(self) -> list[dict[str, Any]]:
        with self._lock:
            rows = self._connection.execute(
                "SELECT * FROM projects ORDER BY last_seen_utc DESC"
            ).fetchall()
            return [dict(row) for row in rows]

    def save_registry(self, project_key: str, registry_hash: str, tools: list[dict[str, Any]]) -> None:
        with self._lock:
            self._connection.execute(
                "INSERT OR REPLACE INTO registry_snapshots(" 
                "project_key, registry_hash, tools_json, created_utc" 
                ") VALUES(?, ?, ?, ?)",
                (project_key, registry_hash, json.dumps(tools, separators=(",", ":")), utc_now()),
            )
            self._connection.commit()

    def create_request(
        self,
        request_id: str,
        idempotency_key: str | None,
        project_key: str | None,
        generation_id: str | None,
        tool_name: str,
        args_hash: str,
        read_only: bool,
        idempotent: bool,
    ) -> dict[str, Any]:
        now = utc_now()
        with self._lock:
            if idempotency_key:
                existing = self._connection.execute(
                    "SELECT * FROM requests WHERE project_key IS ? AND idempotency_key=?",
                    (project_key, idempotency_key),
                ).fetchone()
                if existing:
                    record = self._request_record(existing)
                    if record["tool_name"] != tool_name or record["args_hash"] != args_hash:
                        raise ValueError("idempotency key was already used with different tool arguments")
                    return record
            self._connection.execute(
                """
                INSERT INTO requests VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, NULL, ?, ?)
                """,
                (
                    request_id, idempotency_key, project_key, generation_id,
                    tool_name, args_hash, 1 if read_only else 0, 1 if idempotent else 0,
                    "accepted_by_broker", now, now,
                ),
            )
            self._connection.commit()
            return self.get_request(request_id) or {}

    def update_request(
        self,
        request_id: str,
        state: str,
        *,
        response: dict[str, Any] | list[Any] | None = None,
        error: str | None = None,
        generation_id: str | None = None,
    ) -> None:
        with self._lock:
            self._connection.execute(
                """
                UPDATE requests SET state=?, response_json=COALESCE(?, response_json),
                    error=COALESCE(?, error), generation_id=COALESCE(?, generation_id), updated_utc=?
                WHERE request_id=?
                """,
                (
                    state,
                    self._vault.encode(response) if response is not None else None,
                    error,
                    generation_id,
                    utc_now(),
                    request_id,
                ),
            )
            self._connection.commit()

    def get_request(self, request_id: str) -> dict[str, Any] | None:
        with self._lock:
            row = self._connection.execute(
                "SELECT * FROM requests WHERE request_id=?", (request_id,)
            ).fetchone()
            if not row:
                return None
            return self._request_record(row)

    def _request_record(self, row: sqlite3.Row) -> dict[str, Any]:
        record = dict(row)
        serialized = record.pop("response_json", None)
        if serialized:
            try:
                record["response"] = self._vault.decode(serialized)
            except (OSError, ValueError, EOFError) as exception:
                # The terminal receipt survives even if its media was lost. Never replay a write.
                record["responseUnavailable"] = str(exception)
        return record

    def prune_history(self, *, retention_days: int = 7, max_response_bytes: int = 256 * 1024 * 1024,
                      batch_size: int = 500, reclaim_pages: int = 2048) -> dict[str, int]:
        """Expire only terminal READ payloads, not request identities or mutation receipts.

        Disabled with retention_days=0 and max_response_bytes=0. Never deletes the user's
        database; up to reclaim_pages free pages are returned to the filesystem per pass, so
        the store lock is held only briefly. Unreferenced vault blobs get a 24-hour grace.
        """
        if retention_days < 0 or max_response_bytes < 0 or batch_size < 1 or reclaim_pages < 0:
            raise ValueError("Invalid retention limits")
        if retention_days == 0 and max_response_bytes == 0:
            return {"expiredResponses": 0, "deletedEvents": 0, "deletedArtifacts": 0, "reclaimedPages": 0}
        cutoff = (datetime.now(timezone.utc) - timedelta(days=retention_days)).isoformat()
        with self._lock:
            rows = self._connection.execute(
                "SELECT request_id, updated_utc, LENGTH(response_json) AS payload_bytes, "
                "CASE WHEN LENGTH(response_json)<1024 THEN response_json END AS metadata "
                "FROM requests WHERE read_only=1 AND state IN ('completed','failed','cancelled') "
                "AND response_json IS NOT NULL ORDER BY updated_utc, request_id"
            ).fetchall()
            sizes = []
            for row in rows:
                try:
                    ref = self._vault.reference(row["metadata"])
                except (ValueError, TypeError):
                    ref = None
                sizes.append(int(ref.get("storedBytes", row["payload_bytes"])) if ref else row["payload_bytes"])
            total = sum(sizes)
            expired = []
            for row, size in zip(rows, sizes):
                if len(expired) >= batch_size:
                    break
                if retention_days and row["updated_utc"] < cutoff or max_response_bytes and total > max_response_bytes:
                    expired.append((row["request_id"],))
                    total -= size
            self._connection.executemany(
                "UPDATE requests SET response_json=NULL, error=COALESCE(error,'RESPONSE_EXPIRED: read payload retention elapsed; receipt preserved') "
                "WHERE request_id=?", expired)
            deleted_events = 0
            if retention_days:
                deleted_events = self._connection.execute(
                    "DELETE FROM events WHERE event_id IN (SELECT event_id FROM events WHERE created_utc<? ORDER BY event_id LIMIT ?)",
                    (cutoff, batch_size)).rowcount
            self._connection.commit()
            # Never unlink a blob while a writer is between encode and commit.
            references = set()
            references_valid = True
            for row in self._connection.execute("SELECT response_json FROM requests WHERE LENGTH(response_json)<1024"):
                try:
                    ref = self._vault.reference(row[0])
                except (ValueError, TypeError):
                    # Preserve potential recovery evidence when a descriptor is
                    # corrupt; healthy read receipts can still be expired above.
                    references_valid = False
                    continue
                if ref:
                    references.add(ref.get("sha256"))
            deleted_artifacts = 0
            grace = (datetime.now(timezone.utc) - timedelta(days=1)).timestamp()
            if references_valid and self._vault.root.exists():
                for blob in self._vault.root.glob("*.json.gz"):
                    digest = blob.name.removesuffix(".json.gz")
                    try:
                        blob_path = self._vault._path(digest)
                    except ValueError:
                        continue
                    if digest not in references and not blob.is_symlink() and blob.stat().st_mtime < grace:
                        blob_path.unlink(missing_ok=True)
                        deleted_artifacts += 1
                        if deleted_artifacts >= batch_size:
                            break
            reclaimed_pages = self._release_free_pages(reclaim_pages) if reclaim_pages else 0
            return {"expiredResponses": len(expired), "deletedEvents": deleted_events,
                    "deletedArtifacts": deleted_artifacts, "reclaimedPages": reclaimed_pages}

    def create_job(self, kind: str, project_key: str | None, message: str = "queued") -> dict[str, Any]:
        job_id = uuid4().hex
        now = utc_now()
        with self._lock:
            self._connection.execute(
                "INSERT INTO jobs VALUES(?, ?, ?, 'queued', 0, ?, NULL, NULL, ?, ?)",
                (job_id, kind, project_key, message, now, now),
            )
            self._connection.commit()
        return self.get_job(job_id) or {}

    def ensure_job(
        self,
        job_id: str,
        kind: str,
        project_key: str | None,
        message: str = "queued",
    ) -> dict[str, Any]:
        now = utc_now()
        with self._lock:
            self._connection.execute(
                "INSERT OR IGNORE INTO jobs VALUES(?, ?, ?, 'queued', 0, ?, NULL, NULL, ?, ?)",
                (job_id, kind, project_key, message, now, now),
            )
            self._connection.commit()
        return self.get_job(job_id) or {}

    def update_job(
        self,
        job_id: str,
        state: str,
        *,
        progress: float | None = None,
        message: str | None = None,
        result: Any = None,
        error: str | None = None,
    ) -> None:
        with self._lock:
            self._connection.execute(
                """
                UPDATE jobs SET state=?, progress=COALESCE(?, progress),
                    message=COALESCE(?, message), result_json=COALESCE(?, result_json),
                    error=COALESCE(?, error), updated_utc=? WHERE job_id=?
                """,
                (
                    state, progress, message,
                    json.dumps(result, separators=(",", ":")) if result is not None else None,
                    error, utc_now(), job_id,
                ),
            )
            self._connection.commit()

    def get_job(self, job_id: str) -> dict[str, Any] | None:
        with self._lock:
            row = self._connection.execute("SELECT * FROM jobs WHERE job_id=?", (job_id,)).fetchone()
            if not row:
                return None
            record = dict(row)
            if record.get("result_json"):
                record["result"] = json.loads(record.pop("result_json"))
            else:
                record.pop("result_json", None)
            return record

    def list_jobs(self, project_key: str, state: str) -> list[dict[str, Any]]:
        with self._lock:
            rows = self._connection.execute(
                "SELECT * FROM jobs WHERE project_key=? AND state=? ORDER BY created_utc",
                (project_key, state),
            ).fetchall()
            records = []
            for row in rows:
                record = dict(row)
                if record.get("result_json"):
                    record["result"] = json.loads(record.pop("result_json"))
                else:
                    record.pop("result_json", None)
                records.append(record)
            return records

    def record_event(
        self, project_key: str | None, generation_id: str | None, event_type: str, payload: dict[str, Any]
    ) -> None:
        with self._lock:
            self._connection.execute(
                "INSERT INTO events(project_key, generation_id, event_type, payload_json, created_utc) "
                "VALUES(?, ?, ?, ?, ?)",
                (project_key, generation_id, event_type, json.dumps(payload, separators=(",", ":")), utc_now()),
            )
            self._connection.commit()
