"""The only place that spawns ``ffmpeg`` / ``ffprobe``.

Rules (see ``docs/PROCESSING_PIPELINE.md``): argument lists only (never ``shell=True``),
``-nostdin -hide_banner -y`` on every ffmpeg call, a timeout proportional to the input
duration, and the process is registered with the job's :class:`CancelToken` so it is
killed on cancellation.
"""

from __future__ import annotations

import contextlib
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path
from typing import IO, Any

from app.logging_setup import get_logger
from app.worker.errors import CancelToken, JobCancelled

log = get_logger("ffmpeg")

_CREATION_FLAGS = (
    getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform.startswith("win") else 0
)
STDERR_TAIL = 4000


class FfmpegError(Exception):
    def __init__(
        self,
        message: str,
        *,
        args_list: list[str] | None = None,
        stderr: str = "",
        returncode: int | None = None,
    ):
        super().__init__(message)
        self.args_list = args_list or []
        self.stderr = stderr
        self.returncode = returncode


class FfmpegTimeout(FfmpegError):
    pass


class FfmpegNotFound(FfmpegError):
    pass


@dataclass(slots=True)
class FfmpegResult:
    returncode: int
    stdout: bytes
    stderr: str
    elapsed: float


def timeout_for(
    duration_seconds: float | None,
    *,
    factor: float = 1.0,
    minimum: float = 60.0,
    extra: float = 30.0,
) -> float:
    """Timeout proportional to the media duration (``duration * factor + extra``)."""
    duration = float(duration_seconds or 0.0)
    return max(minimum, duration * factor + extra)


def fmt_num(value: float | int) -> str:
    """Format a validated number for an ffmpeg argument / filter (never user text)."""
    if isinstance(value, bool):
        raise ValueError("boolean is not a valid ffmpeg number")
    if isinstance(value, int):
        return str(value)
    number = float(value)
    if number != number or number in (float("inf"), float("-inf")):
        raise ValueError("non-finite number")
    text = f"{number:.4f}".rstrip("0").rstrip(".")
    return text if text not in ("", "-0") else "0"


class FfmpegTools:
    """Thin wrapper around the configured ffmpeg/ffprobe binaries."""

    def __init__(self, ffmpeg_path: str, ffprobe_path: str, threads: int = 0) -> None:
        self.ffmpeg_path = ffmpeg_path
        self.ffprobe_path = ffprobe_path
        self.threads = max(0, int(threads))
        self._versions: dict[str, str | None] = {}

    @classmethod
    def from_settings(cls, settings: Any) -> FfmpegTools:
        return cls(settings.ffmpeg_path, settings.ffprobe_path, settings.ffmpeg_threads)

    # ------------------------------------------------------------------ discovery ----
    def _version_of(self, binary: str) -> str | None:
        if binary in self._versions:
            return self._versions[binary]
        version: str | None = None
        try:
            proc = subprocess.run(
                [binary, "-version"],
                capture_output=True,
                text=True,
                timeout=20,
                check=False,
                creationflags=_CREATION_FLAGS,
            )
            if proc.returncode == 0 and proc.stdout:
                first = proc.stdout.splitlines()[0]
                parts = first.split()
                version = parts[2] if len(parts) >= 3 else first
        except OSError, subprocess.SubprocessError:
            version = None
        self._versions[binary] = version
        return version

    def ffmpeg_version(self) -> str | None:
        return self._version_of(self.ffmpeg_path)

    def ffprobe_version(self) -> str | None:
        return self._version_of(self.ffprobe_path)

    def available(self) -> bool:
        return self.ffmpeg_version() is not None and self.ffprobe_version() is not None

    # ------------------------------------------------------------------ execution ----
    def base_args(self) -> list[str]:
        return [self.ffmpeg_path, "-nostdin", "-hide_banner", "-y", "-loglevel", "error"]

    def encode_thread_args(self) -> list[str]:
        return ["-threads", str(self.threads)] if self.threads > 0 else []

    def run(
        self,
        args: list[str],
        *,
        timeout: float,
        cancel: CancelToken | None = None,
        capture_stdout: bool = False,
    ) -> FfmpegResult:
        """Run ``ffmpeg <base args> <args>`` and wait; raises on failure/timeout/cancel."""
        full = self.base_args() + [str(a) for a in args]
        started = time.monotonic()
        try:
            proc = subprocess.Popen(
                full,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE if capture_stdout else subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                creationflags=_CREATION_FLAGS,
            )
        except FileNotFoundError as exc:
            raise FfmpegNotFound(f"ffmpeg not found at {self.ffmpeg_path}", args_list=full) from exc
        if cancel is not None:
            cancel.register(proc)
        try:
            try:
                out, err = proc.communicate(timeout=timeout)
            except subprocess.TimeoutExpired:
                proc.kill()
                out, err = proc.communicate()
                raise FfmpegTimeout(
                    f"ffmpeg timed out after {timeout:.0f}s",
                    args_list=full,
                    stderr=_tail(err),
                    returncode=proc.returncode,
                ) from None
        finally:
            if cancel is not None:
                cancel.unregister(proc)
        elapsed = time.monotonic() - started
        if cancel is not None and cancel.cancelled:
            raise JobCancelled(cancel.reason or "cancel_requested")
        stderr = _tail(err)
        if proc.returncode != 0:
            raise FfmpegError(
                f"ffmpeg failed (exit {proc.returncode}): {_last_line(stderr)}",
                args_list=full,
                stderr=stderr,
                returncode=proc.returncode,
            )
        return FfmpegResult(proc.returncode, out or b"", stderr, elapsed)

    def stream(
        self, args: list[str], *, timeout: float, cancel: CancelToken | None = None
    ) -> PipeProcess:
        """Start ffmpeg writing raw data to stdout (use as a context manager)."""
        full = self.base_args() + [str(a) for a in args]
        return PipeProcess(full, timeout=timeout, cancel=cancel)

    # ------------------------------------------------------------------ ffprobe ----
    def probe_json(self, path: str | os.PathLike[str], *, timeout: float = 120.0) -> dict[str, Any]:
        args = [
            self.ffprobe_path,
            "-v",
            "error",
            "-print_format",
            "json",
            "-show_format",
            "-show_streams",
            os.fspath(path),
        ]
        try:
            proc = subprocess.run(
                args,
                capture_output=True,
                timeout=timeout,
                check=False,
                creationflags=_CREATION_FLAGS,
                stdin=subprocess.DEVNULL,
            )
        except FileNotFoundError as exc:
            raise FfmpegNotFound(
                f"ffprobe not found at {self.ffprobe_path}", args_list=args
            ) from exc
        except subprocess.TimeoutExpired as exc:
            raise FfmpegTimeout("ffprobe timed out", args_list=args) from exc
        stderr = _tail(proc.stderr)
        if proc.returncode != 0:
            raise FfmpegError(
                f"ffprobe failed (exit {proc.returncode}): {stderr.strip() or 'no output'}",
                args_list=args,
                stderr=stderr,
                returncode=proc.returncode,
            )
        try:
            data = json.loads(proc.stdout.decode("utf-8", errors="replace") or "{}")
        except json.JSONDecodeError as exc:
            raise FfmpegError(
                "ffprobe returned invalid JSON", args_list=args, stderr=stderr
            ) from exc
        if not isinstance(data, dict):
            raise FfmpegError("ffprobe returned an unexpected document", args_list=args)
        return data


class PipeProcess:
    """ffmpeg process whose stdout is read incrementally (PCM / rawvideo pipes).

    stderr is captured to a temporary file so a chatty process never blocks. The timeout
    is enforced by a watchdog timer that kills the process when it expires: a stalled
    ffmpeg (stuck demuxer/decoder, blocked I/O) cannot keep a blocking pipe read waiting
    forever while the heartbeat extends the lease. On exit the process is waited for (or
    killed when the consumer stopped early); a non-zero exit raises :class:`FfmpegError`
    unless the job was cancelled, in which case :class:`JobCancelled` is raised, or the
    watchdog fired, in which case :class:`FfmpegTimeout` is raised.
    """

    def __init__(self, full_args: list[str], *, timeout: float, cancel: CancelToken | None) -> None:
        self.args = full_args
        self.timeout = timeout
        self.cancel = cancel
        self.deadline = time.monotonic() + timeout
        self._proc: subprocess.Popen[bytes] | None = None
        self._stderr_file: IO[bytes] | None = None
        self._watchdog: threading.Timer | None = None
        self.timed_out = False
        self.finished_cleanly = False

    def __enter__(self) -> PipeProcess:
        self._stderr_file = tempfile.TemporaryFile()
        try:
            self._proc = subprocess.Popen(
                self.args,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=self._stderr_file,
                creationflags=_CREATION_FLAGS,
                bufsize=0,
            )
        except FileNotFoundError as exc:
            self._stderr_file.close()
            raise FfmpegNotFound(
                f"ffmpeg not found at {self.args[0]}", args_list=self.args
            ) from exc
        if self.cancel is not None:
            self.cancel.register(self._proc)
        self._watchdog = threading.Timer(max(0.0, self.deadline - time.monotonic()), self._expire)
        self._watchdog.daemon = True
        self._watchdog.start()
        return self

    def _expire(self) -> None:
        """Watchdog: killing the process makes a blocked ``read`` return EOF."""
        self.timed_out = True
        self.kill()

    @property
    def stdout(self) -> IO[bytes]:
        assert self._proc is not None and self._proc.stdout is not None
        return self._proc.stdout

    def _timeout_error(self) -> FfmpegTimeout:
        return FfmpegTimeout(
            f"ffmpeg pipe timed out after {self.timeout:.0f}s", args_list=self.args
        )

    def _check_timeout(self) -> None:
        if self.timed_out or time.monotonic() > self.deadline:
            self._expire()
            raise self._timeout_error()

    def read_exact(self, size: int) -> bytes:
        """Read exactly ``size`` bytes (fewer only at EOF); enforces the timeout."""
        buf = bytearray()
        out = self.stdout
        while len(buf) < size:
            self._check_timeout()
            chunk = out.read(size - len(buf))
            if not chunk:
                self._check_timeout()  # EOF because the watchdog killed the process
                break
            buf.extend(chunk)
        return bytes(buf)

    def iter_chunks(self, size: int) -> Iterator[bytes]:
        while True:
            chunk = self.read_exact(size)
            if not chunk:
                return
            yield chunk
            if len(chunk) < size:
                return

    def kill(self) -> None:
        proc = self._proc
        if proc is not None and proc.poll() is None:
            with contextlib.suppress(OSError):
                proc.kill()

    def __exit__(self, exc_type: object, exc: object, tb: object) -> None:
        proc = self._proc
        assert proc is not None
        if self._watchdog is not None:
            self._watchdog.cancel()
        try:
            if proc.stdout is not None:
                with contextlib.suppress(OSError):
                    proc.stdout.close()
            if exc_type is not None:
                self.kill()
            try:
                proc.wait(timeout=30)
            except subprocess.TimeoutExpired:
                self.kill()
                proc.wait(timeout=10)
        finally:
            if self.cancel is not None:
                self.cancel.unregister(proc)
        stderr = ""
        if self._stderr_file is not None:
            try:
                self._stderr_file.seek(0)
                stderr = _tail(self._stderr_file.read())
            finally:
                self._stderr_file.close()
        if exc_type is not None:
            return  # propagate the consumer's exception
        if self.cancel is not None and self.cancel.cancelled:
            raise JobCancelled(self.cancel.reason or "cancel_requested")
        if self.timed_out and not self.finished_cleanly:
            raise self._timeout_error()
        # Consumers may stop reading early (e.g. duration reached); treat a broken pipe
        # exit as success only when the consumer finished cleanly.
        if proc.returncode not in (0, None) and not self.finished_cleanly:
            raise FfmpegError(
                f"ffmpeg pipe failed (exit {proc.returncode}): {_last_line(stderr)}",
                args_list=self.args,
                stderr=stderr,
                returncode=proc.returncode,
            )


def _last_line(stderr: str) -> str:
    text = stderr.strip()
    return text.splitlines()[-1] if text else "no output"


def _tail(data: bytes | str | None) -> str:
    if not data:
        return ""
    text = data.decode("utf-8", errors="replace") if isinstance(data, bytes) else data
    return text[-STDERR_TAIL:]


def safe_relative_name(name: str, default: str = "file") -> str:
    """Keep only a conservative subset of characters for file names we generate."""
    keep = "".join(ch for ch in name if ch.isalnum() or ch in "-_.")
    keep = keep.strip("._")
    return keep[:80] or default


def ensure_dir(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    return path
