from __future__ import annotations

import base64
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import webbrowser
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Optional
from urllib.parse import parse_qs, urlparse

from src.dataclasys_workspace import (
    ConflictError,
    DataClasysWorkspaceManager,
    DecryptError,
    ProgressEvent,
    SessionInfo,
    WorkspaceError,
)


class AppBackend:
    def __init__(self) -> None:
        self.latest_progress: dict[str, object] = {
            "stage": "",
            "message": "",
            "percent": None,
            "timestamp": "",
        }
        self.last_heartbeat: float = time.time()
        self.shutdown_requested: bool = False
        self.lock = threading.Lock()
        self.active_sessions: dict[str, SessionInfo] = {}
        self.logs: list[str] = []
        self.log_file_path = Path(__file__).resolve().parent / "debug_trace.log"

        clsu_path = r"C:\Program Files\DataClasys\UserClient\CLSUClient.exe"
        if not Path(clsu_path).exists():
            alt_path = r"C:\Program Files (x86)\DataClasys\UserClient\CLSUClient.exe"
            if Path(alt_path).exists():
                clsu_path = alt_path

        self.manager = DataClasysWorkspaceManager(
            app_id="ks_project_tool",
            app_display_name="KS工程管理ツール",
            vendor_name="KinkiSharyo",
            clsu_client_path=clsu_path,
            decrypt_timeout_sec=120,
            decrypt_poll_interval_sec=0.5,
            lock_timeout_sec=0,
            progress_callback=self.on_progress,
            backup_on_save=True,
            keep_save_backups=3,
            allow_plain_open_without_client=True,
            direct_open_if_plain=True,
        )
        self.log("INIT", f"AppBackend initialized. clsu_path={clsu_path} (exists={self.manager.is_dataclasys_available()})")
        try:
            self.manager.cleanup_stale_sessions(stale_hours=24)
        except Exception as exc:
            self.log("WARN", f"cleanup_stale_sessions failed: {exc}")

    def log(self, category: str, message: str) -> None:
        line = f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')[:-3]}] [{category}] {message}"
        print(line, flush=True)
        with self.lock:
            self.logs.append(line)
            if len(self.logs) > 1000:
                self.logs.pop(0)
            try:
                with open(self.log_file_path, "a", encoding="utf-8") as f:
                    f.write(line + "\n")
            except OSError:
                pass

    def on_progress(self, event: ProgressEvent) -> None:
        percent = "" if event.percent is None else f" ({event.percent:.0%})"
        self.log(f"STAGE:{event.stage}", f"{percent} {event.message} [local={event.local_path}]")
        with self.lock:
            self.latest_progress = {
                "stage": event.stage,
                "message": event.message,
                "percent": event.percent,
                "timestamp": event.timestamp,
                "server_path": event.server_path,
                "local_path": event.local_path,
            }

    def close_slot_session(self, kind: str) -> None:
        old_sess = self.active_sessions.pop(kind, None)
        if old_sess is not None:
            try:
                self.manager.close_session(old_sess, cleanup=True)
            except Exception:
                pass

    def close_all_sessions(self) -> None:
        for kind in list(self.active_sessions.keys()):
            self.close_slot_session(kind)
        for sess in list(self.manager.list_active_sessions()):
            try:
                self.manager.close_session(sess, cleanup=True)
            except Exception:
                pass

    def open_and_read_pdf(self, kind: str, server_file: Path) -> dict[str, object]:
        self.log("OPEN", f"open_and_read_pdf kind={kind} path={server_file}")
        self.close_slot_session(kind)
        session = self.manager.open_server_file(server_file, use_lock=False)
        try:
            work_file = Path(session.work_file)
            pdf_bytes = work_file.read_bytes()
            self.log("READ_OK", f"Read {len(pdf_bytes)} bytes from work_file={work_file} (mode={session.workspace_mode})")
            return {
                "file_name": server_file.name,
                "file_size": len(pdf_bytes),
                "workspace_mode": session.workspace_mode,
                "server_path": str(server_file),
                "work_file": str(work_file),
                "pdf_base64": base64.b64encode(pdf_bytes).decode("ascii"),
            }
        finally:
            try:
                self.manager.close_session(session, cleanup=True)
            except Exception:
                pass

    def decrypt_dropped_bytes(self, kind: str, file_name: str, raw_bytes: bytes) -> dict[str, object]:
        safe_name = Path(file_name).name or "dropped.pdf"
        if not safe_name.lower().endswith(".pdf"):
            safe_name += ".pdf"
        self.log("DROP", f"decrypt_dropped_bytes kind={kind} name={safe_name} size={len(raw_bytes)}")
        tmp_dir = Path(tempfile.mkdtemp(prefix="dcl_drop_"))
        try:
            tmp_file = tmp_dir / safe_name
            tmp_file.write_bytes(raw_bytes)
            res = self.open_and_read_pdf(kind, tmp_file)
            res["file_name"] = Path(file_name).name
            return res
        finally:
            shutil.rmtree(tmp_dir, ignore_errors=True)


def pick_pdf_file_dialog(title: str) -> Optional[str]:
    try:
        import tkinter as tk
        from tkinter import filedialog

        root = tk.Tk()
        root.withdraw()
        root.attributes("-topmost", True)
        file_path = filedialog.askopenfilename(
            title=title,
            filetypes=[("PDF Files", "*.pdf"), ("All Files", "*.*")],
        )
        root.destroy()
        return file_path if file_path else None
    except Exception as exc:
        print("Dialog error:", exc, flush=True)
        return None


def get_resource_path(rel_path: str) -> Path:
    if hasattr(sys, "_MEIPASS"):
        return Path(getattr(sys, "_MEIPASS")) / rel_path
    return Path(__file__).resolve().parent / rel_path


def run_app() -> None:
    backend = AppBackend()
    html_path = get_resource_path("index.html")

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args: object) -> None:
            return

        def _send_json(self, status: int, payload: dict[str, object]) -> None:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def _send_exc(self, exc: Exception) -> None:
            tb = traceback.format_exc()
            backend.log("EXCEPTION", tb)
            with backend.lock:
                recent = list(backend.logs[-30:])
            self._send_json(
                400,
                {
                    "error": str(exc),
                    "detail": tb,
                    "log_file_path": str(backend.log_file_path),
                    "recent_logs": recent,
                },
            )

        def do_GET(self) -> None:
            parsed = urlparse(self.path)
            if parsed.path in ("/", "/index.html"):
                content = html_path.read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(content)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(content)
                return

            if parsed.path == "/api/status":
                backend.last_heartbeat = time.time()
                self._send_json(
                    200,
                    {
                        "ok": True,
                        "server": "app.py",
                        "dataclasys_available": backend.manager.is_dataclasys_available(),
                        "clsu_client_path": str(backend.manager.clsu_client_path),
                        "workspace_root": str(backend.manager.get_app_root()),
                        "log_file_path": str(backend.log_file_path),
                        "app_id": backend.manager.app_id,
                    },
                )
                return

            if parsed.path == "/api/logs":
                with backend.lock:
                    logs_copy = list(backend.logs)
                self._send_json(200, {"ok": True, "log_file_path": str(backend.log_file_path), "logs": logs_copy})
                return

            if parsed.path == "/api/progress":
                with backend.lock:
                    prog = dict(backend.latest_progress)
                self._send_json(200, prog)
                return

            self._send_json(404, {"error": f"Endpoint not found: {parsed.path}"})

        def do_POST(self) -> None:
            parsed = urlparse(self.path)
            qs = parse_qs(parsed.query)

            if parsed.path == "/api/heartbeat":
                backend.last_heartbeat = time.time()
                self._send_json(200, {"ok": True})
                return

            if parsed.path == "/api/logs/clear":
                with backend.lock:
                    backend.logs.clear()
                self._send_json(200, {"ok": True})
                return

            if parsed.path == "/api/shutdown":
                backend.shutdown_requested = True
                self._send_json(200, {"ok": True})
                threading.Thread(target=self.server.shutdown, daemon=True).start()
                return

            if parsed.path == "/api/select-file":
                kind = (qs.get("kind") or ["old"])[0]
                label = "旧版PDFファイルを選択" if kind == "old" else "新版PDFファイルを選択"
                selected = pick_pdf_file_dialog(label)
                if not selected:
                    self._send_json(200, {"cancelled": True})
                    return
                try:
                    result = backend.open_and_read_pdf(kind, Path(selected))
                    self._send_json(200, result)
                except Exception as exc:
                    self._send_exc(exc)
                return

            if parsed.path == "/api/open-path":
                length = int(self.headers.get("Content-Length", "0"))
                raw = self.rfile.read(length)
                data = json.loads(raw.decode("utf-8"))
                kind = str(data.get("kind", "old"))
                file_path = str(data.get("path", "")).strip().strip('"')
                if not file_path:
                    self._send_json(400, {"error": "ファイルパスが空です"})
                    return
                try:
                    result = backend.open_and_read_pdf(kind, Path(file_path))
                    self._send_json(200, result)
                except Exception as exc:
                    self._send_exc(exc)
                return

            if parsed.path == "/api/decrypt-dropped":
                kind = (qs.get("kind") or ["old"])[0]
                name = (qs.get("name") or ["dropped.pdf"])[0]
                length = int(self.headers.get("Content-Length", "0"))
                raw_body = self.rfile.read(length)
                if raw_body.startswith(b"{"):
                    payload = json.loads(raw_body.decode("utf-8"))
                    name = str(payload.get("name") or name)
                    raw_bytes = base64.b64decode(payload.get("data_base64", ""))
                else:
                    raw_bytes = raw_body
                try:
                    result = backend.decrypt_dropped_bytes(kind, name, raw_bytes)
                    self._send_json(200, result)
                except Exception as exc:
                    self._send_exc(exc)
                return

            self._send_json(404, {"error": f"Endpoint not found: {parsed.path}"})

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]

    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    url = f"http://127.0.0.1:{port}/"
    backend.log("SERVER", f"PDF Diff Studio starting at {url}")

    profile_dir = backend.manager.get_app_root() / "edge_app_profile"
    profile_dir.mkdir(parents=True, exist_ok=True)

    edge_paths = [
        Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / r"Microsoft\Edge\Application\msedge.exe",
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / r"Microsoft\Edge\Application\msedge.exe",
    ]
    launched = False
    for ep in edge_paths:
        if ep.exists():
            subprocess.Popen([
                str(ep),
                f"--app={url}",
                "--window-size=1500,940",
                "--no-proxy-server",
                "--disable-extensions",
                f"--user-data-dir={profile_dir}",
            ])
            launched = True
            break
    if not launched:
        webbrowser.open(url)

    try:
        server.serve_forever()
    finally:
        backend.close_all_sessions()


if __name__ == "__main__":
    run_app()
