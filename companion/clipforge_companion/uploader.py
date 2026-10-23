"""Persistent, in-order upload queue for the segments of one live session.

State lives in ``<work-dir>/<sessionId>/queue.json`` and is rewritten atomically after every
change, so a crash or a restart never loses a segment: the next run resumes from the first
sequence that was not acknowledged (the API is idempotent per ``(session, sequence)`` and
answers ``200 {duplicate: true}`` for a sequence it already has).

Retry policy: network errors (TLS errors included), 5xx, 408 and 429 are retried forever with
exponential backoff (2 s doubling up to 60 s, or the server's ``Retry-After``). 401/403 (token
revoked) and 409 (session terminal / quota exceeded) stop the queue and are reported as
``last_error``. Other 4xx answers (413 too big, 422 invalid media) skip that segment.

Local file problems: a segment whose file is gone (or shrank while it was sent) is skipped; a
locked or unreadable file is retried a few times first and then skipped, but kept on disk. Any
other unexpected error is retried like a network error: a segment file is only deleted after
the API accepted it or refused it for good.

:class:`EndingRegistry` remembers, across runs, the sessions this device ended that may still
be ``ending`` on the server.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import math
import os
import time
from collections.abc import Awaitable, Callable, Iterable
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from clipforge_companion.api import (
    ApiError,
    AuthError,
    CompanionApi,
    RateLimiter,
    RejectedError,
    RetryableError,
    SegmentFileError,
    SessionClosed,
)
from clipforge_companion.segmenter import Segment

log = logging.getLogger(__name__)

QUEUE_FILE = "queue.json"
STATE_VERSION = 1
# A session ended this long ago is no longer waited for (it cannot be "ending" for a day).
ENDING_MAX_AGE_SECONDS = 24 * 3600.0


def _now_iso() -> str:
    return datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ")


def _write_json_atomic(target: Path, data: Any, *, attempts: int = 10) -> None:
    """Writes ``data`` to a temporary file and moves it over ``target``."""
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    for attempt in range(attempts):
        try:
            os.replace(tmp, target)
            return
        except PermissionError:  # Windows: file briefly locked (antivirus, indexer)
            if attempt == attempts - 1:
                raise
            time.sleep(0.05 * (attempt + 1))


def _same_path(a: str | os.PathLike[str] | None, b: str | os.PathLike[str] | None) -> bool:
    if not a or not b:
        return False
    return os.path.normcase(os.path.abspath(a)) == os.path.normcase(os.path.abspath(b))


@dataclass(slots=True)
class QueueItem:
    sequence: int
    file: str  # relative to the session directory, POSIX separators
    start: float
    duration: float
    final: bool = False
    attempts: int = 0

    @classmethod
    def from_json(cls, data: dict[str, Any]) -> QueueItem:
        return cls(
            sequence=int(data["sequence"]),
            file=str(data["file"]),
            start=float(data["start"]),
            duration=float(data["duration"]),
            final=bool(data.get("final", False)),
            attempts=int(data.get("attempts", 0)),
        )


class SessionStore:
    """Persistent state of one session (``queue.json``)."""

    def __init__(self, directory: Path, data: dict[str, Any]) -> None:
        self.directory = directory
        self.data = data
        self.items: list[QueueItem] = [QueueItem.from_json(i) for i in data.get("items", [])]

    # -- creation / discovery ------------------------------------------------------------------

    @classmethod
    def create(
        cls,
        work_dir: Path,
        session_id: str,
        *,
        mode: str,
        api_url: str,
        device_id: str | None,
        segment_seconds: float,
        title: str | None = None,
    ) -> SessionStore:
        directory = work_dir / session_id
        directory.mkdir(parents=True, exist_ok=True)
        store = cls(
            directory,
            {
                "version": STATE_VERSION,
                "sessionId": session_id,
                "apiUrl": api_url,
                "deviceId": device_id,
                "mode": mode,
                "title": title,
                "segmentSeconds": segment_seconds,
                "createdAt": _now_iso(),
                "ended": False,
                "endReason": None,
                "source": None,
                "runs": 0,
                "lastSequence": -1,
                "lastEnd": 0.0,
                "lastAckedSequence": -1,
                "uploaded": 0,
                "skipped": 0,
                "enqueuedTotal": 0,
                "finalQueued": False,
                "recordingMark": None,
                "items": [],
            },
        )
        store.save()
        return store

    @classmethod
    def load(cls, directory: Path) -> SessionStore:
        data = json.loads((directory / QUEUE_FILE).read_text(encoding="utf-8"))
        if not isinstance(data, dict) or not data.get("sessionId"):
            raise ValueError(f"invalid {QUEUE_FILE} in {directory}")
        return cls(directory, data)

    @classmethod
    def find_unfinished(cls, work_dir: Path) -> list[SessionStore]:
        """Sessions of earlier runs that were not ended (oldest first)."""
        stores: list[SessionStore] = []
        if not work_dir.is_dir():
            return stores
        for queue_file in work_dir.glob(f"*/{QUEUE_FILE}"):
            try:
                store = cls.load(queue_file.parent)
            except (OSError, ValueError) as exc:
                log.warning("estado de sessão ilegível ignorado (%s): %s", queue_file, exc)
                continue
            if not store.ended:
                stores.append(store)
        stores.sort(key=lambda s: str(s.data.get("createdAt") or ""))
        return stores

    # -- properties ----------------------------------------------------------------------------

    @property
    def session_id(self) -> str:
        return str(self.data["sessionId"])

    @property
    def ended(self) -> bool:
        return bool(self.data.get("ended"))

    @property
    def segment_seconds(self) -> float | None:
        """Target segment length the session was created with (None when missing/invalid).
        A resumed session must be cut with it again: sequences and skips depend on it."""
        value = self.data.get("segmentSeconds")
        if isinstance(value, bool) or not isinstance(value, int | float):
            return None
        return float(value) if math.isfinite(value) and value > 0 else None

    @property
    def last_sequence(self) -> int:
        """Highest sequence ever enqueued (-1 when none)."""
        return int(self.data.get("lastSequence", -1))

    @property
    def last_end(self) -> float:
        return float(self.data.get("lastEnd", 0.0))

    @property
    def last_acked_sequence(self) -> int:
        return int(self.data.get("lastAckedSequence", -1))

    @property
    def uploaded(self) -> int:
        return int(self.data.get("uploaded", 0))

    @property
    def enqueued_total(self) -> int:
        return int(self.data.get("enqueuedTotal", self.uploaded + len(self.items)))

    @property
    def final_queued(self) -> bool:
        return bool(self.data.get("finalQueued"))

    @property
    def recording_mark(self) -> dict[str, Any] | None:
        """Where the capture of the session's last recording file stopped, written when its
        final segment is queued (see ``app.RecordingMark``); None when not set."""
        mark = self.data.get("recordingMark")
        return mark if isinstance(mark, dict) else None

    @property
    def source(self) -> dict[str, Any] | None:
        source = self.data.get("source")
        return source if isinstance(source, dict) else None

    def matches_source(self, path: str | os.PathLike[str]) -> bool:
        source = self.source
        return source is not None and _same_path(source.get("path"), path)

    # -- mutations -----------------------------------------------------------------------------

    def set_source(self, path: Path, *, sequence_offset: int, time_offset: float) -> None:
        self.data["source"] = {
            "path": str(path),
            "sequenceOffset": sequence_offset,
            "timeOffset": time_offset,
        }
        self.save()

    def set_recording_mark(self, mark: dict[str, Any] | None) -> None:
        self.data["recordingMark"] = mark
        self.save()

    def new_run_dir(self) -> Path:
        runs = int(self.data.get("runs", 0)) + 1
        self.data["runs"] = runs
        self.save()
        run_dir = self.directory / f"run-{runs:02d}"
        run_dir.mkdir(parents=True, exist_ok=True)
        return run_dir

    def add(self, segment: Segment, *, final: bool) -> QueueItem:
        try:
            relative = segment.path.relative_to(self.directory).as_posix()
        except ValueError:
            relative = str(segment.path)
        item = QueueItem(
            sequence=segment.sequence,
            file=relative,
            start=segment.start,
            duration=round(segment.duration, 6),
            final=final,
        )
        self.data["enqueuedTotal"] = self.enqueued_total + 1
        self.items.append(item)
        self.data["lastSequence"] = max(self.last_sequence, segment.sequence)
        self.data["lastEnd"] = max(self.last_end, segment.end)
        if final:
            self.data["finalQueued"] = True
        self.save()
        return item

    def head(self) -> QueueItem | None:
        return self.items[0] if self.items else None

    def path_of(self, item: QueueItem) -> Path:
        path = Path(item.file)
        return path if path.is_absolute() else self.directory / path

    def ack_head(self) -> QueueItem:
        item = self.items.pop(0)
        self.data["lastAckedSequence"] = max(self.last_acked_sequence, item.sequence)
        self.data["uploaded"] = self.uploaded + 1
        self.save()
        return item

    def drop_head(self) -> QueueItem:
        item = self.items.pop(0)
        self.data["skipped"] = int(self.data.get("skipped", 0)) + 1
        self.save()
        return item

    def mark_last_final(self) -> bool:
        """Flags the last pending item as final (used when a session is ended later)."""
        if not self.items:
            return False
        self.items[-1].final = True
        self.data["finalQueued"] = True
        self.save()
        return True

    def mark_ended(self, reason: str) -> None:
        self.data["ended"] = True
        self.data["endReason"] = reason
        self.data["endedAt"] = _now_iso()
        self.save()

    def save(self) -> None:
        self.data["items"] = [asdict(item) for item in self.items]
        _write_json_atomic(self.directory / QUEUE_FILE, self.data)


class EndingRegistry:
    """Sessions this device ended that may still be ``ending`` on the server (their last
    segments being analysed). Until they are terminal the API refuses a new session for the
    device with 409 plan_limit. Kept in ``<work-dir>/ending-<device>.json`` so that a companion
    started right after an earlier run ended its session knows why, and waits instead of
    failing. Only the process holding the device's instance lock uses the file; entries of
    other API URLs are kept untouched. Problems with the file are logged, never raised."""

    def __init__(
        self, path: Path, api_url: str, *, max_age: float = ENDING_MAX_AGE_SECONDS
    ) -> None:
        self.path = path
        self.api_url = api_url
        self.max_age = max_age
        self._ended_at: dict[str, float] = {}  # session id -> epoch seconds (this API)
        self._others: list[dict[str, Any]] = []
        self._loaded = False

    def load(self) -> set[str]:
        """Session ids of this API ended less than ``max_age`` ago."""
        self._ended_at, self._others = {}, []
        self._loaded = True
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return set()
        except (OSError, ValueError) as exc:
            log.warning("ignoring unreadable %s: %s", self.path, exc)
            return set()
        entries = data.get("sessions") if isinstance(data, dict) else None
        now = time.time()
        for entry in entries if isinstance(entries, list) else []:
            if not isinstance(entry, dict) or not entry.get("sessionId"):
                continue
            if entry.get("apiUrl") != self.api_url:
                self._others.append(entry)
                continue
            ended_at = entry.get("endedAt")
            if not isinstance(ended_at, int | float) or now - float(ended_at) > self.max_age:
                continue
            self._ended_at[str(entry["sessionId"])] = float(ended_at)
        return set(self._ended_at)

    def save(self, session_ids: Iterable[str]) -> None:
        """Replaces this API's entries with ``session_ids``."""
        if not self._loaded:
            self.load()  # keeps the other APIs' entries and the known end times
        now = time.time()
        self._ended_at = {sid: self._ended_at.get(sid, now) for sid in session_ids}
        entries = [
            {"sessionId": sid, "apiUrl": self.api_url, "endedAt": ended_at}
            for sid, ended_at in sorted(self._ended_at.items())
        ]
        try:
            if entries or self._others:
                # Best effort: never hold the event loop for long over this file.
                _write_json_atomic(
                    self.path,
                    {"version": STATE_VERSION, "sessions": entries + self._others},
                    attempts=3,
                )
            else:
                self.path.unlink(missing_ok=True)
        except OSError as exc:
            log.warning("cannot write %s: %s", self.path, exc)


StatusCallback = Callable[[str, dict[str, Any]], None]


class Uploader:
    """Uploads the queued segments of one session in sequence order."""

    def __init__(
        self,
        api: CompanionApi,
        store: SessionStore,
        *,
        keep_segments: bool = False,
        max_upload_mbps: float | None = None,
        backoff_initial: float = 2.0,
        backoff_max: float = 60.0,
        local_retries: int = 5,
        sleep: Callable[[float], Awaitable[Any]] | None = None,
        on_status: StatusCallback | None = None,
    ) -> None:
        self.api = api
        self.store = store
        self.keep_segments = keep_segments
        self.limiter = RateLimiter.from_mbps(max_upload_mbps) if max_upload_mbps else None
        self.backoff_initial = backoff_initial
        self.backoff_max = backoff_max
        self.local_retries = local_retries
        self._sleep = sleep
        self._on_status = on_status
        self._wakeup = asyncio.Event()
        self._closed = False
        self.uploading: QueueItem | None = None
        self.last_error: str | None = None
        self.fatal: ApiError | None = None
        self.online = True
        self.attempts = 0
        self.delays: list[float] = []

    @property
    def session_id(self) -> str:
        return self.store.session_id

    @property
    def queue_length(self) -> int:
        return len(self.store.items)

    @property
    def is_uploading(self) -> bool:
        return self.uploading is not None

    @property
    def drained(self) -> bool:
        return not self.store.items

    def enqueue(self, segment: Segment, *, final: bool) -> QueueItem:
        item = self.store.add(segment, final=final)
        self._wakeup.set()
        return item

    def close(self) -> None:
        """No more segments will be enqueued: :meth:`run` returns once the queue is empty."""
        self._closed = True
        self._wakeup.set()

    def wake(self) -> None:
        self._wakeup.set()

    async def run(self) -> None:
        """Processes the queue until closed and drained, or until a fatal error."""
        backoff = self.backoff_initial
        local_failures = 0  # file locked / unreadable, for the head item
        failing_sequence: int | None = None
        while True:
            item = self.store.head()
            if item is None:
                if self._closed:
                    return
                self._wakeup.clear()
                await self._wakeup.wait()
                continue
            if item.sequence != failing_sequence:
                failing_sequence = item.sequence
                local_failures = 0
            path = self.store.path_of(item)
            if not path.is_file():
                self.last_error = f"arquivo do segmento {item.sequence} não encontrado"
                log.warning("segment %s missing (%s); skipped", item.sequence, path)
                self.store.drop_head()
                self._emit("skipped", item=item, reason=self.last_error)
                continue
            self.uploading = item
            item.attempts += 1
            self.attempts += 1
            self._emit("uploading", item=item)
            size: int | None = None
            with contextlib.suppress(OSError):
                size = path.stat().st_size
            started = time.monotonic()
            try:
                result = await self.api.upload_segment(
                    self.session_id,
                    sequence=item.sequence,
                    start_offset=item.start,
                    duration=item.duration,
                    path=path,
                    final=item.final,
                    limiter=self.limiter,
                )
            except RetryableError as exc:
                self.uploading = None
                self.online = False
                delay = min(max(backoff, exc.retry_after or 0.0), max(self.backoff_max, 300.0))
                self.delays.append(delay)
                self.last_error = f"falha ao enviar o segmento {item.sequence}: {exc.message}"
                self._emit("retry", item=item, delay=delay, error=exc.message)
                await self._pause(delay)
                backoff = min(backoff * 2, self.backoff_max)
                continue
            except (AuthError, SessionClosed) as exc:
                self.uploading = None
                self.fatal = exc
                self.last_error = exc.message
                self._emit("fatal", item=item, error=exc.message)
                return
            except RejectedError as exc:  # the API refused this segment for good (413, 422...)
                self.uploading = None
                self.last_error = f"segmento {item.sequence} recusado: {exc.message}"
                self.store.drop_head()
                self._delete(path)
                self._emit("rejected", item=item, error=exc.message, status=exc.status)
                continue
            except SegmentFileError as exc:
                self.uploading = None
                if exc.transient and local_failures < self.local_retries:
                    local_failures += 1
                    delay = backoff
                    self.delays.append(delay)
                    self.last_error = f"falha ao ler o segmento {item.sequence}: {exc.message}"
                    self._emit("retry", item=item, delay=delay, error=exc.message)
                    await self._pause(delay)
                    backoff = min(backoff * 2, self.backoff_max)
                    continue
                # Gone, shrunk, or still unreadable after the retries: skip it. The file is
                # left where it is (nothing to delete, or it may still be useful to someone).
                self.last_error = f"segmento {item.sequence} ignorado: {exc.message}"
                log.warning("segment %s skipped: %s", item.sequence, exc.message)
                self.store.drop_head()
                self._emit("skipped", item=item, reason=exc.message)
                continue
            except Exception as exc:
                # Not mapped by the API client: never take it as a reason to lose the segment.
                self.uploading = None
                log.warning("segment %s: unexpected upload error", item.sequence, exc_info=True)
                message = f"{exc.__class__.__name__}: {exc}"
                delay = backoff
                self.delays.append(delay)
                self.last_error = f"falha ao enviar o segmento {item.sequence}: {message}"
                self._emit("retry", item=item, delay=delay, error=message)
                await self._pause(delay)
                backoff = min(backoff * 2, self.backoff_max)
                continue
            self.uploading = None
            self.online = True
            backoff = self.backoff_initial
            self.last_error = None
            self.store.ack_head()
            self._delete(path)
            self._emit(
                "uploaded",
                item=item,
                duplicate=bool(result.get("duplicate")),
                seconds=time.monotonic() - started,
                size=size,
            )

    async def _pause(self, delay: float) -> None:
        if self._sleep is not None:
            await self._sleep(delay)
        else:
            await asyncio.sleep(delay)

    def _delete(self, path: Path) -> None:
        if self.keep_segments:
            return
        with contextlib.suppress(OSError):
            path.unlink()

    def _emit(self, event: str, **data: Any) -> None:
        if self._on_status is None:
            return
        try:
            self._on_status(event, data)
        except Exception:
            log.exception("uploader status callback failed")
