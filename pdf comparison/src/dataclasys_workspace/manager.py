from __future__ import annotations

import hashlib
import json
import os
import shutil
import socket
import subprocess
import time
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Optional
from uuid import uuid4


class WorkspaceError(Exception):
    """Base exception for workspace-related failures."""


class DecryptError(WorkspaceError):
    """Raised when DataClasys decryption fails."""


class LockError(WorkspaceError):
    """Raised when a server-side lock cannot be acquired."""


class ConflictError(WorkspaceError):
    """Raised when the server-side file changed during local editing."""


class SessionNotFoundError(WorkspaceError):
    """Raised when the requested session cannot be found."""


@dataclass(slots=True)
class FileSnapshot:
    size: int
    mtime_ns: int
    sha256: str
    captured_at: str


@dataclass(slots=True)
class ProgressEvent:
    timestamp: str
    stage: str
    message: str
    session_id: str = ""
    server_path: Optional[str] = None
    local_path: Optional[str] = None
    percent: Optional[float] = None


@dataclass(slots=True)
class SessionInfo:
    app_id: str
    app_display_name: str
    session_id: str
    server_path: str
    source_file: str
    work_file: str
    session_root: str
    created_at: str
    hostname: str
    username: str
    server_open_snapshot: FileSnapshot
    last_seen_server_snapshot: FileSnapshot
    source_snapshot: FileSnapshot
    opened_work_snapshot: FileSnapshot
    last_sync_work_snapshot: FileSnapshot
    save_count: int = 0
    last_saved_at: Optional[str] = None
    workspace_mode: str = "dataclasys"
    lock_file: Optional[str] = None
    recovery_file: Optional[str] = None


@dataclass(slots=True)
class RecoveryRecord:
    state: str
    dirty: bool
    updated_at: str
    session: SessionInfo


ProgressCallback = Callable[[ProgressEvent], None]


class DataClasysWorkspaceManager:
    """
    Workspace manager for DataClasys-protected files.

    Flow:
      1. Copy encrypted file from server to local workspace.
      2. Decrypt local copy by invoking CLSUClient.exe /d.
      3. Work only on the local decrypted file.
      4. Save back to server path with conflict checks.
      5. Clean up local plaintext workspace files.
    """

    def __init__(
        self,
        app_id: str,
        app_display_name: str,
        vendor_name: str = "YourCompany",
        clsu_client_path: str = r"C:\Program Files\DataClasys\UserClient\CLSUClient.exe",
        decrypt_timeout_sec: int = 120,
        decrypt_poll_interval_sec: float = 0.5,
        lock_timeout_sec: int = 0,
        lock_suffix: str = ".dcl.lock",
        progress_callback: ProgressCallback | None = None,
        backup_on_save: bool = True,
        keep_save_backups: int = 3,
        allow_plain_open_without_client: bool = True,
        direct_open_if_plain: bool = False,
    ) -> None:
        if not app_id.strip():
            raise ValueError("app_id must not be empty")
        if not app_display_name.strip():
            raise ValueError("app_display_name must not be empty")
        if decrypt_timeout_sec <= 0:
            raise ValueError("decrypt_timeout_sec must be greater than 0")
        if decrypt_poll_interval_sec <= 0:
            raise ValueError("decrypt_poll_interval_sec must be greater than 0")
        if keep_save_backups < 0:
            raise ValueError("keep_save_backups must be 0 or greater")

        self.app_id = self._sanitize_app_id(app_id)
        self.app_display_name = app_display_name.strip()
        self.vendor_name = vendor_name.strip() or "YourCompany"
        self.clsu_client_path = Path(clsu_client_path)
        self.decrypt_timeout_sec = decrypt_timeout_sec
        self.decrypt_poll_interval_sec = decrypt_poll_interval_sec
        self.lock_timeout_sec = lock_timeout_sec
        self.lock_suffix = lock_suffix
        self.progress_callback = progress_callback
        self.backup_on_save = backup_on_save
        self.keep_save_backups = keep_save_backups
        self.allow_plain_open_without_client = allow_plain_open_without_client
        self.direct_open_if_plain = direct_open_if_plain
        self._sessions: dict[str, SessionInfo] = {}
        self._prepared_direct_saves: set[str] = set()

    # ---------- public API ----------
    def open_server_file(
        self,
        server_path: str | Path,
        use_lock: bool = True,
        progress_callback: ProgressCallback | None = None,
    ) -> SessionInfo:
        server_path = Path(server_path)
        if not server_path.exists():
            raise FileNotFoundError(f"Server file not found: {server_path}")
        if not server_path.is_file():
            raise WorkspaceError(f"Server path is not a file: {server_path}")
        self._ensure_not_open_already(server_path)

        direct_open = self.direct_open_if_plain and self.looks_like_plain_file(server_path)
        session_id = self._new_session_id()
        session_root: Path | None = None
        source_dir: Path | None = None
        work_dir: Path | None = None
        logs_dir: Path | None = None
        if not direct_open:
            session_root, source_dir, work_dir, logs_dir = self._create_session_dirs(session_id)
        callback = progress_callback or self.progress_callback
        lock_file: Path | None = None

        try:
            self._emit_progress(
                callback,
                logs_dir,
                stage="prepare",
                message="Opening server file",
                session_id=session_id,
                server_path=str(server_path),
                percent=0.05,
            )
            if use_lock:
                self._emit_progress(
                    callback,
                    logs_dir,
                    stage="lock",
                    message="Acquiring server-side lock",
                    session_id=session_id,
                    server_path=str(server_path),
                    percent=0.10,
                )
                lock_file = self._acquire_lock(server_path, session_id=session_id)
                self._log_event(logs_dir, f"Lock acquired: {lock_file}")

            server_stat = server_path.stat()

            if direct_open:
                self._emit_progress(
                    callback,
                    logs_dir,
                    stage="direct_open",
                    message="The file looks like plaintext, so it will be opened directly without workspace copy",
                    session_id=session_id,
                    server_path=str(server_path),
                    local_path=str(server_path),
                    percent=0.90,
                )
                source_snapshot = self._capture_snapshot(server_path, mtime_ns=server_stat.st_mtime_ns)
                server_open_snapshot = FileSnapshot(
                    size=server_stat.st_size,
                    mtime_ns=server_stat.st_mtime_ns,
                    sha256=source_snapshot.sha256,
                    captured_at=source_snapshot.captured_at,
                )
                work_snapshot = source_snapshot
                source_file_str = str(server_path)
                work_file_str = str(server_path)
                workspace_mode = "direct"
            else:
                source_file = source_dir / server_path.name
                work_file = work_dir / server_path.name

                self._emit_progress(
                    callback,
                    logs_dir,
                    stage="copy",
                    message="Copying encrypted file to local workspace",
                    session_id=session_id,
                    server_path=str(server_path),
                    local_path=str(source_file),
                    percent=0.20,
                )
                shutil.copy2(server_path, source_file)
                shutil.copy2(source_file, work_file)
                self._log_event(logs_dir, f"Copied server file to source: {server_path} -> {source_file}")
                self._log_event(logs_dir, f"Copied source to work: {source_file} -> {work_file}")

                source_snapshot = self._capture_snapshot(source_file, mtime_ns=server_stat.st_mtime_ns)
                server_open_snapshot = FileSnapshot(
                    size=server_stat.st_size,
                    mtime_ns=server_stat.st_mtime_ns,
                    sha256=source_snapshot.sha256,
                    captured_at=source_snapshot.captured_at,
                )

                workspace_mode = "dataclasys"
                if self.is_dataclasys_available():
                    self.decrypt_file(work_file, logs_dir=logs_dir, progress_callback=callback, session_id=session_id)
                    self._wait_until_decrypted(
                        work_file,
                        logs_dir=logs_dir,
                        progress_callback=callback,
                        session_id=session_id,
                    )
                elif self.allow_plain_open_without_client:
                    workspace_mode = "plain"
                    self._emit_progress(
                        callback,
                        logs_dir,
                        stage="plain_open",
                        message="CLSUClient.exe was not found, so the file will be opened without decryption",
                        session_id=session_id,
                        server_path=str(server_path),
                        local_path=str(work_file),
                        percent=0.90,
                    )
                else:
                    raise DecryptError(f"CLSUClient.exe not found: {self.clsu_client_path}")

                work_snapshot = self._capture_snapshot(work_file)
                source_file_str = str(source_file)
                work_file_str = str(work_file)

            info = SessionInfo(
                app_id=self.app_id,
                app_display_name=self.app_display_name,
                session_id=session_id,
                server_path=str(server_path),
                source_file=source_file_str,
                work_file=work_file_str,
                session_root=str(session_root) if session_root else "",
                created_at=self._now_iso(),
                hostname=self._hostname(),
                username=self._username(),
                server_open_snapshot=server_open_snapshot,
                last_seen_server_snapshot=server_open_snapshot,
                source_snapshot=source_snapshot,
                opened_work_snapshot=work_snapshot,
                last_sync_work_snapshot=work_snapshot,
                workspace_mode=workspace_mode,
                lock_file=str(lock_file) if lock_file else None,
            )
            self._sessions[info.session_id] = info
            self._persist_session_state(info, state="active")
            self._emit_progress(
                callback,
                logs_dir,
                stage="ready",
                message="Workspace session is ready",
                session_id=info.session_id,
                server_path=info.server_path,
                local_path=info.work_file,
                percent=1.0,
            )
            return info
        except Exception:
            if lock_file is not None:
                self._release_lock_path(lock_file)
            self._sessions.pop(session_id, None)
            if session_root is not None:
                self._remove_recovery_file(session_id)
                shutil.rmtree(session_root, ignore_errors=True)
            raise

    def decrypt_file(
        self,
        target_path: str | Path,
        logs_dir: Optional[Path] = None,
        progress_callback: ProgressCallback | None = None,
        session_id: str = "",
    ) -> None:
        target_path = Path(target_path)
        if not self.clsu_client_path.exists():
            raise DecryptError(f"CLSUClient.exe not found: {self.clsu_client_path}")
        if not target_path.exists():
            raise DecryptError(f"Target file not found for decryption: {target_path}")

        self._emit_progress(
            progress_callback,
            logs_dir,
            stage="decrypt",
            message="Running DataClasys decryption command",
            session_id=session_id,
            local_path=str(target_path),
            percent=0.45,
        )
        cmd = [str(self.clsu_client_path), "/d", str(target_path)]
        started = time.time()
        try:
            proc = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                errors="replace",
                timeout=self.decrypt_timeout_sec,
            )
        except subprocess.TimeoutExpired as exc:
            raise DecryptError(f"DataClasys decryption command timed out for {target_path}") from exc
        elapsed = time.time() - started

        if logs_dir:
            self._log(logs_dir / "decrypt_command.log", f"COMMAND: {cmd}")
            self._log(logs_dir / "decrypt_command.log", f"RETURN CODE: {proc.returncode}")
            if proc.stdout:
                self._log(logs_dir / "decrypt_stdout.log", proc.stdout)
            if proc.stderr:
                self._log(logs_dir / "decrypt_stderr.log", proc.stderr)
            self._log(logs_dir / "events.log", f"Decrypt command finished in {elapsed:.2f}s")

        if proc.returncode != 0:
            raise DecryptError(
                f"DataClasys decryption failed (rc={proc.returncode}) for {target_path}. "
                f"stderr={proc.stderr.strip()!r}"
            )

    def prepare_for_save(
        self,
        session: SessionInfo | str | None = None,
        detect_conflict: bool = True,
        create_backup: Optional[bool] = None,
        progress_callback: ProgressCallback | None = None,
    ) -> None:
        session = self._resolve_session(session)
        server_path = Path(session.server_path)
        work_file = Path(session.work_file)
        logs_dir = self._get_logs_dir(session)
        callback = progress_callback or self.progress_callback

        if not work_file.exists():
            raise WorkspaceError(f"Work file not found: {work_file}")
        if not server_path.parent.exists():
            raise WorkspaceError(f"Server parent directory not found: {server_path.parent}")

        self._emit_progress(
            callback,
            logs_dir,
            stage="save_prepare",
            message="Preparing to save local work file",
            session_id=session.session_id,
            server_path=session.server_path,
            local_path=session.work_file,
            percent=0.05,
        )
        if detect_conflict:
            self._check_save_conflict(session, logs_dir=logs_dir)

        should_backup = self.backup_on_save if create_backup is None else create_backup
        if should_backup and session.workspace_mode != "direct":
            self._create_work_backup(session, logs_dir=logs_dir)

        if session.workspace_mode == "direct":
            self._prepared_direct_saves.add(session.session_id)

    def cancel_prepared_save(self, session: SessionInfo | str | None = None) -> None:
        session = self._resolve_session(session)
        self._prepared_direct_saves.discard(session.session_id)

    def save_back(
        self,
        session: SessionInfo | str | None = None,
        verify_size_only: bool = False,
        detect_conflict: bool = True,
        create_backup: Optional[bool] = None,
        progress_callback: ProgressCallback | None = None,
    ) -> None:
        session = self._resolve_session(session)
        server_path = Path(session.server_path)
        work_file = Path(session.work_file)
        logs_dir = self._get_logs_dir(session)
        callback = progress_callback or self.progress_callback
        direct_mode = session.workspace_mode == "direct"

        if not work_file.exists():
            raise WorkspaceError(f"Work file not found: {work_file}")
        if not server_path.parent.exists():
            raise WorkspaceError(f"Server parent directory not found: {server_path.parent}")

        if direct_mode:
            if detect_conflict and session.session_id not in self._prepared_direct_saves:
                raise WorkspaceError(
                    "Direct-open sessions require prepare_for_save() before the caller writes to the original file"
                )
            self._emit_progress(
                callback,
                logs_dir,
                stage="save_finalize",
                message="Finalizing direct save on the original file",
                session_id=session.session_id,
                server_path=session.server_path,
                local_path=session.work_file,
                percent=0.75,
            )
        else:
            self.prepare_for_save(
                session,
                detect_conflict=detect_conflict,
                create_backup=create_backup,
                progress_callback=callback,
            )
            temp_server_path = server_path.with_name(f"{server_path.name}.{session.session_id}.uploading")
            try:
                self._emit_progress(
                    callback,
                    logs_dir,
                    stage="save_copy",
                    message="Copying local work file to server",
                    session_id=session.session_id,
                    server_path=session.server_path,
                    local_path=session.work_file,
                    percent=0.50,
                )
                shutil.copy2(work_file, temp_server_path)
                os.replace(temp_server_path, server_path)
            finally:
                if temp_server_path.exists():
                    temp_server_path.unlink(missing_ok=True)

            self._log_event(logs_dir, f"Saved back work file to server: {work_file} -> {server_path}")

        try:
            work_snapshot = self._capture_snapshot(work_file)
            server_stat = server_path.stat()

            if verify_size_only:
                if work_snapshot.size != server_stat.st_size:
                    raise WorkspaceError(
                        f"Server file size mismatch after save_back. local={work_snapshot.size}, server={server_stat.st_size}"
                    )
                self._log_event(logs_dir, f"Verified size match: {work_snapshot.size} bytes")
            else:
                server_hash = self._hash_file(server_path)
                if work_snapshot.size != server_stat.st_size or work_snapshot.sha256 != server_hash:
                    raise WorkspaceError("Server file verification failed after save_back")
                self._log_event(logs_dir, "Verified hash match after save_back")

            session.save_count += 1
            session.last_saved_at = self._now_iso()
            session.last_sync_work_snapshot = work_snapshot
            session.last_seen_server_snapshot = FileSnapshot(
                size=server_stat.st_size,
                mtime_ns=server_stat.st_mtime_ns,
                sha256=work_snapshot.sha256,
                captured_at=session.last_saved_at,
            )
            self._persist_session_state(session, state="active")
            self._emit_progress(
                callback,
                logs_dir,
                stage="saved",
                message="Local work file saved to server",
                session_id=session.session_id,
                server_path=session.server_path,
                local_path=session.work_file,
                percent=1.0,
            )
        finally:
            self._prepared_direct_saves.discard(session.session_id)

    def close_session(
        self,
        session: SessionInfo | str | None = None,
        cleanup: bool = True,
        progress_callback: ProgressCallback | None = None,
    ) -> None:
        session = self._resolve_session(session)
        logs_dir = self._get_logs_dir(session)
        callback = progress_callback or self.progress_callback

        self._emit_progress(
            callback,
            logs_dir,
            stage="close",
            message="Closing workspace session",
            session_id=session.session_id,
            server_path=session.server_path,
            local_path=session.work_file,
            percent=0.10,
        )
        self._release_lock(session)
        self._log_event(logs_dir, "Released lock")
        self._sessions.pop(session.session_id, None)
        self._prepared_direct_saves.discard(session.session_id)

        if cleanup:
            self.cleanup_session(session)
            return

        self._persist_session_state(session, state="detached")
        self._emit_progress(
            callback,
            logs_dir,
            stage="closed",
            message="Workspace session detached without cleanup",
            session_id=session.session_id,
            server_path=session.server_path,
            local_path=session.work_file,
            percent=1.0,
        )

    def cleanup_session(self, session: SessionInfo | str | None = None) -> None:
        session = self._resolve_session(session)
        self._sessions.pop(session.session_id, None)
        self._prepared_direct_saves.discard(session.session_id)
        if session.recovery_file:
            self._remove_recovery_file(session.session_id, session.recovery_file)
        if session.session_root and Path(session.session_root).exists():
            session_root = Path(session.session_root)
            shutil.rmtree(session_root, ignore_errors=True)

    def cleanup_stale_sessions(self, stale_hours: int = 24) -> list[str]:
        if stale_hours < 0:
            raise ValueError("stale_hours must be 0 or greater")

        removed: list[str] = []
        app_root = self.get_app_root()
        sessions_root = app_root / "sessions"
        if sessions_root.exists():
            now = time.time()
            cutoff = stale_hours * 3600
            for child in sessions_root.iterdir():
                if not child.is_dir() or child.name in self._sessions:
                    continue
                if now - child.stat().st_mtime >= cutoff:
                    shutil.rmtree(child, ignore_errors=True)
                    self._remove_recovery_file(child.name)
                    removed.append(str(child))

        recovery_root = app_root / "recovery"
        if recovery_root.exists():
            for recovery_file in recovery_root.glob("*.json"):
                session_root = sessions_root / recovery_file.stem
                if session_root.exists():
                    continue
                recovery_file.unlink(missing_ok=True)
                removed.append(str(recovery_file))
        return removed

    def require_active_session(self) -> SessionInfo:
        if not self._sessions:
            raise WorkspaceError("No active workspace session")
        if len(self._sessions) > 1:
            raise WorkspaceError("Multiple active sessions exist; specify the session explicitly")
        return next(iter(self._sessions.values()))

    def list_active_sessions(self) -> list[SessionInfo]:
        return list(self._sessions.values())

    def list_recovery_records(self) -> list[RecoveryRecord]:
        records: list[RecoveryRecord] = []
        recovery_root = self.get_app_root() / "recovery"
        if not recovery_root.exists():
            return records

        for path in recovery_root.glob("*.json"):
            try:
                records.append(self._recovery_from_dict(self._read_json(path)))
            except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError):
                continue
        records.sort(key=lambda item: item.updated_at, reverse=True)
        return records

    def restore_session(self, session_id: str, reacquire_lock: bool = False) -> SessionInfo:
        recovery_file = self.get_app_root() / "recovery" / f"{session_id}.json"
        if not recovery_file.exists():
            raise SessionNotFoundError(f"Recovery record not found: {session_id}")

        session = self._recovery_from_dict(self._read_json(recovery_file)).session
        if not session.session_root:
            raise SessionNotFoundError(f"Session {session_id} has no recoverable workspace directory")
        if not Path(session.session_root).exists():
            raise SessionNotFoundError(f"Session directory not found: {session.session_root}")
        if not Path(session.work_file).exists():
            raise SessionNotFoundError(f"Work file not found: {session.work_file}")
        if reacquire_lock:
            session.lock_file = str(self._acquire_lock(Path(session.server_path), session_id=session.session_id))

        self._sessions[session.session_id] = session
        self._persist_session_state(session, state="active")
        return session

    def is_session_dirty(self, session: SessionInfo | str | None = None) -> bool:
        session = self._resolve_session(session)
        work_file = Path(session.work_file)
        if not work_file.exists():
            return False
        return not self._content_matches(self._capture_snapshot(work_file), session.last_sync_work_snapshot)

    def get_app_root(self) -> Path:
        local_appdata = os.environ.get("LOCALAPPDATA")
        if not local_appdata:
            raise WorkspaceError("LOCALAPPDATA is not available")
        app_root = Path(local_appdata) / self.vendor_name / "Workspaces" / self.app_id
        app_root.mkdir(parents=True, exist_ok=True)
        (app_root / "sessions").mkdir(parents=True, exist_ok=True)
        (app_root / "recovery").mkdir(parents=True, exist_ok=True)
        return app_root

    def is_dataclasys_available(self) -> bool:
        return self.clsu_client_path.exists()

    def looks_like_plain_file(self, target_path: str | Path) -> bool:
        target_path = Path(target_path)
        if not target_path.exists() or not target_path.is_file():
            return False
        return self._looks_plain_for_direct_open(target_path)

    # ---------- helpers ----------
    @staticmethod
    def _new_session_id() -> str:
        return datetime.now().strftime("%Y%m%d_%H%M%S") + "_" + uuid4().hex[:6]

    def _create_session_dirs(self, session_id: str) -> tuple[Path, Path, Path, Path]:
        app_root = self.get_app_root()
        session_root = app_root / "sessions" / session_id
        source_dir = session_root / "source"
        work_dir = session_root / "work"
        logs_dir = session_root / "logs"
        for path in (source_dir, work_dir, logs_dir):
            path.mkdir(parents=True, exist_ok=True)
        return session_root, source_dir, work_dir, logs_dir

    @staticmethod
    def _get_logs_dir(session: SessionInfo) -> Optional[Path]:
        if not session.session_root:
            return None
        return Path(session.session_root) / "logs"

    def _log_event(self, logs_dir: Optional[Path], message: str) -> None:
        if logs_dir:
            self._log(logs_dir / "events.log", message)

    def _resolve_session(self, session: SessionInfo | str | None) -> SessionInfo:
        if session is None:
            return self.require_active_session()
        if isinstance(session, SessionInfo):
            return session
        if session in self._sessions:
            return self._sessions[session]
        raise SessionNotFoundError(f"Active session not found: {session}")

    def _ensure_not_open_already(self, server_path: Path) -> None:
        normalized = str(server_path.resolve(strict=False)).lower()
        for session in self._sessions.values():
            other = str(Path(session.server_path).resolve(strict=False)).lower()
            if other == normalized:
                raise WorkspaceError(
                    f"Server file is already open in session {session.session_id}: {session.server_path}"
                )

    def _acquire_lock(self, server_path: Path, session_id: str) -> Path:
        lock_path = Path(str(server_path) + self.lock_suffix)
        payload = {
            "session_id": session_id,
            "hostname": self._hostname(),
            "username": self._username(),
            "app_id": self.app_id,
            "created_at": self._now_iso(),
        }
        started = time.time()

        while True:
            try:
                with open(lock_path, "x", encoding="utf-8") as file_obj:
                    json.dump(payload, file_obj, ensure_ascii=False, indent=2)
                return lock_path
            except FileExistsError:
                existing = self._read_lock_payload(lock_path)
                if existing and existing.get("session_id") == session_id and existing.get("app_id") == self.app_id:
                    return lock_path
                if self.lock_timeout_sec <= 0 or (time.time() - started) >= self.lock_timeout_sec:
                    raise LockError(f"Lock file already exists: {lock_path}")
                time.sleep(1.0)
            except OSError as exc:
                raise LockError(f"Failed to create lock file: {lock_path}") from exc

    def _release_lock(self, session: SessionInfo) -> None:
        if session.lock_file:
            self._release_lock_path(Path(session.lock_file))

    @staticmethod
    def _release_lock_path(lock_path: Path) -> None:
        if lock_path.exists():
            try:
                lock_path.unlink()
            except OSError:
                pass

    def _check_save_conflict(self, session: SessionInfo, logs_dir: Optional[Path]) -> None:
        server_path = Path(session.server_path)
        if not server_path.exists():
            raise ConflictError(f"Server file no longer exists: {server_path}")

        self._assert_lock_still_owned(session)
        current_stat = server_path.stat()

        # 1回目の保存ではオープン時の状態と比較して、外部更新を強めに検知する。
        if session.save_count == 0:
            if (
                current_stat.st_size != session.server_open_snapshot.size
                or current_stat.st_mtime_ns != session.server_open_snapshot.mtime_ns
            ):
                if self._hash_file(server_path) != session.server_open_snapshot.sha256:
                    raise ConflictError(f"Server file changed after open: {server_path}")
                self._log_event(logs_dir, "Server metadata changed, but file content still matches open snapshot")
            return

        # 2回目以降はサーバ側の自動再暗号化で stat/hash が変わり得るため、
        # ロックなし運用時だけ best-effort で比較する。
        if session.lock_file:
            self._log_event(
                logs_dir,
                "Skipped strict snapshot comparison after first save because lock ownership is authoritative",
            )
            return

        if (
            current_stat.st_size != session.last_seen_server_snapshot.size
            or current_stat.st_mtime_ns != session.last_seen_server_snapshot.mtime_ns
        ):
            if self._hash_file(server_path) != session.last_seen_server_snapshot.sha256:
                raise ConflictError(f"Server file changed after last save: {server_path}")
            self._log_event(logs_dir, "Server metadata changed, but content still matches last saved snapshot")

    def _assert_lock_still_owned(self, session: SessionInfo) -> None:
        if not session.lock_file:
            return
        lock_path = Path(session.lock_file)
        if not lock_path.exists():
            raise ConflictError(f"Lock file disappeared during session: {lock_path}")

        payload = self._read_lock_payload(lock_path)
        if not payload:
            raise ConflictError(f"Lock file could not be read: {lock_path}")
        if payload.get("session_id") != session.session_id or payload.get("app_id") != self.app_id:
            raise ConflictError(f"Lock is no longer owned by this session: {lock_path}")

    def _create_work_backup(self, session: SessionInfo, logs_dir: Optional[Path]) -> None:
        if not session.session_root:
            return
        backups_dir = Path(session.session_root) / "backups"
        backups_dir.mkdir(parents=True, exist_ok=True)
        work_file = Path(session.work_file)
        backup_path = backups_dir / f"{datetime.now().strftime('%Y%m%d_%H%M%S')}_work{work_file.suffix}"
        shutil.copy2(work_file, backup_path)
        self._log_event(logs_dir, f"Created work backup: {backup_path}")

        if self.keep_save_backups <= 0:
            return

        backups = sorted(backups_dir.glob("*"), key=lambda item: item.stat().st_mtime, reverse=True)
        for stale in backups[self.keep_save_backups :]:
            if stale.is_file():
                stale.unlink(missing_ok=True)

    def _wait_until_decrypted(
        self,
        path: Path,
        logs_dir: Optional[Path] = None,
        progress_callback: ProgressCallback | None = None,
        session_id: str = "",
    ) -> None:
        started = time.time()
        stable_count = 0
        last_size: Optional[int] = None
        last_hash: Optional[str] = None

        self._emit_progress(
            progress_callback,
            logs_dir,
            stage="decrypt_wait",
            message="Waiting until decrypted file becomes stable",
            session_id=session_id,
            local_path=str(path),
            percent=0.70,
        )

        while time.time() - started < self.decrypt_timeout_sec:
            if not path.exists():
                time.sleep(self.decrypt_poll_interval_sec)
                continue
            try:
                current_size = path.stat().st_size
                current_hash = self._quick_hash(path)
            except (PermissionError, OSError):
                time.sleep(self.decrypt_poll_interval_sec)
                continue

            stable_count = stable_count + 1 if current_size == last_size and current_hash == last_hash else 0
            if stable_count >= 1 and self._looks_decrypted(path):
                self._emit_progress(
                    progress_callback,
                    logs_dir,
                    stage="decrypt_ready",
                    message="Decrypted file is ready for local editing",
                    session_id=session_id,
                    local_path=str(path),
                    percent=0.90,
                )
                return

            last_size = current_size
            last_hash = current_hash
            time.sleep(self.decrypt_poll_interval_sec)

        raise DecryptError(f"Timed out waiting for decrypted state: {path}")

    def _looks_decrypted(self, path: Path) -> bool:
        suffix = path.suffix.lower()
        if suffix == ".pdf":
            return self._looks_like_pdf(path)
        if suffix in {".xlsx", ".xlsm", ".docx", ".docm", ".pptx", ".pptm"}:
            return self._looks_like_zip(path)
        if suffix == ".json":
            return self._looks_like_json(path)
        if suffix in {".db", ".sqlite", ".sqlite3"}:
            return self._looks_like_sqlite(path)
        if suffix in {".txt", ".csv", ".tsv", ".log", ".md", ".ini", ".cfg", ".yaml", ".yml", ".xml"}:
            return self._looks_like_text(path)
        try:
            with open(path, "rb") as file_obj:
                file_obj.read(32)
            return True
        except OSError:
            return False

    def _looks_plain_for_direct_open(self, path: Path) -> bool:
        suffix = path.suffix.lower()
        if suffix == ".pdf":
            return self._looks_like_pdf(path)
        if suffix in {".xlsx", ".xlsm", ".docx", ".docm", ".pptx", ".pptm"}:
            return self._looks_like_zip(path)
        if suffix == ".json":
            return self._looks_like_json(path)
        if suffix in {".db", ".sqlite", ".sqlite3"}:
            return self._looks_like_sqlite(path)
        if suffix in {".txt", ".csv", ".tsv", ".log", ".md", ".ini", ".cfg", ".yaml", ".yml", ".xml"}:
            return self._looks_like_text(path)
        return False

    def _persist_session_state(self, session: SessionInfo, state: str) -> None:
        if not session.session_root:
            return
        meta_path = Path(session.session_root) / "meta.json"
        recovery_path = Path(session.recovery_file) if session.recovery_file else self._recovery_path(session.session_id)
        session.recovery_file = str(recovery_path)
        self._write_json(meta_path, asdict(session))
        record = RecoveryRecord(
            state=state,
            dirty=self.is_session_dirty(session) if Path(session.work_file).exists() else False,
            updated_at=self._now_iso(),
            session=session,
        )
        self._write_json(recovery_path, asdict(record))

    def _capture_snapshot(self, path: Path, mtime_ns: Optional[int] = None) -> FileSnapshot:
        stat = path.stat()
        return FileSnapshot(
            size=stat.st_size,
            mtime_ns=stat.st_mtime_ns if mtime_ns is None else mtime_ns,
            sha256=self._hash_file(path),
            captured_at=self._now_iso(),
        )

    @staticmethod
    def _content_matches(left: FileSnapshot, right: FileSnapshot) -> bool:
        return left.size == right.size and left.sha256 == right.sha256

    def _recovery_path(self, session_id: str) -> Path:
        return self.get_app_root() / "recovery" / f"{session_id}.json"

    def _remove_recovery_file(self, session_id: str, recovery_file: str | None = None) -> None:
        (Path(recovery_file) if recovery_file else self._recovery_path(session_id)).unlink(missing_ok=True)

    def _read_lock_payload(self, lock_path: Path) -> dict[str, Any] | None:
        try:
            with open(lock_path, "r", encoding="utf-8") as file_obj:
                data = json.load(file_obj)
        except (OSError, json.JSONDecodeError):
            return None
        return data if isinstance(data, dict) else None

    @staticmethod
    def _read_json(path: Path) -> dict[str, Any]:
        with open(path, "r", encoding="utf-8") as file_obj:
            data = json.load(file_obj)
        if not isinstance(data, dict):
            raise ValueError(f"JSON root must be an object: {path}")
        return data

    @staticmethod
    def _write_json(path: Path, data: dict[str, Any]) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        temp_path = path.with_name(f"{path.name}.{uuid4().hex}.tmp")
        with open(temp_path, "w", encoding="utf-8") as file_obj:
            json.dump(data, file_obj, ensure_ascii=False, indent=2)
        os.replace(temp_path, path)

    @staticmethod
    def _snapshot_from_dict(data: dict[str, Any]) -> FileSnapshot:
        return FileSnapshot(
            size=int(data["size"]),
            mtime_ns=int(data["mtime_ns"]),
            sha256=str(data["sha256"]),
            captured_at=str(data["captured_at"]),
        )

    def _session_from_dict(self, data: dict[str, Any]) -> SessionInfo:
        return SessionInfo(
            app_id=str(data["app_id"]),
            app_display_name=str(data["app_display_name"]),
            session_id=str(data["session_id"]),
            server_path=str(data["server_path"]),
            source_file=str(data["source_file"]),
            work_file=str(data["work_file"]),
            session_root=str(data.get("session_root", "")),
            created_at=str(data["created_at"]),
            hostname=str(data.get("hostname", "")),
            username=str(data.get("username", "")),
            server_open_snapshot=self._snapshot_from_dict(data["server_open_snapshot"]),
            last_seen_server_snapshot=self._snapshot_from_dict(
                data.get("last_seen_server_snapshot", data["server_open_snapshot"])
            ),
            source_snapshot=self._snapshot_from_dict(data["source_snapshot"]),
            opened_work_snapshot=self._snapshot_from_dict(data["opened_work_snapshot"]),
            last_sync_work_snapshot=self._snapshot_from_dict(
                data.get("last_sync_work_snapshot", data["opened_work_snapshot"])
            ),
            save_count=int(data.get("save_count", 0)),
            last_saved_at=data.get("last_saved_at"),
            workspace_mode=str(data.get("workspace_mode", "dataclasys")),
            lock_file=data.get("lock_file"),
            recovery_file=data.get("recovery_file"),
        )

    def _recovery_from_dict(self, data: dict[str, Any]) -> RecoveryRecord:
        return RecoveryRecord(
            state=str(data["state"]),
            dirty=bool(data.get("dirty", False)),
            updated_at=str(data["updated_at"]),
            session=self._session_from_dict(data["session"]),
        )

    def _emit_progress(
        self,
        callback: ProgressCallback | None,
        logs_dir: Optional[Path],
        *,
        stage: str,
        message: str,
        session_id: str = "",
        server_path: Optional[str] = None,
        local_path: Optional[str] = None,
        percent: Optional[float] = None,
    ) -> None:
        event = ProgressEvent(
            timestamp=self._now_iso(),
            stage=stage,
            message=message,
            session_id=session_id,
            server_path=server_path,
            local_path=local_path,
            percent=percent,
        )
        if logs_dir:
            self._log(logs_dir / "events.log", f"{stage}: {message}")
        if callback is not None:
            callback(event)

    @staticmethod
    def _looks_like_pdf(path: Path) -> bool:
        try:
            with open(path, "rb") as file_obj:
                return file_obj.read(5) == b"%PDF-"
        except OSError:
            return False

    @staticmethod
    def _looks_like_zip(path: Path) -> bool:
        try:
            with open(path, "rb") as file_obj:
                return file_obj.read(2) == b"PK"
        except OSError:
            return False

    @staticmethod
    def _looks_like_json(path: Path) -> bool:
        try:
            text = path.read_text(encoding="utf-8")
            if not text.lstrip().startswith(("{", "[")):
                return False
            json.loads(text)
            return True
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            return False

    @staticmethod
    def _looks_like_sqlite(path: Path) -> bool:
        try:
            with open(path, "rb") as file_obj:
                return file_obj.read(16) == b"SQLite format 3\x00"
        except OSError:
            return False

    @staticmethod
    def _looks_like_text(path: Path) -> bool:
        try:
            sample = path.read_bytes()[:4096]
        except OSError:
            return False

        if not sample:
            return True
        if b"\x00" in sample:
            return False

        for encoding in ("utf-8-sig", "utf-8", "cp932", "shift_jis", "utf-16", "utf-16-le", "utf-16-be"):
            try:
                text = sample.decode(encoding)
            except UnicodeDecodeError:
                continue

            meaningful = [ch for ch in text if ch not in "\r\n\t"]
            if not meaningful:
                return True
            printable_ratio = sum(ch.isprintable() for ch in meaningful) / len(meaningful)
            if printable_ratio >= 0.85:
                return True
        return False

    @staticmethod
    def _quick_hash(path: Path, chunk_size: int = 1024 * 64) -> str:
        hasher = hashlib.sha256()
        with open(path, "rb") as file_obj:
            hasher.update(file_obj.read(chunk_size))
        return hasher.hexdigest()

    @staticmethod
    def _hash_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
        hasher = hashlib.sha256()
        with open(path, "rb") as file_obj:
            while True:
                chunk = file_obj.read(chunk_size)
                if not chunk:
                    break
                hasher.update(chunk)
        return hasher.hexdigest()

    @staticmethod
    def _sanitize_app_id(app_id: str) -> str:
        out = []
        for ch in app_id.strip():
            out.append(ch if ch.isalnum() or ch in {"-", "_"} else "_")
        return "".join(out)

    @staticmethod
    def _now_iso() -> str:
        return datetime.now().isoformat(timespec="seconds")

    @staticmethod
    def _hostname() -> str:
        return os.environ.get("COMPUTERNAME") or socket.gethostname()

    @staticmethod
    def _username() -> str:
        return os.environ.get("USERNAME", "")

    @staticmethod
    def _log(path: Path, message: str) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a", encoding="utf-8") as file_obj:
            file_obj.write(f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] {message}\n")
