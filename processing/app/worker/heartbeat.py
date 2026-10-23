"""Heartbeat thread: extends the lease every ``leaseSeconds/3`` and watches for cancel.

The thread keeps beating after a cancel is observed: the job thread still has to unwind
(an upload in flight, an LLM batch, a model being loaded...) and then post the final
``/status cancelled``, which can take longer than what is left of the lease. Without the
heartbeats the API's reaper would re-queue the cancelled job (a re-queue keeps
``cancelRequested`` but clears the worker id, so the worker's ``/status cancelled`` would
be a 409 and the job would be re-claimed, re-downloaded and re-probed). The thread stops
when the runner calls :meth:`HeartbeatThread.stop` after the final report, or when the API
answers 409 (lease owned by another worker, or the job already finished).
"""

from __future__ import annotations

import contextlib
import threading
from collections.abc import Callable

from app.api_client import ApiConflictError, ApiError, InternalApiClient
from app.logging_setup import get_logger
from app.worker.errors import CancelToken

log = get_logger("heartbeat")

# Published through /heartbeat while a cancelled job unwinds (the API keeps the previous
# status message when a cancel is requested).
CANCELLING_MESSAGE = "Cancelando…"


class ProgressState:
    """Thread-safe (progress, message) shared between the job and the heartbeat."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.progress = 0
        self.message = ""

    def set(self, progress: int | None = None, message: str | None = None) -> None:
        with self._lock:
            if progress is not None:
                self.progress = max(0, min(100, int(progress)))
            if message is not None:
                self.message = message

    def get(self) -> tuple[int, str]:
        with self._lock:
            return self.progress, self.message


class HeartbeatThread(threading.Thread):
    def __init__(
        self,
        api: InternalApiClient,
        job_id: str,
        lease_seconds: int,
        state: ProgressState,
        cancel: CancelToken,
        *,
        on_beat: Callable[[], None] | None = None,
    ) -> None:
        super().__init__(name=f"heartbeat-{job_id}", daemon=True)
        self.api = api
        self.job_id = job_id
        self.interval = max(1.0, float(lease_seconds) / 3.0)
        self.state = state
        self.cancel = cancel
        # local liveness hook (the runner refreshes the work directory's mtime so a worker
        # starting on the same WORK_DIR never sweeps the directory of a running job)
        self.on_beat = on_beat
        self._stop = threading.Event()
        self.beats = 0
        self.failures = 0

    def run(self) -> None:
        while not self._stop.wait(self.interval):
            if self.on_beat is not None:
                with contextlib.suppress(Exception):
                    self.on_beat()
            progress, message = self.state.get()
            try:
                response = self.api.heartbeat(self.job_id, progress, message)
            except ApiConflictError as exc:
                if self.cancel.cancelled:
                    # The job thread already posted its final status (or the lease is gone
                    # while the job unwinds): nothing left to extend.
                    log.info("heartbeat.finished", job_id=self.job_id, detail=exc.detail)
                else:
                    log.warning("heartbeat.lease_lost", job_id=self.job_id, detail=exc.detail)
                    self.cancel.cancel("lease_lost")
                break
            except ApiError as exc:
                self.failures += 1
                log.warning("heartbeat.failed", job_id=self.job_id, error=str(exc)[:200])
                continue
            self.beats += 1
            if response.cancel_requested:
                if not self.cancel.cancelled:
                    log.info("heartbeat.cancel_requested", job_id=self.job_id)
                self.state.set(message=CANCELLING_MESSAGE)
                self.cancel.cancel("cancel_requested")
            # Keep extending the lease until stop(): the final /status is the job thread's,
            # and the steps it still has to unwind may outlast what is left of the lease.

    def stop(self, timeout: float = 5.0) -> None:
        self._stop.set()
        if self.is_alive():
            self.join(timeout=timeout)
