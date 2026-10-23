"""Claim loop (one job at a time; ``MAX_PARALLEL_JOBS=1`` is the only supported value in
the MVP because the work is CPU-bound)."""

from __future__ import annotations

import threading
import time
from collections.abc import Callable, Sequence
from typing import Any

from app.api_client import ApiError, InternalApiClient, InvalidJobPayload
from app.config import Settings
from app.logging_setup import get_logger
from app.worker.runner import JobRunner

log = get_logger("worker")


class WorkerLoop:
    def __init__(
        self,
        settings: Settings,
        api: InternalApiClient,
        runner: JobRunner,
        *,
        kinds: Sequence[str] | None = None,
        poll_interval: float | None = None,
        sleep: Callable[[float], None] | None = None,
    ) -> None:
        self.settings = settings
        self.api = api
        self.runner = runner
        self.kinds = list(kinds) if kinds is not None else settings.kinds
        self.poll_interval = (
            poll_interval if poll_interval is not None else settings.worker_poll_interval_seconds
        )
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._sleep = sleep
        self.jobs_run = 0
        self.jobs_rejected = 0  # claims whose payload did not match the contract
        self.last_error: str | None = None
        self.started_at: float | None = None

    # ------------------------------------------------------------------ control ----
    @property
    def running(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    def start(self) -> None:
        if self.running:
            return
        self._stop.clear()
        self._thread = threading.Thread(
            target=self.run_forever, name="clipforge-worker", daemon=True
        )
        self.started_at = time.time()
        self._thread.start()
        log.info(
            "worker.started",
            worker_id=self.settings.worker_id,
            kinds=self.kinds,
            api=self.settings.clipforge_api_base_url,
        )

    def stop(self, timeout: float = 15.0) -> None:
        self._stop.set()
        self.runner.abort_current("shutdown")
        thread = self._thread
        if thread is not None and thread.is_alive():
            thread.join(timeout=timeout)
        log.info("worker.stopped")

    def status(self) -> dict[str, Any]:
        return {
            "enabled": True,
            "running": self.running,
            "currentJobId": self.runner.current_job_id,
            "workerId": self.settings.worker_id,
            "kinds": self.kinds,
            "jobsRun": self.jobs_run,
            "jobsRejected": self.jobs_rejected,
            "lastError": self.last_error,
        }

    # ------------------------------------------------------------------ loop ----
    def _wait(self, seconds: float) -> None:
        if self._sleep is not None:
            self._sleep(seconds)
        else:
            self._stop.wait(seconds)

    def run_once(self) -> bool:
        """Claim and run one job. Returns ``False`` when the queue was empty.

        A claimed payload that does not match the contract was already failed on the API by
        the claim (``/fail invalid_payload``): it counts as a finished job, so the loop goes
        on to the next claim without backing off (the next job may well be fine).
        """
        try:
            payload = self.api.claim(
                self.kinds, {"maxParallelJobs": self.settings.max_parallel_jobs}
            )
        except InvalidJobPayload as exc:
            self.jobs_rejected += 1
            log.error(
                "job.invalid_payload",
                job_id=exc.job_id,
                reported=exc.reported,
                error=exc.summary[:300],
            )
            return True
        if payload is None:
            return False
        log.info("job.claimed", job_id=payload.job_id, kind=payload.kind, attempt=payload.attempt)
        self.runner.run(payload)
        self.jobs_run += 1
        return True

    def run_forever(self) -> None:
        # Job directories a crashed worker left behind (the source download and renders of
        # a long stream are several GB) would otherwise fill WORK_DIR over time.
        try:
            self.runner.sweep_stale_work_dirs()
        except Exception:
            log.exception("workdir.sweep_failed")
        backoff = 0
        while not self._stop.is_set():
            try:
                did_work = self.run_once()
                backoff = 0
                self.last_error = None
            except ApiError as exc:
                backoff = min(backoff + 1, 5)
                self.last_error = str(exc)[:300]
                delay = min(60.0, self.poll_interval * (2**backoff))
                log.warning("worker.api_unavailable", error=self.last_error, retry_in=delay)
                self._wait(delay)
                continue
            except Exception as exc:
                backoff = min(backoff + 1, 5)
                self.last_error = f"{type(exc).__name__}: {exc}"[:300]
                log.exception("worker.loop_error")
                self._wait(min(60.0, self.poll_interval * (2**backoff)))
                continue
            if not did_work:
                self._wait(self.poll_interval)
