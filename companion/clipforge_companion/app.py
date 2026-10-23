"""Orchestration of the companion: OBS mode and file mode.

Life of a session::

    create (or resume) session ─▶ uploader + heartbeat tasks
          │
          ▼
    capture: FFmpeg follows the recording, every completed segment is queued
          │      (the last one of the capture is queued with final=true)
          ▼
    finish: wait until the queue is empty ─▶ POST /end ─▶ local state removed

OBS mode creates a session when OBS records (or streams without recording, so the warning can
be shown on the Lives page), captures ``outputPath`` of ``RecordStateChanged`` and ends the
session after the recording stops. File mode captures one file until it stops growing.

Ctrl+C once: finish the current segment, send the queue and end the session. Ctrl+C twice:
leave immediately; the queue stays on disk and is sent by the next run (in OBS mode right at
startup: the rest of that recording is captured, or the queue is sent, and the session ended).
A session whose final segment was already queued (Ctrl+C once, then twice) is never resumed:
the next run sends its queue and ends it, and a recording that goes on is captured in a new
session, from that segment.

Only one companion runs per device and work directory, and a session directory is locked by
the process working on it, so a second instance can never take a live session for a leftover.

A session is always cut with its own segment length: a resumed one keeps the length it was
created with, whatever ``--segment-seconds`` says now. After an OBS reconnect the file OBS writes
is looked up again (OBS may have been restarted and be recording another file).
"""

from __future__ import annotations

import asyncio
import contextlib
import enum
import logging
import math
import os
import re
import shutil
import signal
import sys
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any, NamedTuple, TypeGuard

from clipforge_companion.api import (
    ApiError,
    AuthError,
    CompanionApi,
    PlanLimitError,
    RejectedError,
    RetryableError,
    SessionClosed,
)
from clipforge_companion.config import Config
from clipforge_companion.heartbeat import Heartbeat
from clipforge_companion.locking import FileLock
from clipforge_companion.obs import (
    OUTPUT_PAUSED,
    OUTPUT_RECONNECTED,
    OUTPUT_RESUMED,
    OUTPUT_STARTED,
    OUTPUT_STOPPED,
    ObsClient,
    ObsError,
)
from clipforge_companion.segmenter import (
    RecordingFormat,
    RecordingFormatError,
    Segment,
    Segmenter,
    SegmenterError,
    check_recording_format,
    wait_for_recording,
)
from clipforge_companion.status import (
    StatusReporter,
    StatusSnapshot,
    fmt_clock,
    fmt_number,
    fmt_seconds,
    fmt_size,
    short_id,
)
from clipforge_companion.uploader import EndingRegistry, SessionStore, Uploader

log = logging.getLogger(__name__)

EXIT_OK = 0
EXIT_ERROR = 1
EXIT_USAGE = 2
EXIT_AUTH = 3
EXIT_ABORTED = 130

RECORDING_EXTENSIONS = {".mkv", ".mp4", ".mov", ".flv", ".ts", ".m4v", ".webm", ".fmp4"}
RECENT_RECORDING_SECONDS = 120.0
UNKNOWN_RECORDING = "<desconhecido>"
SESSION_LOCK_FILE = "session.lock"
# obs-websocket reports automatic file splits itself (RecordFileChanged) since 5.5.0.
RECORD_FILE_CHANGED_SINCE = (5, 5, 0)
REPLAY_BUFFER_PREFIX = "replay"  # OBS default name of Replay Buffer saves
# FFmpeg runs over one recording file that may fail in a row without reaching new data: each
# run reads the file from the start again, so this bounds how often that happens.
MAX_FAILED_RUNS = 3
PROGRESS_EPSILON = 0.5  # seconds: a run that got less than this further made no progress

TOKEN_REJECTED = (
    "Token inválido ou revogado. Crie um novo token na página Lives (Dispositivos) e rode o "
    "companion de novo."
)
STREAMING_WITHOUT_RECORDING = (
    "O OBS está transmitindo, mas não está gravando: os destaques são detectados a partir da "
    "gravação. Clique em Iniciar gravação no OBS (ou rode o companion com --auto-record)."
)


class Outcome(enum.Enum):
    ENDED = "ended"  # capture over; the final segment (if any) is queued
    REFUSED = "refused"  # the recording could not be captured
    ABORTED = "aborted"  # second Ctrl+C
    FATAL = "fatal"  # token rejected or session closed by the server


class _Exit(Exception):
    def __init__(self, code: int) -> None:
        super().__init__(code)
        self.code = code


class FoundRecording(NamedTuple):
    """An OBS recording file that can be captured, with the recording generation it belongs to
    and the OBS connection (``connect_count``) it was resolved on. Its capture may start much
    later (the previous session still being analysed): a reconnect in between can hide that
    OBS was restarted and records another file by now, although it reports "recording" before
    and after. The capture then looks for that file once this one stops growing, exactly as
    after a reconnect during the capture (see CompanionApp._split_scan_needed)."""

    path: Path
    generation: int
    connect_count: int


@dataclass(frozen=True, slots=True)
class RecordingMark:
    """Where the capture of a recording file stopped when its session ended: FFmpeg's number
    (counted from the file's first segment) and file time of the last segment sent, the
    segment length used and the file size then. When OBS turns out to be still writing that
    file (its capture ended while OBS was unreachable), a new session goes on from that
    segment instead of sending the whole recording again.

    It is also written to ``queue.json`` when the final segment is queued: if the run stops
    before the session is ended, the next run does not resume that session (nothing may follow
    a final segment) but ends it, and captures the rest of the recording in a new session that
    starts with that segment."""

    sequence: int
    start: float
    segment_seconds: float
    size: int

    def to_json(self, path: Path) -> dict[str, Any]:
        return {
            "path": str(path),
            "sequence": self.sequence,
            "start": self.start,
            "segmentSeconds": self.segment_seconds,
            "size": self.size,
        }

    @staticmethod
    def from_json(data: object) -> tuple[str, RecordingMark] | None:
        """The file and its mark as written by :meth:`to_json`; None when missing or invalid."""
        if not isinstance(data, dict):
            return None
        path, sequence, size = data.get("path"), data.get("sequence"), data.get("size")
        start, seconds = data.get("start"), data.get("segmentSeconds")
        if not isinstance(path, str) or not path:
            return None
        if not (_is_count(sequence) and _is_count(size)):
            return None
        if not (_is_finite(start) and _is_finite(seconds)) or start < 0 or seconds <= 0:
            return None
        return path, RecordingMark(sequence, float(start), float(seconds), size)


def _is_count(value: object) -> TypeGuard[int]:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _is_finite(value: object) -> TypeGuard[int | float]:
    return isinstance(value, int | float) and not isinstance(value, bool) and math.isfinite(value)


def _path_key(path: str | os.PathLike[str]) -> str:
    """Comparable form of a file path. OBS on Windows reports Qt-style paths (forward
    slashes), ``Path`` gives backslashes and the file system ignores case: every set of
    recording paths and every comparison goes through this."""
    return os.path.normcase(os.path.abspath(path))


def _same_path(a: str | os.PathLike[str] | None, b: str | os.PathLike[str] | None) -> bool:
    if not a or not b:
        return False
    return _path_key(a) == _path_key(b)


def _version_tuple(value: object) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", str(value or ""))[:3])


def _created_time(stat: os.stat_result) -> float | None:
    """Creation time of a file when the platform keeps it (Windows, macOS)."""
    birth = getattr(stat, "st_birthtime", None)
    if birth:
        return float(birth)
    if sys.platform == "win32":
        return float(stat.st_ctime)  # creation time on Windows before Python 3.12
    return None


def recent_recordings(
    directory: Path,
    *,
    max_age: float = RECENT_RECORDING_SECONDS,
    exclude: set[str] | frozenset[str] = frozenset(),
) -> list[Path]:
    """Recording-like files in ``directory`` (or one level below) modified in the last
    ``max_age`` seconds, newest first."""
    if not directory.is_dir():
        return []
    now = time.time()
    excluded = {_path_key(p) for p in exclude if p != UNKNOWN_RECORDING}
    found: list[tuple[float, Path]] = []
    for pattern in ("*", "*/*"):
        for path in directory.glob(pattern):
            if path.suffix.lower() not in RECORDING_EXTENSIONS:
                continue
            if _path_key(path) in excluded:
                continue
            try:
                stat = path.stat()
            except OSError:
                continue
            if path.is_file() and now - stat.st_mtime <= max_age:
                found.append((stat.st_mtime, path))
    found.sort(key=lambda item: item[0], reverse=True)
    return [path for _, path in found]


def newest_recording(
    directory: Path,
    *,
    max_age: float = RECENT_RECORDING_SECONDS,
    exclude: set[str] | frozenset[str] = frozenset(),
) -> Path | None:
    """Newest recording-like file in ``directory`` (or one level below) modified recently."""
    candidates = recent_recordings(directory, max_age=max_age, exclude=exclude)
    return candidates[0] if candidates else None


class LiveSession:
    """One session on the API: persistent store + uploader task + heartbeat task.

    A ``leftover`` session belongs to an earlier run and is only drained and ended: it never
    captures, and a stop requested for it on the website does not stop the companion."""

    def __init__(
        self,
        app: CompanionApp,
        store: SessionStore,
        *,
        resumed: bool = False,
        leftover: bool = False,
    ) -> None:
        self.app = app
        self.store = store
        self.resumed = resumed
        self.leftover = leftover
        self.server_next_sequence: int | None = None
        self.capturing = False
        self.generation: int | None = None  # OBS recording generation being captured
        self.captured_paths: list[str] = []
        self.last_segment: Segment | None = None  # last segment queued
        self.error: str | None = None
        self.fatal: ApiError | None = None
        self.heartbeat_restarts = 0
        self._closed = False
        config = app.config
        self.uploader = Uploader(
            app.api,
            store,
            keep_segments=config.keep_segments,
            max_upload_mbps=config.max_upload_mbps,
            backoff_initial=app.upload_backoff[0],
            backoff_max=app.upload_backoff[1],
            on_status=app.on_upload_status,
        )
        self.heartbeat = Heartbeat(
            app.api,
            store.session_id,
            lambda: app.companion_state(self),
            interval=config.heartbeat_interval,
            min_spacing=min(1.0, config.heartbeat_interval),
            on_stop_requested=None if leftover else app.on_server_stop,
            on_fatal=self.set_fatal,
            on_online_change=app.on_online_change,
        )
        self._uploader_task: asyncio.Task[None] | None = None
        self._heartbeat_task: asyncio.Task[None] | None = None

    @property
    def session_id(self) -> str:
        return self.store.session_id

    @property
    def segment_seconds(self) -> float:
        """The session's own segment length (the one it was created with)."""
        return self.store.segment_seconds or float(self.app.config.segment_seconds)

    @property
    def uploader_done(self) -> bool:
        return self._uploader_task is not None and self._uploader_task.done()

    def start(self) -> None:
        self._uploader_task = asyncio.create_task(self.uploader.run(), name="uploader")
        self._uploader_task.add_done_callback(self._uploader_finished)
        self._start_heartbeat()

    def _start_heartbeat(self, delay: float = 0.0) -> None:
        task = asyncio.create_task(self.heartbeat.run(initial_delay=delay), name="heartbeat")
        task.add_done_callback(self._heartbeat_finished)
        self._heartbeat_task = task

    def _heartbeat_finished(self, task: asyncio.Task[None]) -> None:
        """The heartbeat only ends on stop() or on a fatal API answer. Had it died any other
        way, nothing would notice: the session would show offline and then go to error."""
        if task.cancelled() or self._closed or self.heartbeat.stopped:
            return
        exc = task.exception()
        if exc is None:
            return  # fatal API answer, already reported through set_fatal
        self.heartbeat_restarts += 1
        log.error("heartbeat stopped unexpectedly; restarting it", exc_info=exc)
        self._start_heartbeat(delay=min(self.heartbeat.interval, 5.0))

    def set_fatal(self, exc: ApiError) -> None:
        if self.fatal is None:
            self.fatal = exc
        self.app.notify()

    def _uploader_finished(self, task: asyncio.Task[None]) -> None:
        if task.cancelled():
            return
        exc = task.exception()
        if exc is not None:
            log.error("uploader stopped unexpectedly", exc_info=exc)
            self.set_fatal(ApiError(f"erro interno no envio: {exc}"))
        elif self.uploader.fatal is not None:
            self.set_fatal(self.uploader.fatal)
        self.app.notify()

    async def stop_heartbeat(self) -> None:
        self.heartbeat.stop()
        if self._heartbeat_task is not None:
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await asyncio.wait_for(self._heartbeat_task, 15)

    async def close(self) -> None:
        self._closed = True
        self.heartbeat.stop()
        for task in (self._uploader_task, self._heartbeat_task):
            if task is not None and not task.done():
                task.cancel()
        for task in (self._uploader_task, self._heartbeat_task):
            if task is not None:
                with contextlib.suppress(asyncio.CancelledError, Exception):
                    await task


ObsFactory = Callable[["CompanionApp"], ObsClient]


class CompanionApp:
    """Runs the companion until the work is done, Ctrl+C, or a fatal error."""

    def __init__(
        self,
        config: Config,
        *,
        api: CompanionApi | None = None,
        status: StatusReporter | None = None,
        obs_factory: ObsFactory | None = None,
        install_signal_handlers: bool = True,
        settle_seconds: float = 2.0,
        poll_interval: float = 0.5,
        upload_backoff: tuple[float, float] = (2.0, 60.0),
        api_backoff: tuple[float, float] = (2.0, 60.0),
        record_wait_timeout: float = 30.0,
        stall_obs_timeout: float = 60.0,
        split_scan_seconds: float = 5.0,
        split_growth_seconds: float = 2.0,
        split_window_seconds: float = 15.0,
        ending_wait: tuple[float, float] = (5.0, 30.0),
        leftover_grace_seconds: float = 10.0,
    ) -> None:
        self.config = config
        self._owns_api = api is None
        self.api = api or CompanionApi(config.api_url, config.token)
        self.status = status or StatusReporter()
        self._obs_factory = obs_factory
        self._install_signal_handlers = install_signal_handlers
        self.settle_seconds = settle_seconds
        self.poll_interval = poll_interval
        self.upload_backoff = upload_backoff
        self.api_backoff = api_backoff
        self.record_wait_timeout = record_wait_timeout
        self.stall_obs_timeout = stall_obs_timeout
        self.split_scan_seconds = split_scan_seconds
        self.split_growth_seconds = split_growth_seconds
        self.split_window_seconds = split_window_seconds
        self.ending_wait = ending_wait
        self.leftover_grace_seconds = leftover_grace_seconds

        self.me: dict[str, Any] = {}
        self.obs: ObsClient | None = None
        self.obs_connected = False
        self.obs_synced = False
        self.obs_streaming = False
        self.obs_recording = False
        self.obs_paused = False
        self.obs_version: dict[str, Any] = {}
        self.record_path: str | None = None
        self.file_changed_to: str | None = None
        self.file_changed_generation = 0  # recording generation file_changed_to belongs to
        # Bumped on every recording start/stop: a capture belongs to one generation, so a new
        # recording started while FFmpeg still drains the previous one is never taken for it.
        self.record_generation = 0
        self.recording_error: str | None = None  # why the current recording cannot be captured
        # Handled recordings, as _path_key() values.
        self.unusable_recordings: set[str] = set()
        self.finished_recordings: set[str] = set()
        self.capture_marks: dict[str, RecordingMark] = {}  # by _path_key() of the file
        self.session: LiveSession | None = None
        self.sessions_ended = 0
        self.ended_sessions: list[dict[str, Any]] = []
        # Sessions ended by this process (or, through the registry, by an earlier run) that may
        # still be "ending" (segments being analysed): the device cannot open a new session until
        # they are terminal.
        self.ending_sessions: set[str] = set()
        self._ending_registry: EndingRegistry | None = None
        self._instance_lock: FileLock | None = None
        self._store_locks: dict[str, FileLock] = {}
        self.api_online = True
        self.exit_code = EXIT_OK
        self.server_stop = False
        self._interrupts = 0
        self._stop = asyncio.Event()
        self._abort = asyncio.Event()
        self._changed = asyncio.Event()
        self._background: set[asyncio.Task[Any]] = set()

    # -- public control ------------------------------------------------------------------------

    @property
    def stopping(self) -> bool:
        return self._stop.is_set()

    @property
    def aborted(self) -> bool:
        return self._abort.is_set()

    def notify(self) -> None:
        self._changed.set()

    def request_stop(self) -> None:
        """Ctrl+C: the first one finishes gracefully, the second one leaves immediately."""
        self._interrupts += 1
        if self._interrupts == 1:
            if self._stop.is_set():
                self.status.info("Já encerrando. Pressione Ctrl+C de novo para sair imediatamente.")
            elif self.session is not None:
                self.status.info(
                    "Encerrando: finalizando o segmento atual e enviando a fila. "
                    "Pressione Ctrl+C de novo para sair imediatamente."
                )
            else:
                self.status.info("Encerrando.")
            self._stop.set()
        else:
            if not self._abort.is_set():
                self.status.warn("Saindo imediatamente.")
            self._stop.set()
            self._abort.set()
        self.notify()

    def on_server_stop(self) -> None:
        if self.server_stop:
            return
        self.server_stop = True
        self.status.info("Encerramento solicitado pelo site: finalizando a sessão.")
        self._stop.set()
        self.notify()

    def on_online_change(self, online: bool) -> None:
        if online != self.api_online:
            self.api_online = online
            if online:
                self.status.info("API acessível novamente.")
            else:
                self.status.warn("offline: a API não respondeu; os segmentos ficam na fila.")
        self.update_state()

    # -- entry point ---------------------------------------------------------------------------

    async def run(self) -> int:
        previous = self._set_signal_handlers() if self._install_signal_handlers else {}
        try:
            await self._identify()
            if not self._lock_instance():
                return EXIT_ERROR
            self._load_ending_sessions()
            if self.config.mode == "file":
                return await self._run_file_mode()
            return await self._run_obs_mode()
        except _Exit as exc:
            return exc.code
        except AuthError:
            self.status.error(TOKEN_REJECTED)
            return EXIT_AUTH
        except ApiError as exc:
            log.warning("unhandled API error", exc_info=True)
            self.status.error(f"A API respondeu de forma inesperada: {exc.message}")
            return EXIT_ERROR
        finally:
            for task in list(self._background):
                task.cancel()
            self._restore_signal_handlers(previous)
            self._release_locks()
            if self._owns_api:
                await self.api.aclose()

    # -- state for heartbeat and console -------------------------------------------------------

    def companion_state(self, session: LiveSession | None = None) -> dict[str, Any]:
        session = session or self.session
        uploader = session.uploader if session else None
        leftover = bool(session and session.leftover)
        error = (session.error if session else None) or (uploader.last_error if uploader else None)
        if error is None and self.config.mode == "obs" and not leftover:
            if self.obs_streaming and not self.obs_recording:
                error = STREAMING_WITHOUT_RECORDING
            elif self.obs_recording and not (session and session.capturing):
                error = self.recording_error
        if leftover:
            recording = False  # an old session: its recording is over, only the queue is sent
        elif self.config.mode == "obs":
            recording = self.obs_recording
        else:
            recording = bool(session and session.capturing)
        return {
            "obsConnected": self.obs_connected if self.config.mode == "obs" else False,
            "streaming": self.obs_streaming,
            "recording": recording,
            "uploading": bool(uploader and uploader.is_uploading),
            "queueLength": uploader.queue_length if uploader else 0,
            "lastError": error[:500] if error else None,
        }

    def update_state(self) -> None:
        session = self.session
        uploader = session.uploader if session else None
        uploading = bool(uploader and uploader.is_uploading)
        index = total = 0
        if session is not None:
            store = session.store
            total = store.enqueued_total
            done = store.uploaded + int(store.data.get("skipped", 0))
            index = min(total, done + 1)
        self.status.state(
            StatusSnapshot(
                mode=self.config.mode,
                obs_connected=self.obs_connected,
                streaming=self.obs_streaming,
                recording=self.obs_recording,
                capturing=bool(session and session.capturing),
                uploading=uploading,
                upload_index=index,
                upload_total=total,
                queue_length=uploader.queue_length if uploader else 0,
                online=self.api_online and (uploader.online if uploader else True),
            )
        )

    def on_upload_status(self, event: str, data: dict[str, Any]) -> None:
        item = data.get("item")
        sequence = getattr(item, "sequence", "?")
        if event == "uploaded":
            size = data.get("size")
            extra = f"{fmt_size(size)} em " if isinstance(size, int) else ""
            note = " (já estava no servidor)" if data.get("duplicate") else ""
            final = " · último" if getattr(item, "final", False) else ""
            self.status.info(
                f"Segmento {sequence} enviado ({extra}{fmt_seconds(data.get('seconds', 0.0))})"
                f"{note}{final}"
            )
        elif event == "retry":
            self.status.warn(
                f"Falha ao enviar o segmento {sequence} ({data.get('error')}); nova tentativa em "
                f"{fmt_number(data.get('delay', 0.0), 0)} s."
            )
        elif event == "rejected":
            self.status.error(f"A API recusou o segmento {sequence}: {data.get('error')}")
            if data.get("status") == 413:
                self.status.warn(
                    "Segmento grande demais para a API: use um --segment-seconds menor ou "
                    "reduza o bitrate da gravação."
                )
        elif event == "skipped":
            self.status.warn(f"Segmento {sequence} ignorado: {data.get('reason')}")
        if self.session is not None:
            self.session.heartbeat.poke()
        self.update_state()

    # -- OBS callbacks -------------------------------------------------------------------------

    def on_obs_event(self, event_type: str, data: dict[str, Any]) -> None:
        if event_type == "RecordStateChanged":
            state = data.get("outputState")
            raw_path = data.get("outputPath")
            path = os.path.normpath(str(raw_path)) if raw_path else None
            if state == OUTPUT_STARTED:
                self.obs_recording = True
                self.obs_paused = False
                self.record_generation += 1
                self.record_path = path
                self.file_changed_to = None
                self.recording_error = None
                self.status.info("OBS começou a gravar" + (f": {path}" if path else "."))
            elif state == OUTPUT_STOPPED:
                self.obs_recording = False
                self.obs_paused = False
                self.record_generation += 1
                self.status.info("OBS parou de gravar.")
            elif state == OUTPUT_PAUSED:
                self.obs_paused = True
                self.status.info("Gravação pausada no OBS.")
            elif state == OUTPUT_RESUMED:
                self.obs_paused = False
                self.status.info("Gravação retomada no OBS.")
        elif event_type == "RecordFileChanged":
            new_path = data.get("newOutputPath")
            if new_path:
                path = os.path.normpath(str(new_path))
                self.file_changed_to = path
                self.file_changed_generation = self.record_generation
                self.record_path = path
                self.status.info(f"OBS passou a gravar em um novo arquivo: {path}")
        elif event_type == "StreamStateChanged":
            state = data.get("outputState")
            if state in (OUTPUT_STARTED, OUTPUT_RECONNECTED):
                if not self.obs_streaming:
                    self.status.info("OBS começou a transmitir.")
                self.obs_streaming = True
            elif state == OUTPUT_STOPPED:
                if self.obs_streaming:
                    self.status.info("OBS parou de transmitir.")
                self.obs_streaming = False
        if self.session is not None:
            self.session.heartbeat.poke()
        self.notify()
        self.update_state()

    def on_obs_connection(self, connected: bool) -> None:
        self.obs_connected = connected
        if connected:
            self._spawn(self._sync_obs())
        else:
            self.obs_synced = False
            if not self.stopping:
                self.status.warn("OBS desconectado; tentando reconectar (a captura continua).")
        if self.session is not None:
            self.session.heartbeat.poke()
        self.notify()
        self.update_state()

    def on_obs_retry(self, delay: float, reason: str) -> None:
        attempts = len(self.obs.retry_delays) if self.obs is not None else 1
        never_connected = self.obs is not None and self.obs.connect_count == 0
        if never_connected and attempts > 1 and attempts % 5:
            return  # OBS not opened yet: do not repeat the same line every few seconds
        self.status.warn(f"OBS indisponível: {reason}. Nova tentativa em {fmt_number(delay, 0)} s.")

    async def _sync_obs(self) -> None:
        obs = self.obs
        if obs is None:
            return
        count = obs.connect_count
        try:
            version = await obs.get_version()
            record = await obs.get_record_status()
            try:
                stream = await obs.get_stream_status()
            except ObsError:
                stream = {}
        except ObsError as exc:
            log.warning("OBS state sync failed: %s", exc)
            return
        if obs.connect_count != count:
            return  # superseded by a newer connection, which syncs again
        self.obs_version = version
        recording = bool(record.get("outputActive"))
        if recording != self.obs_recording:
            # A start or stop happened while we were not connected (events missed).
            self.record_generation += 1
            self.recording_error = None
        self.obs_recording = recording
        self.obs_paused = bool(record.get("outputPaused"))
        self.obs_streaming = bool(stream.get("outputActive"))
        reconnect = count > 1
        if not self.obs_recording or reconnect:
            # After a reconnect events may have been missed: resolve the file again.
            self.record_path = None
        if reconnect and self.obs_recording and not self.obs_paused:
            # Before obs_synced: nothing may pick a file meanwhile.
            await self._check_recording_after_reconnect()
            if obs.connect_count != count:
                return
        self.obs_synced = True
        parts = []
        if self.obs_streaming:
            parts.append("transmitindo")
        if self.obs_recording:
            parts.append("gravando")
        self.status.info(
            f"OBS conectado (OBS {version.get('obsVersion', '?')}, obs-websocket "
            f"{version.get('obsWebSocketVersion', '?')})"
            + (f": {', '.join(parts)}." if parts else "; aguardando gravação.")
        )
        if self.session is not None:
            self.session.heartbeat.poke()
        self.notify()
        self.update_state()

    async def _check_recording_after_reconnect(self) -> None:
        """OBS events are lost while it is not connected: it may have been restarted and be
        recording another file by now, although it reports "recording" before and after. The
        file it writes is looked up (GetRecordDirectory) and captured, whether it is the one
        seen before or not:

        * the file being captured still grows: nothing changes;
        * the next file of the same (split) recording: the capture goes on in it;
        * another recording: the capture in progress ends with its file and the new one gets
          its own session;
        * no capture in progress: the growing file is the one the main loop captures, even
          one whose capture already ended (OBS was unreachable for too long); a new session
          then goes on from where the previous one stopped. A file resolved before this
          reconnect whose capture has not started yet (its session still being created) is
          captured first; once it stops growing, its capture finds the newer file
          (FoundRecording) and ends, and the newer file gets its own session.

        Nothing growing (paused, or not visible from here): left to the stall wait and to the
        folder lookup of the main loop."""
        generation = self.record_generation
        session = self.session
        current: Path | None = None
        if (
            session is not None
            and session.capturing
            and session.captured_paths
            and session.generation == generation
        ):
            current = Path(session.captured_paths[-1])
        found: tuple[Path, bool] | None = None
        if current is not None and session is not None:
            if current in await self._growing([current]):
                found = (current, False)
            else:
                found = await self._find_next_recording(session, current, new_allowed=True)
        else:
            active = await self._find_active_recording()
            found = (active, False) if active is not None else None
        if found is None or self.record_generation != generation or not self.obs_recording:
            return  # nothing grows, or OBS events took over meanwhile
        path, split = found
        capturing = (
            current is not None
            and session is not None
            and session is self.session
            and session.capturing
        )
        if capturing and _same_path(path, current):
            self.record_path = str(path)  # the same recording goes on
        elif capturing and split:
            self.file_changed_to = str(path)
            self.file_changed_generation = generation
            self.record_path = str(path)
            self.status.info(f"OBS passou a gravar em um novo arquivo: {path}")
        elif capturing:
            self._new_recording_found(path)
        else:  # no capture in progress (any more): the main loop captures this file
            key = _path_key(path)
            if key in self.finished_recordings:
                self.finished_recordings.discard(key)
                self.status.info(
                    f"O OBS continua gravando {path.name}, cuja captura tinha terminado enquanto "
                    "ele estava inacessível: a captura continua numa sessão nova."
                )
            self.record_path = str(path)
        self.notify()

    def _new_recording_found(self, path: Path) -> None:
        """OBS writes a file of a recording that started while its events could not be
        received (OBS restarted, connection lost): a new recording, with its own session. The
        capture in progress ends where its file ended."""
        self.record_generation += 1
        self.record_path = str(path)
        self.recording_error = None
        self.file_changed_to = None
        self.status.info(
            f"O OBS está gravando um arquivo novo, iniciado enquanto ele estava desconectado: "
            f"{path}. A sessão atual termina com o que já foi gravado e a gravação nova vai "
            "para uma sessão nova."
        )
        self.notify()

    async def _find_active_recording(self) -> Path | None:
        """The newest file growing in the OBS recording folder (Replay Buffer saves aside)."""
        obs = self.obs
        if obs is None:
            return None
        try:
            directory = await obs.get_record_directory()
        except ObsError as exc:
            log.info("GetRecordDirectory failed: %s", exc)
            return None
        if not directory:
            return None
        candidates = [
            path
            for path in recent_recordings(Path(directory))
            if not path.name.lower().startswith(REPLAY_BUFFER_PREFIX)
        ][:5]
        grown = await self._growing(candidates)
        return next((path for path in candidates if path in grown), None)

    async def _growing(self, paths: list[Path]) -> set[Path]:
        """Those of ``paths`` whose size grows within ``split_growth_seconds``."""
        sizes: dict[Path, int] = {}
        for path in paths:
            with contextlib.suppress(OSError):
                sizes[path] = path.stat().st_size
        if not sizes:
            return set()
        await asyncio.sleep(self.split_growth_seconds)
        grown: set[Path] = set()
        for path, size in sizes.items():
            with contextlib.suppress(OSError):
                if path.stat().st_size > size:
                    grown.add(path)
        return grown

    # -- modes ---------------------------------------------------------------------------------

    async def _run_file_mode(self) -> int:
        assert self.config.file is not None
        path = Path(os.path.abspath(self.config.file))
        if not path.is_file():
            self.status.error(f"Arquivo não encontrado: {path}")
            return EXIT_ERROR
        try:
            fmt = check_recording_format(path)
        except RecordingFormatError as exc:
            self.status.error(str(exc))
            return EXIT_ERROR
        if fmt is RecordingFormat.UNKNOWN:
            self.status.warn("Formato do arquivo não reconhecido; o FFmpeg vai tentar lê-lo.")
        session = await self._open_session(source=path, obs_info=None)
        assert session is not None  # only OBS mode gives up on a session it no longer needs
        outcome = await self._run_session(session, lambda: self._capture(session, path))
        return EXIT_ABORTED if outcome is Outcome.ABORTED else self.exit_code

    async def _run_obs_mode(self) -> int:
        config = self.config
        if self._obs_factory is not None:
            self.obs = self._obs_factory(self)
        else:
            self.obs = ObsClient(
                config.obs_url,
                config.obs_password,
                on_event=self.on_obs_event,
                on_connection_change=self.on_obs_connection,
                on_retry=self.on_obs_retry,
            )
        obs_task = asyncio.create_task(self.obs.run(), name="obs")
        obs_task.add_done_callback(lambda _: self.notify())
        self.status.info(f"Conectando ao OBS em {config.obs_url}...")
        try:
            outcome = await self._startup_leftovers(obs_task)
            if outcome is Outcome.ABORTED:
                return EXIT_ABORTED
            if outcome is Outcome.FATAL or self.stopping:
                return self.exit_code
            if outcome is Outcome.ENDED and not self._obs_activity():
                self.status.info("Aguardando a próxima gravação no OBS (Ctrl+C para sair).")
            while True:
                while not (self.stopping or obs_task.done() or self._obs_activity()):
                    await self._wait_changed(1.0)
                if obs_task.done():
                    return self._obs_failed(obs_task)
                if self.stopping:
                    return self.exit_code
                outcome = await self._obs_activity_session()
                if outcome is Outcome.ABORTED:
                    return EXIT_ABORTED
                if outcome is Outcome.FATAL or self.stopping:
                    return self.exit_code
                self.status.info("Aguardando a próxima gravação no OBS (Ctrl+C para sair).")
                # Never go straight back to the activity check: whatever state OBS is in, this
                # loop must not spin (and starve OBS events, heartbeats and Ctrl+C).
                await self._idle(1.0)
        finally:
            await self.obs.close()
            if not obs_task.done():
                obs_task.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await obs_task

    async def _startup_leftovers(self, obs_task: asyncio.Task[None]) -> Outcome | None:
        """OBS mode: sessions left by an earlier run (Ctrl+C twice, crash, reboot) are dealt
        with at startup, not only once OBS records again; meanwhile the server would time them
        out for being offline and the rest of their queue would be lost.

        * a session whose final segment is already queued (it was being ended): the queue is
          sent and the session ended, whatever OBS records (a recording OBS still writes goes
          on in a new session, from that segment: see _close_final_leftover);
        * the file OBS is still recording: left for the main loop, which resumes the session;
        * a finished recording still on disk: the rest of it is captured and the session ended;
        * otherwise: the queue is sent (with heartbeats) and the session ended.

        Returns None when there was nothing to do, ENDED once leftovers were dealt with."""
        stores = [
            s for s in SessionStore.find_unfinished(self.config.work_dir) if self._is_own_store(s)
        ]
        if not stores:
            return None
        deadline = time.monotonic() + self.leftover_grace_seconds
        while not (self.obs_connected and self.obs_synced) and not obs_task.done():
            remaining = deadline - time.monotonic()
            if remaining <= 0 or self.stopping:
                break
            await self._wait_changed(min(remaining, 1.0))
        if obs_task.done() or self.stopping:
            return None  # the main loop reports the OBS failure (or leaves)
        current: Path | None = None
        if self.obs_connected and self.obs_synced and self.obs_recording:
            current = await self._resolve_record_path()
        result: Outcome | None = None
        for store in stores:
            if self.stopping:
                break
            if not self._lock_store(store):
                self.status.info(
                    f"A sessão {short_id(store.session_id)} está em uso por outro companion; "
                    "ela não foi alterada."
                )
                continue
            if store.final_queued:
                await self._close_final_leftover(store)
                result = Outcome.ENDED
                continue
            source = (store.source or {}).get("path")
            if store.data.get("mode") == "obs" and source:
                path = Path(str(source))
                if self.obs_recording and (
                    _same_path(path, current) or (current is None and await self._is_growing(path))
                ):
                    continue  # OBS still records it: resumed by the main loop
                if path.is_file():
                    outcome = await self._finish_previous_recording(store, path)
                    if outcome in (Outcome.ABORTED, Outcome.FATAL):
                        return outcome
                    result = Outcome.ENDED
                    continue
            await self._close_leftover(store)
            result = Outcome.ENDED
        return result

    async def _finish_previous_recording(self, store: SessionStore, path: Path) -> Outcome | None:
        """Resumes a session whose recording finished while no companion was running: the
        rest of the file is captured (the last segment flagged final) and the session ended."""
        session = await self._try_resume(store)
        if session is None:
            return None
        self.status.info(
            f"Concluindo a sessão anterior {short_id(store.session_id)} com o restante de "
            f"{path.name}."
        )
        outcome = await self._run_session(session, lambda: self._capture(session, path))
        self.finished_recordings.add(_path_key(path))
        return outcome

    async def _is_growing(self, path: Path) -> bool:
        try:
            size = path.stat().st_size
        except OSError:
            return False
        await self._idle(self.split_growth_seconds)
        try:
            return path.stat().st_size > size
        except OSError:
            return False

    def _obs_failed(self, obs_task: asyncio.Task[None]) -> int:
        exc = None if obs_task.cancelled() else obs_task.exception()
        if exc is not None:
            self.status.error(str(exc))
        return EXIT_ERROR

    def _obs_activity(self) -> bool:
        """Something worth a session: OBS streams, or records a file we have not handled."""
        if not (self.obs_connected and self.obs_synced):
            return False
        if self.obs_streaming:
            return True
        if not self.obs_recording:
            return False
        path = self.record_path
        return not (path and self._is_handled(path))

    def _is_handled(self, path: str | os.PathLike[str]) -> bool:
        key = _path_key(path)
        return key in self.unusable_recordings or key in self.finished_recordings

    def _mark_unusable(self, path: str | os.PathLike[str], reason: str | None = None) -> None:
        self.unusable_recordings.add(_path_key(path))
        if reason:
            self.recording_error = reason

    async def _obs_activity_session(self) -> Outcome:
        found: FoundRecording | None = None
        if self.obs_recording:
            found = await self._usable_recording()
            if found is None and not self.obs_streaming:
                return Outcome.REFUSED
        recording = found.path if found else None
        session = await self._open_session(
            source=recording,
            obs_info={
                "version": self.obs_version.get("obsVersion"),
                "recordingPath": str(recording) if recording else None,
            },
            # A recording is captured even if it stops while the session is being created;
            # a session for streaming without recording is pointless once that is over.
            wanted=lambda: recording is not None or self._obs_activity(),
        )
        if session is None:
            return Outcome.ENDED
        return await self._run_session(session, lambda: self._obs_session_body(session, found))

    async def _obs_session_body(
        self, session: LiveSession, found: FoundRecording | None
    ) -> Outcome:
        warned = False
        auto_record_sent = False
        while True:
            if self.aborted:
                return Outcome.ABORTED
            if session.fatal is not None:
                return Outcome.FATAL
            if self.stopping:
                return Outcome.ENDED
            if found is None and self.obs_recording and self._obs_activity():
                found = await self._usable_recording()
            if found is not None:
                session.error = None
                outcome = await self._capture(
                    session,
                    found.path,
                    obs_mode=True,
                    generation=found.generation,
                    connect_count=found.connect_count,
                )
                handled = session.captured_paths or [str(found.path)]
                for path in handled:
                    if outcome is Outcome.REFUSED:
                        self._mark_unusable(path)
                    else:
                        self.finished_recordings.add(_path_key(path))
                return outcome
            if self.obs_streaming and not self.obs_recording:
                if not warned:
                    warned = True
                    self.status.warn(STREAMING_WITHOUT_RECORDING)
                    session.heartbeat.poke()
                if self.config.auto_record and not auto_record_sent and self.obs is not None:
                    auto_record_sent = True
                    try:
                        await self.obs.start_record()
                        self.status.info("Pedido de gravação enviado ao OBS (--auto-record).")
                    except ObsError as exc:
                        self.status.warn(f"Não foi possível iniciar a gravação no OBS: {exc}")
            elif self.obs_connected and self.obs_synced and not self.obs_streaming:
                if not self.obs_recording or not self._obs_activity():
                    return Outcome.ENDED  # streaming ended (or the recording is unusable)
            await self._wait_changed(1.0)

    async def _usable_recording(self) -> FoundRecording | None:
        """Resolves the current OBS recording file and checks that it can be followed. Returns
        it with the recording generation it belongs to and the OBS connection it was resolved
        on (both taken first: a later change must count as one)."""
        generation = self.record_generation
        connect_count = self._obs_connect_count()
        path = await self._resolve_record_path()
        if path is None:
            if self.obs_recording and not self.stopping:
                message = (
                    "Não foi possível descobrir o arquivo da gravação no OBS. Confira a pasta de "
                    "gravação em Configurações > Saída."
                )
                self.status.error(message)
                if self.record_path is None:
                    self.record_path = UNKNOWN_RECORDING
                self._mark_unusable(self.record_path, message)
            return None
        if self._is_handled(path):
            return None
        try:
            fmt = await wait_for_recording(
                path,
                timeout=self.record_wait_timeout,
                should_stop=lambda: self.stopping or not self.obs_recording,
            )
        except RecordingFormatError as exc:
            self.status.error(str(exc))
            self._mark_unusable(path, str(exc))
            if self.session is not None:
                self.session.error = str(exc)
            return None
        if fmt is RecordingFormat.PENDING:
            if not self.stopping and self.obs_recording:
                message = f"O arquivo da gravação não apareceu no disco: {path}"
                self.status.error(message)
                self._mark_unusable(path, message)
            return None
        return FoundRecording(path, generation, connect_count)

    async def _resolve_record_path(self) -> Path | None:
        if self.record_path and self.record_path != UNKNOWN_RECORDING:
            return Path(self.record_path)
        obs = self.obs
        handled = self.unusable_recordings | self.finished_recordings
        for _ in range(10):
            if obs is None or not self.obs_recording or self.stopping:
                return None
            try:
                directory = await obs.get_record_directory()
            except ObsError as exc:
                log.info("GetRecordDirectory failed: %s", exc)
                directory = ""
            candidate = newest_recording(Path(directory), exclude=handled) if directory else None
            if candidate is not None:
                self.record_path = str(candidate)
                self.status.info(f"Arquivo da gravação (pasta do OBS): {candidate}")
                return candidate
            await self._wait_changed(1.0)
        return None

    # -- sessions ------------------------------------------------------------------------------

    async def _identify(self) -> None:
        delay = self.api_backoff[0]
        while True:
            try:
                self.me = await self.api.me()
                break
            except RetryableError as exc:
                self.status.warn(
                    f"offline: não foi possível contatar a API em {self.config.api_url} "
                    f"({exc.message}). Nova tentativa em {fmt_number(delay, 0)} s."
                )
            except RejectedError as exc:
                self.status.error(
                    f"A API respondeu {exc.message} em /api/live/companion/me. Confira se --api "
                    "aponta para a API."
                )
                raise _Exit(EXIT_ERROR) from exc
            await self._sleep_or_stop(delay)
            delay = min(delay * 2, self.api_backoff[1])
        device = self.me.get("device") or {}
        plan = self.me.get("plan") or {}
        self.status.info(
            f"API conectada. Dispositivo: {device.get('name') or '?'} · plano "
            f"{plan.get('name') or plan.get('code') or '?'}"
        )

    @property
    def device_id(self) -> str | None:
        device = self.me.get("device") or {}
        value = device.get("id")
        return str(value) if value else None

    def _is_own_store(self, store: SessionStore) -> bool:
        device = store.data.get("deviceId")
        return store.data.get("apiUrl") == self.config.api_url and not (
            device and device != self.device_id
        )

    async def _open_session(
        self,
        *,
        source: Path | None,
        obs_info: dict[str, Any] | None,
        wanted: Callable[[], bool] | None = None,
    ) -> LiveSession | None:
        """Resumes the unfinished session of the same recording, closes other leftovers and
        creates a new session otherwise. Returns None only when ``wanted`` says the session is
        no longer needed while the previous one is still being analysed (OBS mode)."""
        config = self.config
        for store in SessionStore.find_unfinished(config.work_dir):
            if not self._is_own_store(store):
                log.info("leaving session %s of another API/device alone", store.session_id)
                continue
            if not self._lock_store(store):
                self.status.info(
                    f"A sessão {short_id(store.session_id)} está em uso por outro companion; "
                    "ela não foi alterada."
                )
                continue
            if (
                source is not None
                and store.data.get("mode") == config.mode
                and store.matches_source(source)
            ):
                resumed = await self._try_resume(store)
                if resumed is not None:
                    return resumed
                continue
            await self._close_leftover(store)
        network_delay = self.api_backoff[0]
        ending_delay = self.ending_wait[0]
        waiting_noted = False
        while True:
            self._check_abort()
            if self.stopping:
                raise _Exit(self.exit_code)
            if wanted is not None and not wanted():
                return None
            try:
                response = await self.api.create_session(
                    title=config.title, segment_seconds=config.segment_seconds, obs=obs_info
                )
                break
            except RetryableError as exc:
                delay = network_delay
                network_delay = min(network_delay * 2, self.api_backoff[1])
                self.status.warn(
                    f"offline: não foi possível criar a sessão ({exc.message}). Nova tentativa em "
                    f"{fmt_number(delay, 0)} s."
                )
            except PlanLimitError as exc:
                if not await self._previous_session_ending():
                    self.status.error(
                        "Não foi possível criar a sessão: "
                        + (exc.detail or exc.message)
                        + ". Encerre a sessão ativa na página Lives ou confira os limites do "
                        "plano."
                    )
                    raise _Exit(EXIT_ERROR) from exc
                # A session this device ended (in this run or an earlier one) is still
                # "ending": finished for us, but its last segments are being analysed and the
                # API still counts it as the device's active session. Retried with backoff.
                delay = ending_delay
                ending_delay = min(ending_delay * 2, self.ending_wait[1])
                if not waiting_noted:
                    waiting_noted = True
                    self.status.info(
                        "Aguardando a análise da sessão anterior terminar no servidor para criar "
                        "a nova sessão (nada se perde: a gravação continua no disco)."
                    )
                log.info("previous session still ending; new attempt in %.0f s", delay)
            except RejectedError as exc:
                self.status.error(f"A API recusou a criação da sessão: {exc.message}")
                raise _Exit(EXIT_ERROR) from exc
            await self._sleep_or_stop(
                delay, wake=None if wanted is None else (lambda: not wanted())
            )
        data = response.get("session") or {}
        session_id = data.get("id")
        if not session_id:
            self.status.error("Resposta inesperada da API ao criar a sessão (sem id).")
            raise _Exit(EXIT_ERROR)
        if self.ending_sessions:
            # The API only creates a session when every other one of the device is terminal.
            self.ending_sessions.clear()
            self._save_ending_sessions()
        store = SessionStore.create(
            config.work_dir,
            str(session_id),
            mode=config.mode,
            api_url=config.api_url,
            device_id=self.device_id,
            segment_seconds=config.segment_seconds,
            title=data.get("title"),
        )
        if not self._lock_store(store):
            log.warning("could not lock the new session directory %s", store.directory)
        self.status.info(
            f"Sessão criada: {data.get('title') or 'live'} ({short_id(str(session_id))})"
        )
        return LiveSession(self, store)

    async def _try_resume(self, store: SessionStore) -> LiveSession | None:
        """The session of an earlier run, resumed (its heartbeat tells which sequences the
        server has), or None when it is not: its final segment is already queued (it is ended,
        see _close_final_leftover), the server ended it already, or refused it."""
        if store.final_queued:
            await self._close_final_leftover(store)
            return None
        session = LiveSession(self, store, resumed=True)
        try:
            response = await self._with_retry(
                lambda: self.api.heartbeat(store.session_id, self.companion_state(session))
            )
        except SessionClosed:
            self.status.info(
                f"A sessão anterior {short_id(store.session_id)} já foi encerrada no servidor."
            )
            self._retire(store, "closed_by_server")
            return None
        except RejectedError as exc:
            self.status.warn(
                f"A API recusou retomar a sessão anterior {short_id(store.session_id)} "
                f"({exc.message}); ela será encerrada com o que já foi gravado."
            )
            await self._close_leftover(store)
            return None
        if str(response.get("status")) == "ending":
            # /end was already sent for it (the run that ended it stopped right after): it is
            # finished as far as capturing goes. What is left is sent and it is ended again.
            self.status.info(
                f"A sessão anterior {short_id(store.session_id)} já foi encerrada e está em "
                "análise no servidor; ela não é retomada."
            )
            await self._close_leftover(store)
            return None
        next_sequence = response.get("nextSequence")
        if isinstance(next_sequence, int):
            session.server_next_sequence = next_sequence
        self.status.info(
            f"Retomando a sessão {short_id(store.session_id)} "
            f"({len(store.items)} segmento(s) na fila)."
        )
        self._note_segment_seconds(store)
        return session

    def _note_segment_seconds(self, store: SessionStore) -> None:
        """A resumed session is cut again with the segment length it was created with: the
        sequences already sent and the range skipped depend on it."""
        stored = store.segment_seconds
        wanted = float(self.config.segment_seconds)
        if stored is None:
            log.warning(
                "session %s has no valid segmentSeconds; using %s s", store.session_id, wanted
            )
        elif stored != wanted:
            self.status.info(
                f"A sessão {short_id(store.session_id)} foi criada com segmentos de "
                f"{fmt_number(stored, 0)} s e continua com eles; --segment-seconds "
                f"{fmt_number(wanted, 0)} vale só para sessões novas (cortar a mesma gravação "
                "de outro jeito pularia ou repetiria trechos)."
            )

    async def _close_leftover(self, store: SessionStore) -> None:
        """Sends what is left of a session interrupted earlier and ends it. Heartbeats keep the
        session online meanwhile: a long queue would otherwise outlast the server's offline
        timeout, the session would go to error and the rest of the queue would be lost."""
        self.status.info(
            f"Encerrando a sessão anterior {short_id(store.session_id)} "
            f"({len(store.items)} segmento(s) pendente(s))."
        )
        store.mark_last_final()
        session = LiveSession(self, store, resumed=True, leftover=True)
        previous, self.session = self.session, session
        session.start()
        session.uploader.close()
        try:
            while not session.uploader_done and session.fatal is None:
                self._check_abort()
                await self._wait_changed(0.5)
        finally:
            await session.close()
            self.session = previous
            self.update_state()
        fatal = session.fatal
        if isinstance(fatal, AuthError):
            raise fatal
        if isinstance(fatal, SessionClosed):
            self._retire(store, "closed_by_server")
            return
        if fatal is not None:  # internal error of the uploader: the queue stays for next time
            self.status.warn(
                f"Não foi possível enviar a fila da sessão anterior {short_id(store.session_id)}"
                f" ({fatal.message}); ela fica para a próxima execução."
            )
            self._unlock_store(store)
            return
        try:
            await self._end_session(store)
        except RejectedError as exc:
            # Never let this block every later start: the leftover is dropped locally.
            self.status.warn(
                f"A API recusou o encerramento da sessão anterior {short_id(store.session_id)} "
                f"({exc.message}). Ela foi descartada neste computador; se continuar ativa na "
                "página Lives, encerre-a por lá."
            )
        self._retire(store, "ended_after_restart")

    async def _close_final_leftover(self, store: SessionStore) -> None:
        """A session whose final segment is already queued (or sent) was being ended when its
        run stopped (Ctrl+C or a stop from the Lives page, then Ctrl+C again or a crash): it is
        never resumed. The server takes a final segment as the end of the live, and a stop cuts
        it short: segments after it would leave a gap and be analysed again. Its queue is sent
        and it is ended; a recording that goes on is captured in a new session, from that
        segment (its mark is kept in queue.json, see RecordingMark)."""
        self.status.info(
            f"A sessão anterior {short_id(store.session_id)} já estava sendo encerrada (o último "
            "segmento dela já está na fila como final): ela é encerrada, não retomada."
        )
        await self._close_leftover(store)

    def _retire(self, store: SessionStore, reason: str) -> None:
        """A session of an earlier run is over: marked ended and its local state removed. Where
        its capture stopped (kept with its final segment) is remembered: if this run captures
        that recording again, the new session goes on from there (see RecordingMark)."""
        store.mark_ended(reason)
        found = RecordingMark.from_json(store.recording_mark)
        if found is not None:
            path, mark = found
            self.capture_marks[_path_key(path)] = mark
        self._cleanup(store)

    @staticmethod
    def _last_sequence(store: SessionStore) -> int | None:
        """``lastSequence`` for /end: the last segment the API acknowledged (a segment that was
        dropped locally or refused never reached it)."""
        acked = store.last_acked_sequence
        return acked if acked >= 0 else None

    async def _end_session(self, store: SessionStore) -> None:
        """POST /end (retried while offline). A session that is already terminal is fine;
        raises :class:`AuthError` / :class:`RejectedError`."""
        last = self._last_sequence(store)
        try:
            await self._with_retry(lambda: self.api.end_session(store.session_id, last))
        except SessionClosed:
            return  # already terminal (stopped from the website, timed out...)
        self.ending_sessions.add(store.session_id)
        self._save_ending_sessions()

    def _load_ending_sessions(self) -> None:
        """Sessions an earlier run ended may still be "ending" (see EndingRegistry)."""
        self._ending_registry = EndingRegistry(
            self.config.work_dir / f"ending-{self._device_slug()}.json", self.config.api_url
        )
        self.ending_sessions |= self._ending_registry.load()

    def _save_ending_sessions(self) -> None:
        if self._ending_registry is not None:
            self._ending_registry.save(self.ending_sessions)

    async def _previous_session_ending(self) -> bool:
        """Whether a 409 plan_limit is worth retrying: True while a session this device ended
        (in this run or an earlier one) is still "ending" on the server (its segments are
        being analysed): for starting a new session it is finished, but until it is terminal
        the API refuses a new session for the device. Also True once when such a session has
        just become terminal. Asked through the old session's heartbeat."""
        state = {
            "obsConnected": self.obs_connected if self.config.mode == "obs" else False,
            "streaming": self.obs_streaming,
            "recording": False,
            "uploading": False,
            "queueLength": 0,
            "lastError": None,
        }
        retry = False
        for session_id in list(self.ending_sessions):
            try:
                response = await self.api.heartbeat(session_id, state)
            except SessionClosed:
                response = {"status": "ended"}  # terminal now
            except RetryableError:
                return True  # cannot tell right now: keep waiting
            except RejectedError:
                response = {"status": "error"}  # cannot be followed: stop waiting for it
            status = str(response.get("status"))
            if status in ("ended", "error"):
                self.ending_sessions.discard(session_id)
                self._save_ending_sessions()
                retry = True  # it may have turned terminal right after the 409
                continue
            if status in ("waiting", "live"):
                # Not being ended (a stale record): not a reason to wait.
                self.ending_sessions.discard(session_id)
                self._save_ending_sessions()
                continue
            return True
        return retry

    async def _run_session(
        self, session: LiveSession, capture: Callable[[], Awaitable[Outcome]]
    ) -> Outcome:
        self.session = session
        session.start()
        self.update_state()
        try:
            outcome = await capture()
            if outcome is Outcome.ABORTED:
                self._report_abort(session)
                return outcome
            if outcome is Outcome.FATAL:
                self._report_fatal(session)
                return outcome
            outcome = await self._finish_session(session)
            if outcome is Outcome.ABORTED:
                self._report_abort(session)
            elif outcome is Outcome.FATAL:
                self._report_fatal(session)
            return outcome
        finally:
            await session.close()
            self._unlock_store(session.store)
            self.session = None
            self.update_state()

    async def _finish_session(self, session: LiveSession) -> Outcome:
        store = session.store
        session.uploader.close()
        if store.items:
            self.status.info(f"Enviando {len(store.items)} segmento(s) restante(s)...")
        while not session.uploader_done:
            if self.aborted:
                return Outcome.ABORTED
            if session.fatal is not None:
                return Outcome.FATAL
            await self._wait_changed(1.0)
        if session.fatal is not None:
            return Outcome.FATAL
        await session.stop_heartbeat()
        last = self._last_sequence(store)
        try:
            await self._end_session(store)
        except AuthError as exc:
            session.fatal = exc
            return Outcome.FATAL
        except RejectedError as exc:
            self.status.warn(f"A API recusou o encerramento da sessão: {exc.message}")
        store.mark_ended("ended")
        self.sessions_ended += 1
        self.ended_sessions.append(
            {
                "sessionId": session.session_id,
                "uploaded": store.uploaded,
                "skipped": int(store.data.get("skipped", 0)),
                "lastSequence": last,
            }
        )
        self.status.info(
            f"Sessão {short_id(session.session_id)} encerrada: {store.uploaded} segmento(s) "
            "enviado(s)."
        )
        self._cleanup(store)
        return Outcome.ENDED

    def _report_fatal(self, session: LiveSession) -> None:
        exc = session.fatal
        if isinstance(exc, AuthError):
            self.status.error(TOKEN_REJECTED)
            self.exit_code = EXIT_AUTH
            return
        if isinstance(exc, SessionClosed):
            if exc.code == "quota_exceeded":
                reason = "os minutos de análise do plano acabaram"
            else:
                reason = exc.detail or exc.message
            self.status.error(
                f"A sessão foi encerrada pelo servidor ({reason}). Captura interrompida."
            )
            session.store.mark_ended("closed_by_server")
            self._cleanup(session.store)
        elif exc is not None:
            self.status.error(str(exc))
        self.exit_code = EXIT_ERROR

    def _report_abort(self, session: LiveSession) -> None:
        pending = len(session.store.items)
        if pending:
            self.status.warn(
                f"{pending} segmento(s) ficaram na fila em {session.store.directory} e serão "
                "enviados na próxima execução."
            )
        self.exit_code = EXIT_ABORTED

    def _cleanup(self, store: SessionStore) -> None:
        self._unlock_store(store)  # first: Windows cannot delete an open lock file
        if self.config.keep_segments:
            return
        shutil.rmtree(store.directory, ignore_errors=True)

    # -- capture -------------------------------------------------------------------------------

    async def _capture(
        self,
        session: LiveSession,
        recording: Path,
        *,
        obs_mode: bool = False,
        generation: int | None = None,
        connect_count: int | None = None,
    ) -> Outcome:
        """Captures ``recording`` (and, in OBS mode, the files it is split into) into the
        session. ``generation`` is the OBS recording generation the file belongs to: once OBS
        stopped that recording, the capture ends even if OBS is recording again.
        ``connect_count`` is the OBS connection the file was resolved on (default: the current
        one): after a reconnect since then, OBS may record another file (see FoundRecording).

        The session is cut with its own segment length (a resumed one keeps the length it was
        created with): the sequences already sent and the range skipped depend on it."""
        store = session.store
        if generation is None:
            generation = self.record_generation
        segment_seconds = session.segment_seconds
        source = store.source
        if source is not None and store.matches_source(recording):
            sequence_offset = int(source.get("sequenceOffset", 0))
            time_offset = float(source.get("timeOffset", 0.0))
            skip_through = store.last_sequence
            if session.server_next_sequence is not None:
                skip_through = max(skip_through, session.server_next_sequence - 1)
        else:
            sequence_offset = store.last_sequence + 1
            time_offset = store.last_end if store.last_sequence >= 0 else 0.0
            skip_through = store.last_sequence
            mark = self._take_mark(recording, segment_seconds)
            if mark is not None and store.last_sequence < 0:
                # Captured before by a session that has ended (OBS was unreachable for too
                # long, or a run stopped right after queuing its final segment): this one
                # starts with the last segment that one sent.
                sequence_offset, time_offset = -mark.sequence, -mark.start
                self.status.info(
                    f"{recording.name} já foi enviado até {fmt_clock(mark.start)} numa sessão "
                    "anterior; esta sessão continua a partir daí."
                )
            store.set_source(recording, sequence_offset=sequence_offset, time_offset=time_offset)
        session.capturing = True
        session.generation = generation
        session.captured_paths.append(str(recording))
        self.update_state()
        self.status.info(
            f"Capturando {recording.name} em segmentos de {fmt_number(segment_seconds, 0)} s."
        )
        # FFmpeg runs on this file that failed since a run last reached new data, and how far
        # (session time) the runs got. Re-creating the held segment is no progress.
        failures = 0
        reached: float | None = None
        held: Segment | None = None  # last segment of a stalled run, see _consume
        if connect_count is None:
            connect_count = self._obs_connect_count()
        try:
            while True:
                if self.aborted:
                    return Outcome.ABORTED
                segmenter = Segmenter(
                    ffmpeg=self.config.ffmpeg,
                    recording=recording,
                    out_dir=store.new_run_dir(),
                    segment_seconds=segment_seconds,
                    rw_timeout=self.config.rw_timeout,
                    sequence_offset=sequence_offset,
                    time_offset=time_offset,
                    skip_through=skip_through,
                    settle_seconds=self.settle_seconds,
                    poll_interval=self.poll_interval,
                )
                try:
                    await segmenter.start()
                except SegmenterError as exc:
                    # FFmpeg missing or not executable: nothing can be captured, stop the app.
                    self.status.error(str(exc))
                    session.error = str(exc)
                    self.exit_code = EXIT_ERROR
                    self._stop.set()
                    return Outcome.REFUSED
                last, interrupted = await self._consume(session, segmenter, held=held)
                held = None
                if interrupted is not None:
                    return interrupted
                end = segmenter.produced_end
                if end is not None and (reached is None or end > reached + PROGRESS_EPSILON):
                    # New data: the failures counted so far are behind, whether this run failed
                    # further on or not (FFmpeg exits 0 when a followed file stops growing, so
                    # most runs that end on a pause are clean), and so is the error reported.
                    reached = end
                    failures = 0
                    session.error = None
                elif segmenter.error:
                    failures += 1
                if segmenter.error:
                    self.status.warn(segmenter.error)
                    session.error = segmenter.error
                    if failures and (store.last_sequence < 0 or failures >= MAX_FAILED_RUNS):
                        session.error = self._capture_failed(recording, failures)
                        self.exit_code = EXIT_ERROR
                        # What was captured is sent, its last segment flagged final.
                        self._queue_final(session, last, recording, sequence_offset, time_offset)
                        return Outcome.REFUSED
                decision = self._after_run(recording, obs_mode, generation)
                if decision == "stalled":
                    decision = await self._wait_stalled(
                        session, recording, generation, connect_count
                    )
                    if decision == "grew":
                        if last is not None:
                            # Kept until the next run, which reads the file from the start
                            # again, has re-created it with the new data (see _consume).
                            held = last
                            skip_through = last.sequence - 1
                        else:
                            skip_through = store.last_sequence
                        self.status.info("A gravação voltou a crescer; retomando a captura.")
                        continue
                    if decision == "aborted":
                        return Outcome.ABORTED
                    if decision == "fatal":
                        return Outcome.FATAL
                if decision == "file_changed" and self._file_changed(recording, generation):
                    assert self.file_changed_to is not None
                    if last is not None:
                        self._enqueue(session, last, final=False)
                    recording = Path(self.file_changed_to)
                    self.file_changed_to = None
                    session.captured_paths.append(str(recording))
                    sequence_offset = store.last_sequence + 1
                    time_offset = store.last_end
                    skip_through = store.last_sequence
                    failures, reached = 0, None  # counted per recording file
                    store.set_source(
                        recording, sequence_offset=sequence_offset, time_offset=time_offset
                    )
                    connect_count = self._obs_connect_count()
                    self.status.info(f"Continuando a captura em {recording.name}.")
                    continue
                self._queue_final(session, last, recording, sequence_offset, time_offset)
                return Outcome.ENDED
        finally:
            session.capturing = False
            self.update_state()

    def _capture_failed(self, recording: Path, failures: int) -> str:
        """Reports (pt-BR) that FFmpeg cannot get through ``recording``; returns the message."""
        if failures >= MAX_FAILED_RUNS:
            message = (
                f"O FFmpeg falhou {failures} vezes ao ler {recording.name} sem conseguir avançar "
                "na gravação. A captura desse arquivo foi interrompida (cada tentativa relia a "
                "gravação desde o início); o que já foi capturado é enviado e a sessão é "
                "encerrada. Confira se o arquivo pode ser lido e se há espaço livre em "
                f"{self.config.work_dir}."
            )
        else:
            message = (
                f"O FFmpeg não conseguiu ler {recording.name}; a captura desse arquivo foi "
                "interrompida."
            )
        self.status.error(message)
        return message

    def _queue_final(
        self,
        session: LiveSession,
        last: Segment | None,
        recording: Path,
        sequence_offset: int,
        time_offset: float,
    ) -> None:
        """The capture is over: its last segment is queued flagged final (or the last one
        queued is flagged), and where the capture of ``recording`` stopped is remembered."""
        if last is not None:
            self._enqueue(session, last, final=True)
        else:
            session.store.mark_last_final()
        self._remember_mark(session, recording, sequence_offset, time_offset)

    def _remember_mark(
        self, session: LiveSession, recording: Path, sequence_offset: int, time_offset: float
    ) -> None:
        """Remembers where the capture of ``recording`` stopped (see RecordingMark), also in
        the session's queue.json, for a run that stops before the session is ended."""
        segment = session.last_segment
        if segment is None or segment.sequence < sequence_offset:
            return  # nothing of this file was queued
        try:
            size = recording.stat().st_size
        except OSError:
            return
        mark = RecordingMark(
            sequence=segment.sequence - sequence_offset,
            start=segment.start - time_offset,
            segment_seconds=session.segment_seconds,
            size=size,
        )
        self.capture_marks[_path_key(recording)] = mark
        session.store.set_recording_mark(mark.to_json(recording))

    def _take_mark(self, recording: Path, segment_seconds: float) -> RecordingMark | None:
        """The mark of ``recording`` if a capture can go on from it: same segment length (the
        cuts are the same) and the file only grew since (it is the same recording)."""
        mark = self.capture_marks.pop(_path_key(recording), None)
        if mark is None or mark.segment_seconds != segment_seconds:
            return None
        try:
            size = recording.stat().st_size
        except OSError:
            return None
        return mark if size >= mark.size else None

    async def _consume(
        self, session: LiveSession, segmenter: Segmenter, *, held: Segment | None = None
    ) -> tuple[Segment | None, Outcome | None]:
        """Queues every segment except the last one of the run, which is returned.

        ``held`` is the last segment of an earlier run that ended when the recording stopped
        growing (paused). This run reads the recording from the start again and re-creates
        that segment, longer, when it gets there: only then is the old copy deleted. If the
        run ends before (stopped, failed), the old copy is still the newest data and is
        returned as the last segment.

        A graceful stop ("q") is held back while FFmpeg is still re-reading what was already
        sent (after a resume or a pause): it would close a segment that is thrown away and the
        tail of the live would be lost. Ctrl+C twice still stops at once."""
        last: Segment | None = None
        interrupted: Outcome | None = None
        resume_after = held.sequence if held is not None else segmenter.skip_through

        async def watch() -> None:
            nonlocal interrupted
            stop_sent = False
            catch_up_noted = False
            while segmenter.running:
                if self.aborted:
                    interrupted = Outcome.ABORTED
                    await segmenter.terminate()
                    return
                if session.fatal is not None:
                    interrupted = Outcome.FATAL
                    await segmenter.terminate()
                    return
                if self.stopping and not stop_sent:
                    if segmenter.listed_through >= resume_after:
                        stop_sent = True
                        segmenter.request_stop()
                        self.status.info(
                            "Finalizando o último segmento (se a gravação já parou, o FFmpeg "
                            f"pode levar até {fmt_number(3 * self.config.rw_timeout, 0)} s)."
                        )
                    elif not catch_up_noted:
                        catch_up_noted = True
                        self.status.info(
                            "O FFmpeg ainda está relendo a parte da gravação que já foi "
                            "enviada; o último segmento é finalizado logo depois dela."
                        )
                await self._wait_changed(0.5)

        watcher = asyncio.create_task(watch(), name="segmenter-watch")
        try:
            async for segment in segmenter.segments():
                if held is not None and segment.sequence >= held.sequence:
                    if (
                        segment.sequence == held.sequence
                        and segment.last
                        and segment.end < held.end
                    ):
                        # The run ended inside it: the old copy holds more of the recording.
                        with contextlib.suppress(OSError):
                            segment.path.unlink()
                        last, held = held, None
                        continue
                    with contextlib.suppress(OSError):
                        held.path.unlink()  # superseded by this run's longer copy
                    held = None
                if segment.last:
                    last = segment
                else:
                    self._enqueue(session, segment, final=False)
        finally:
            if not watcher.done():
                watcher.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await watcher
            await segmenter.close()
        if interrupted is not None:
            return None, interrupted  # a held copy stays on disk; the next run re-creates it
        if last is None and held is not None:
            last = held  # the run never got to it
        return last, None

    def _after_run(self, recording: Path, obs_mode: bool, generation: int) -> str:
        if not obs_mode or self.stopping:
            return "final"
        if self._file_changed(recording, generation):
            return "file_changed"
        if self.record_generation != generation:
            # That recording was stopped. OBS may already record the next one (started while
            # FFmpeg was still draining this file): that one gets its own session.
            return "final"
        if self.obs_recording:
            # Paused or stalled - or OBS is unreachable (crashed, restarting): wait, bounded,
            # until it can tell whether this recording goes on (see _wait_stalled).
            return "stalled"
        return "final"

    def _file_changed(self, recording: Path, generation: int) -> bool:
        """OBS went on writing this recording in another file (split). A file change of a
        later recording (started while this file was still drained) is not this one's."""
        return bool(
            self.file_changed_to
            and self.file_changed_generation == generation
            and not _same_path(self.file_changed_to, recording)
        )

    def _obs_connect_count(self) -> int:
        return self.obs.connect_count if self.obs is not None else 0

    def _split_scan_needed(self, connect_count: int) -> bool:
        """Whether to look in the OBS folder for the next file of a split recording: only when
        OBS may not report it (RecordFileChanged needs obs-websocket 5.5) or when the connection
        dropped since this file was resolved - before its capture started, too (the event may
        have been missed - and then also for a new recording started meanwhile)."""
        if self.obs is None:
            return False
        if self.obs.connect_count != connect_count:
            return True
        version = _version_tuple(self.obs_version.get("obsWebSocketVersion"))
        return version < RECORD_FILE_CHANGED_SINCE

    async def _wait_stalled(
        self, session: LiveSession, recording: Path, generation: int, connect_count: int
    ) -> str:
        """OBS still records but the file stopped growing (paused, stalled), or OBS cannot be
        reached (crashed, restarting) and may tell, once it is back, that the recording goes
        on - or that it is recording another file (see _check_recording_after_reconnect).
        OBS unreachable for more than ``stall_obs_timeout``: the capture ends."""
        if self.obs_connected:
            self.status.warn(
                "O arquivo da gravação parou de crescer (gravação pausada?). A captura continua "
                "quando ele voltar a crescer."
            )
        else:
            self.status.warn(
                "O arquivo da gravação parou de crescer e o OBS está desconectado; aguardando o "
                f"OBS voltar (até {fmt_number(self.stall_obs_timeout, 0)} s) para saber se a "
                "gravação continua."
            )
        try:
            size = recording.stat().st_size
        except OSError:
            return "final"
        disconnected_since: float | None = None
        next_scan = time.monotonic() + self.split_scan_seconds
        while True:
            if self.aborted:
                return "aborted"
            if session.fatal is not None:
                return "fatal"
            if self.stopping:
                return "final"
            if self._file_changed(recording, generation):
                return "file_changed"
            if self.record_generation != generation:
                return "final"  # stopped (and maybe a new recording started)
            if self.obs_connected:
                disconnected_since = None
                if self.obs_synced and not self.obs_recording:
                    return "final"
            else:
                disconnected_since = disconnected_since or time.monotonic()
                if time.monotonic() - disconnected_since > self.stall_obs_timeout:
                    return "final"
            try:
                grown = recording.stat().st_size > size
            except OSError:
                return "final"
            if grown:
                # OBS appends the index when it stops: give the stop event a moment to arrive.
                await self._wait_changed(2.0)
                if self.record_generation != generation or (
                    self.obs_connected and not self.obs_recording
                ):
                    return "final"
                return "grew"
            if (
                self.obs_connected
                and self.obs_synced
                and time.monotonic() >= next_scan
                and self._split_scan_needed(connect_count)
            ):
                next_scan = time.monotonic() + self.split_scan_seconds
                # A recording can only start unseen when events were missed (reconnect).
                reconnected = self._obs_connect_count() != connect_count
                found = await self._find_next_recording(session, recording, new_allowed=reconnected)
                if found is not None and self.record_generation == generation:
                    candidate, split = found
                    if not split:
                        self._new_recording_found(candidate)
                        return "final"
                    self.file_changed_to = str(candidate)
                    self.file_changed_generation = generation
                    self.record_path = str(candidate)
                    self.status.info(f"OBS passou a gravar em um novo arquivo: {candidate}")
                    return "file_changed"
            await self._wait_changed(1.0)

    async def _find_next_recording(
        self, session: LiveSession, current: Path, *, new_allowed: bool = False
    ) -> tuple[Path, bool] | None:
        """A newer file that keeps growing in the OBS recording folder while ``current`` does
        not: ``(path, True)`` for the continuation of a split recording (needed when
        RecordFileChanged was missed or is not supported); with ``new_allowed``,
        ``(path, False)`` for a new recording OBS started while its events were missed.

        OBS creates the next file of a split right when it stops writing the current one, in
        the same folder and format; a recording started after OBS was restarted is created
        later, in the same format. Only such files are considered (same extension, created
        around or after the current file's last write, not a Replay Buffer save, growing), so
        that a replay save, an automatic remux or a Game Bar capture is never taken for the
        live. Without creation times (some Linux file systems) every candidate counts as a
        split: the capture goes on in the same session rather than losing anything."""
        obs = self.obs
        if obs is None:
            return None
        try:
            directory = await obs.get_record_directory()
        except ObsError:
            return None
        if not directory:
            return None
        exclude = (
            self.unusable_recordings
            | self.finished_recordings
            | {_path_key(path) for path in session.captured_paths}
            | {_path_key(current)}
        )
        try:
            current_mtime = current.stat().st_mtime
        except OSError:
            return None
        suffix = current.suffix.lower()
        kinds: dict[Path, bool] = {}  # candidate -> is a split, newest first
        for candidate in recent_recordings(Path(directory), exclude=exclude):
            if len(kinds) >= 5:
                break
            if candidate.suffix.lower() != suffix:
                continue  # OBS keeps the container when it splits a recording
            if candidate.name.lower().startswith(REPLAY_BUFFER_PREFIX):
                continue
            try:
                stat = candidate.stat()
            except OSError:
                continue
            if stat.st_mtime < current_mtime:
                continue
            created = _created_time(stat)
            if created is None or abs(created - current_mtime) <= self.split_window_seconds:
                kinds[candidate] = True
            elif new_allowed and created > current_mtime:
                kinds[candidate] = False
        grown = await self._growing(list(kinds))
        return next(((path, kinds[path]) for path in kinds if path in grown), None)

    def _enqueue(self, session: LiveSession, segment: Segment, *, final: bool) -> None:
        if segment.duration <= 0:
            with contextlib.suppress(OSError):
                segment.path.unlink()
            if final:
                session.store.mark_last_final()
            return
        session.uploader.enqueue(segment, final=final)
        session.last_segment = segment
        self.status.info(
            f"Segmento {segment.sequence} pronto ({fmt_clock(segment.start)}-"
            f"{fmt_clock(segment.end)}, {fmt_seconds(segment.duration)})"
            + (" · último" if final else "")
        )
        session.heartbeat.poke()
        self.update_state()

    # -- helpers -------------------------------------------------------------------------------

    async def _with_retry(self, call: Callable[[], Awaitable[dict[str, Any]]]) -> dict[str, Any]:
        """Retries network errors / 5xx forever (abortable with a second Ctrl+C)."""
        delay = self.api_backoff[0]
        while True:
            self._check_abort()
            try:
                return await call()
            except RetryableError as exc:
                self.status.warn(
                    f"offline: {exc.message}. Nova tentativa em {fmt_number(delay, 0)} s."
                )
            await self._sleep_or_abort(delay)
            delay = min(delay * 2, self.api_backoff[1])

    def _check_abort(self) -> None:
        if self.aborted:
            raise _Exit(EXIT_ABORTED)

    async def _wait_changed(self, timeout: float) -> None:
        self._changed.clear()
        with contextlib.suppress(TimeoutError):
            await asyncio.wait_for(self._changed.wait(), timeout)

    async def _sleep_or_stop(self, delay: float, *, wake: Callable[[], bool] | None = None) -> None:
        deadline = time.monotonic() + delay
        while time.monotonic() < deadline:
            if self.stopping:
                raise _Exit(EXIT_ABORTED if self.aborted else self.exit_code)
            if wake is not None and wake():
                return
            await self._wait_changed(min(1.0, deadline - time.monotonic()))

    async def _idle(self, seconds: float) -> None:
        """Waits ``seconds`` (less when stopping). Always suspends, and keeps waiting even when
        state changes are notified continuously: a loop calling it can never spin."""
        deadline = time.monotonic() + seconds
        while not self.stopping:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return
            await self._wait_changed(remaining)

    async def _sleep_or_abort(self, delay: float) -> None:
        deadline = time.monotonic() + delay
        while time.monotonic() < deadline:
            self._check_abort()
            await self._wait_changed(min(1.0, deadline - time.monotonic()))

    def _lock_instance(self) -> bool:
        """One companion per device and work directory: a second one would take the first
        one's live session for a leftover of a crashed run and end it."""
        lock = FileLock(self.config.work_dir / f"companion-{self._device_slug()}.lock")
        try:
            acquired = lock.acquire()
        except OSError as exc:
            self.status.error(
                f"Não foi possível usar a pasta de trabalho {self.config.work_dir}: {exc}"
            )
            return False
        if not acquired:
            self.status.error(
                "Outro companion já está rodando com este token e a mesma pasta de trabalho "
                f"({self.config.work_dir}). Feche o outro (ou espere ele terminar) antes de "
                "iniciar um novo."
            )
            return False
        self._instance_lock = lock
        return True

    def _device_slug(self) -> str:
        return re.sub(r"[^A-Za-z0-9_-]", "_", self.device_id or "default")[:64]

    def _lock_store(self, store: SessionStore) -> bool:
        """Locks a session directory for this process (False when another companion has it)."""
        key = _path_key(store.directory)
        if key in self._store_locks:
            return True
        lock = FileLock(store.directory / SESSION_LOCK_FILE)
        try:
            acquired = lock.acquire()
        except OSError as exc:
            log.warning("cannot lock session %s: %s", store.session_id, exc)
            return False
        if acquired:
            self._store_locks[key] = lock
        return acquired

    def _unlock_store(self, store: SessionStore) -> None:
        lock = self._store_locks.pop(_path_key(store.directory), None)
        if lock is not None:
            lock.release()

    def _release_locks(self) -> None:
        for lock in self._store_locks.values():
            lock.release()
        self._store_locks.clear()
        if self._instance_lock is not None:
            self._instance_lock.release()
            self._instance_lock = None

    def _spawn(self, coro: Awaitable[Any]) -> None:
        task = asyncio.ensure_future(coro)
        self._background.add(task)
        task.add_done_callback(self._background.discard)

    def _set_signal_handlers(self) -> dict[int, Any]:
        loop = asyncio.get_running_loop()

        def handler(signum: int, frame: object) -> None:
            loop.call_soon_threadsafe(self.request_stop)

        previous: dict[int, Any] = {}
        for name in ("SIGINT", "SIGTERM", "SIGBREAK"):
            sig = getattr(signal, name, None)
            if sig is None:
                continue
            try:
                previous[sig] = signal.signal(sig, handler)
            except (ValueError, OSError):  # not the main thread
                continue
        return previous

    @staticmethod
    def _restore_signal_handlers(previous: dict[int, Any]) -> None:
        for sig, handler in previous.items():
            with contextlib.suppress(ValueError, OSError, TypeError):
                signal.signal(sig, handler)
