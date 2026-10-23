"""Error types and the cancellation token shared by the worker and the media layer."""

from __future__ import annotations

import contextlib
import subprocess
import threading
from collections.abc import Callable


class JobError(Exception):
    """Base class for errors reported to the API through ``/fail``.

    ``code`` and ``retryable`` follow the contract: user/media errors are not retryable
    (``invalid_media``, ``render_failed``); a transfer with the storage that failed is
    ``transfer_failed`` (retryable when a fresh claim may fix it, see
    ``api_client.TransferError``, never charged); a claim whose payload does not match the
    contract is ``invalid_payload`` (never retryable); unexpected failures use
    ``worker_error`` and are retryable (the API decides whether to re-queue).
    """

    code: str = "worker_error"
    retryable: bool = True

    def __init__(self, message: str, *, code: str | None = None, retryable: bool | None = None):
        super().__init__(message)
        self.message = message
        if code is not None:
            self.code = code
        if retryable is not None:
            self.retryable = retryable


class MediaError(JobError):
    """The source cannot be processed (no video stream, corrupt file, unreadable...)."""

    code = "invalid_media"
    retryable = False


class RenderError(JobError):
    """A clip render failed in a way that is not worth retrying."""

    code = "render_failed"
    retryable = False


class QuotaError(JobError):
    code = "quota_exceeded"
    retryable = False


class JobRejected(Exception):
    """The API answered 409 to ``/probe`` (quota/plan/invalid media): the job is already
    marked failed server-side, so the worker just stops."""

    def __init__(self, code: str | None, detail: str | None):
        super().__init__(f"job rejected by the API ({code}): {detail}")
        self.code = code
        self.detail = detail


class JobCancelled(Exception):
    """Raised inside the job when cancellation was requested (or the lease was lost)."""

    def __init__(self, reason: str = "cancel_requested"):
        super().__init__(f"job cancelled ({reason})")
        self.reason = reason


class CancelToken:
    """Thread-safe cancellation flag that also kills registered child processes.

    The heartbeat thread calls :meth:`cancel` when the API reports ``cancelRequested``;
    long-running steps call :meth:`raise_if_cancelled` at safe points and every ffmpeg
    subprocess is registered so it is killed immediately.
    """

    def __init__(self) -> None:
        self._event = threading.Event()
        self._lock = threading.Lock()
        self._processes: set[subprocess.Popen[bytes]] = set()
        self._callbacks: list[Callable[[], None]] = []
        self.reason: str | None = None

    @property
    def cancelled(self) -> bool:
        return self._event.is_set()

    def cancel(self, reason: str = "cancel_requested") -> None:
        with self._lock:
            if self._event.is_set():
                return
            self.reason = reason
            self._event.set()
            processes = list(self._processes)
            callbacks = list(self._callbacks)
        for proc in processes:
            _kill(proc)
        for callback in callbacks:
            with contextlib.suppress(Exception):
                callback()

    def raise_if_cancelled(self) -> None:
        if self._event.is_set():
            raise JobCancelled(self.reason or "cancel_requested")

    def register(self, proc: subprocess.Popen[bytes]) -> None:
        with self._lock:
            if self._event.is_set():
                _kill(proc)
                return
            self._processes.add(proc)

    def unregister(self, proc: subprocess.Popen[bytes]) -> None:
        with self._lock:
            self._processes.discard(proc)

    def on_cancel(self, callback: Callable[[], None]) -> None:
        with self._lock:
            self._callbacks.append(callback)


def _kill(proc: subprocess.Popen[bytes]) -> None:
    try:
        if proc.poll() is None:
            proc.kill()
    except Exception:
        pass
