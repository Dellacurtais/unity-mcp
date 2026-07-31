from __future__ import annotations

import json
import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from uuid import uuid4


CURRENT_SCHEMA_VERSION = 2


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


class BrokerStore:
    def __init__(self, path: Path) -> None:
        path = path.expanduser().resolve()
        path.parent.mkdir(parents=True, exist_ok=True)
        self._path = path
        self._connection = sqlite3.connect(str(path), check_same_thread=False)
        self._connection.row_factory = sqlite3.Row
        self._lock = threading.RLock()
        try:
            with self._lock:
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
                "ELSE COALESCE(error, 'Broker restarted after Unity accepted the request') END, "
                "updated_utc=? WHERE state IN ('accepted_by_unity', 'running_in_unity')",
                (now,),
            )
            self._connection.execute(
                "UPDATE jobs SET state='waiting_for_reconnect', "
                "message='Broker restarted; waiting for Unity bridge', updated_utc=? "
                "WHERE state IN ('queued', 'dispatched', 'running')",
                (now,),
            )
            self._connection.commit()

    def load_registry_snapshots(self) -> list[dict[str, Any]]:
        with self._lock:
            rows = self._connection.execute(
                "SELECT project_key, registry_hash, tools_json, created_utc "
                "FROM registry_snapshots ORDER BY created_utc"
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
                    record = dict(existing)
                    if record["tool_name"] != tool_name or record["args_hash"] != args_hash:
                        raise ValueError("idempotency key was already used with different tool arguments")
                    if record.get("response_json"):
                        record["response"] = json.loads(record.pop("response_json"))
                    else:
                        record.pop("response_json", None)
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
                    json.dumps(response, separators=(",", ":")) if response is not None else None,
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
            record = dict(row)
            if record.get("response_json"):
                record["response"] = json.loads(record.pop("response_json"))
            else:
                record.pop("response_json", None)
            return record

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
