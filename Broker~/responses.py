"""Private, integrity-checked response vault; MCP responses keep their existing shape."""
from __future__ import annotations

import gzip
import hashlib
import json
import os
import re
from pathlib import Path
from typing import Any
from uuid import uuid4


class ResponseVault:
    MARKER = "_unityMcpStoredResponse"

    def __init__(self, root: Path, inline_bytes: int = 256 * 1024) -> None:
        if inline_bytes < 1:
            raise ValueError("response inline limit must be positive")
        self.root = root.resolve()
        self.inline_bytes = inline_bytes

    def encode(self, response: Any) -> str:
        serialized = json.dumps(response, separators=(",", ":"))
        raw = serialized.encode("utf-8")
        if len(raw) <= self.inline_bytes:
            return serialized
        digest = hashlib.sha256(raw).hexdigest()
        self.root.mkdir(parents=True, exist_ok=True)
        destination = self._path(digest)
        compressed = gzip.compress(raw, compresslevel=3, mtime=0)
        temporary = self.root / (uuid4().hex + ".tmp")
        try:
            with temporary.open("xb") as stream:
                stream.write(compressed)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, destination)
        finally:
            temporary.unlink(missing_ok=True)
        return json.dumps({self.MARKER: {"sha256": digest, "bytes": len(raw),
                                        "storedBytes": len(compressed), "encoding": "gzip-json-v1"}},
                          separators=(",", ":"))

    def _path(self, digest: str) -> Path:
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ValueError("Invalid stored response hash")
        candidate = self.root / (digest + ".json.gz")
        if candidate.is_symlink() or candidate.resolve().parent != self.root:
            raise ValueError("Stored response is outside its vault")
        return candidate

    def reference(self, serialized: str | None) -> dict[str, Any] | None:
        if not serialized or len(serialized) > 1024:
            return None
        value = json.loads(serialized)
        if isinstance(value, dict) and set(value) == {self.MARKER}:
            reference = value[self.MARKER]
            if not isinstance(reference, dict):
                raise ValueError("Invalid stored response descriptor")
            digest = reference.get("sha256")
            if (not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest)
                    or reference.get("encoding") != "gzip-json-v1"):
                raise ValueError("Invalid stored response descriptor")
            for field in ("bytes", "storedBytes"):
                size = reference.get(field)
                if isinstance(size, bool) or not isinstance(size, int) or size < 1:
                    raise ValueError("Invalid stored response descriptor")
            return reference
        return None

    def decode(self, serialized: str) -> Any:
        ref = self.reference(serialized)
        if ref is None:
            return json.loads(serialized)
        size = ref.get("bytes")
        # Read no more than the expected size. A corrupt gzip cannot expand without a bound.
        with gzip.open(self._path(ref.get("sha256")), "rb") as stream:
            raw = stream.read(size + 1)
        if len(raw) != size or hashlib.sha256(raw).hexdigest() != ref["sha256"]:
            raise ValueError("Stored response failed integrity validation")
        return json.loads(raw)
