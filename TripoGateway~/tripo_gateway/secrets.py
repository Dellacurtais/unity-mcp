from __future__ import annotations

import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import os
import sys
import threading
from typing import Protocol


ERROR_NOT_FOUND = 1168
CRED_TYPE_GENERIC = 1
CRED_PERSIST_LOCAL_MACHINE = 2
MAX_API_KEY_BYTES = 2048


class CredentialStoreError(RuntimeError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


class CredentialStore(Protocol):
    @property
    def available(self) -> bool: ...

    @property
    def persistent(self) -> bool: ...

    @property
    def name(self) -> str: ...

    def read(self) -> str | None: ...

    def write(self, value: str) -> None: ...

    def delete(self) -> None: ...


class UnavailableCredentialStore:
    def __init__(self, platform_name: str | None = None) -> None:
        self._platform_name = platform_name or sys.platform

    @property
    def available(self) -> bool:
        return False

    @property
    def persistent(self) -> bool:
        return False

    @property
    def name(self) -> str:
        return "unavailable"

    def read(self) -> str | None:
        return None

    def write(self, value: str) -> None:
        raise CredentialStoreError(
            "CREDENTIAL_STORE_UNAVAILABLE",
            f"Secure credential storage is unavailable on {self._platform_name}; use TRIPO_API_KEY.",
        )

    def delete(self) -> None:
        return None


class MemoryCredentialStore:
    """Non-production store used by deterministic tests and injected providers."""

    def __init__(self, value: str | None = None) -> None:
        self._value = value

    @property
    def available(self) -> bool:
        return True

    @property
    def persistent(self) -> bool:
        return False

    @property
    def name(self) -> str:
        return "memory"

    def read(self) -> str | None:
        return self._value

    def write(self, value: str) -> None:
        self._value = value

    def delete(self) -> None:
        self._value = None


if sys.platform == "win32":
    class _CREDENTIALW(ctypes.Structure):
        _fields_ = [
            ("Flags", wintypes.DWORD),
            ("Type", wintypes.DWORD),
            ("TargetName", wintypes.LPWSTR),
            ("Comment", wintypes.LPWSTR),
            ("LastWritten", wintypes.FILETIME),
            ("CredentialBlobSize", wintypes.DWORD),
            ("CredentialBlob", ctypes.POINTER(ctypes.c_ubyte)),
            ("Persist", wintypes.DWORD),
            ("AttributeCount", wintypes.DWORD),
            ("Attributes", ctypes.c_void_p),
            ("TargetAlias", wintypes.LPWSTR),
            ("UserName", wintypes.LPWSTR),
        ]


class WindowsCredentialStore:
    def __init__(self, target: str) -> None:
        if sys.platform != "win32":
            raise CredentialStoreError(
                "CREDENTIAL_STORE_UNAVAILABLE", "Windows Credential Manager is unavailable."
            )
        if not target or len(target) > 256:
            raise ValueError("credential target must contain 1-256 characters")
        self._target = target
        self._advapi = ctypes.WinDLL("Advapi32.dll", use_last_error=True)
        self._cred_read = self._advapi.CredReadW
        self._cred_read.argtypes = [
            wintypes.LPCWSTR,
            wintypes.DWORD,
            wintypes.DWORD,
            ctypes.POINTER(ctypes.POINTER(_CREDENTIALW)),
        ]
        self._cred_read.restype = wintypes.BOOL
        self._cred_write = self._advapi.CredWriteW
        self._cred_write.argtypes = [ctypes.POINTER(_CREDENTIALW), wintypes.DWORD]
        self._cred_write.restype = wintypes.BOOL
        self._cred_delete = self._advapi.CredDeleteW
        self._cred_delete.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD]
        self._cred_delete.restype = wintypes.BOOL
        self._cred_free = self._advapi.CredFree
        self._cred_free.argtypes = [ctypes.c_void_p]
        self._cred_free.restype = None

    @property
    def available(self) -> bool:
        return True

    @property
    def persistent(self) -> bool:
        return True

    @property
    def name(self) -> str:
        return "windows_credential_manager"

    def read(self) -> str | None:
        pointer = ctypes.POINTER(_CREDENTIALW)()
        if not self._cred_read(
            self._target, CRED_TYPE_GENERIC, 0, ctypes.byref(pointer)
        ):
            error = ctypes.get_last_error()
            if error == ERROR_NOT_FOUND:
                return None
            raise self._windows_error("CREDENTIAL_READ_FAILED", error)
        try:
            credential = pointer.contents
            if not credential.CredentialBlob or credential.CredentialBlobSize == 0:
                return None
            raw = ctypes.string_at(
                credential.CredentialBlob, credential.CredentialBlobSize
            )
            try:
                return raw.decode("utf-8")
            except UnicodeDecodeError as exception:
                raise CredentialStoreError(
                    "CREDENTIAL_VALUE_INVALID", "Stored Tripo credential is not valid UTF-8."
                ) from exception
        finally:
            self._cred_free(pointer)

    def write(self, value: str) -> None:
        encoded = value.encode("utf-8")
        if not encoded or len(encoded) > MAX_API_KEY_BYTES:
            raise CredentialStoreError(
                "CREDENTIAL_VALUE_INVALID",
                f"Tripo API key must contain 1-{MAX_API_KEY_BYTES} UTF-8 bytes.",
            )
        blob = ctypes.create_string_buffer(encoded, len(encoded))
        credential = _CREDENTIALW()
        credential.Type = CRED_TYPE_GENERIC
        credential.TargetName = self._target
        credential.Comment = "Unity Scene Analyzer Tripo API key"
        credential.CredentialBlobSize = len(encoded)
        credential.CredentialBlob = ctypes.cast(
            blob, ctypes.POINTER(ctypes.c_ubyte)
        )
        credential.Persist = CRED_PERSIST_LOCAL_MACHINE
        credential.UserName = "Tripo API"
        try:
            if not self._cred_write(ctypes.byref(credential), 0):
                raise self._windows_error(
                    "CREDENTIAL_WRITE_FAILED", ctypes.get_last_error()
                )
        finally:
            ctypes.memset(ctypes.addressof(blob), 0, len(encoded))

    def delete(self) -> None:
        if self._cred_delete(self._target, CRED_TYPE_GENERIC, 0):
            return
        error = ctypes.get_last_error()
        if error != ERROR_NOT_FOUND:
            raise self._windows_error("CREDENTIAL_DELETE_FAILED", error)

    @staticmethod
    def _windows_error(code: str, error: int) -> CredentialStoreError:
        return CredentialStoreError(code, f"Windows Credential Manager failed ({error}).")


@dataclass(frozen=True)
class CredentialStatus:
    configured: bool
    source: str
    store_available: bool
    persistent: bool
    store: str
    platform: str

    def public_dict(self) -> dict[str, object]:
        return {
            "configured": self.configured,
            "source": self.source,
            "storeAvailable": self.store_available,
            "persistent": self.persistent,
            "store": self.store,
            "platform": self.platform,
        }


class TripoCredentialManager:
    def __init__(
        self, environment_key: str | None, store: CredentialStore
    ) -> None:
        self._environment_key = environment_key or None
        self._store = store
        self._stored_key: str | None = None
        self._store_error: CredentialStoreError | None = None
        self._lock = threading.RLock()
        if store.available:
            try:
                self._stored_key = store.read()
            except CredentialStoreError as exception:
                self._store_error = exception

    @property
    def api_key(self) -> str | None:
        with self._lock:
            return self._stored_key or self._environment_key

    @property
    def configured(self) -> bool:
        return bool(self.api_key)

    def status(self) -> CredentialStatus:
        with self._lock:
            source = self._store.name if self._stored_key else (
                "environment" if self._environment_key else "none"
            )
            return CredentialStatus(
                configured=bool(self._stored_key or self._environment_key),
                source=source,
                store_available=self._store.available and self._store_error is None,
                persistent=self._store.persistent,
                store=self._store.name,
                platform=sys.platform,
            )

    def replace(self, value: str) -> CredentialStatus:
        validate_api_key(value)
        with self._lock:
            self._store.write(value)
            self._stored_key = value
            self._store_error = None
            return self.status()

    def clear(self) -> CredentialStatus:
        with self._lock:
            self._store.delete()
            self._stored_key = None
            self._store_error = None
            return self.status()


def validate_api_key(value: str) -> None:
    if not isinstance(value, str):
        raise CredentialStoreError(
            "TRIPO_API_KEY_INVALID", "Tripo API key must be a string."
        )
    encoded = value.encode("utf-8")
    if len(value) < 8 or len(encoded) > MAX_API_KEY_BYTES:
        raise CredentialStoreError(
            "TRIPO_API_KEY_INVALID",
            f"Tripo API key must contain 8-{MAX_API_KEY_BYTES} UTF-8 bytes.",
        )
    if value != value.strip() or any(ord(character) < 33 or ord(character) == 127 for character in value):
        raise CredentialStoreError(
            "TRIPO_API_KEY_INVALID", "Tripo API key cannot contain whitespace or control characters."
        )


def create_platform_credential_store(api_base_url: str) -> CredentialStore:
    region = "cn" if api_base_url.endswith(".com/v3") else "global"
    target = f"Maicon.UnitySceneAnalyzer.TripoGateway.TripoApiKey.{region}"
    if os.name == "nt" and sys.platform == "win32":
        return WindowsCredentialStore(target)
    return UnavailableCredentialStore()
