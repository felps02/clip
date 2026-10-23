"""Executes one claimed job end-to-end and reports to the API.

``analyze_source``: download -> probe (/probe) -> analysis -> /candidates -> render the top
``autoRenderTop``, uploading each clip and its thumbnail as soon as it is rendered (then
deleting them locally, so ``WORK_DIR`` holds the source plus one clip at a time) -> upload
transcript.json, signals.json -> /clips -> /analysis -> /complete.  ``render_clip``:
download (cached; for a clip of a live project the segments overlapping it, joined with the
concat demuxer) -> transcript -> render -> upload -> /render-result -> /complete.
``live_segment``: download the segment and the previous one -> /probe (the segment) ->
analysis of the previous tail + the segment on the session's grid, keeping the peaks of the
commit rule (``app.analysis.live``) -> /candidates in session times -> render from the
window file -> artifacts -> /clips -> /analysis with ``summary.liveContext`` -> /complete
with the segment's duration.

Status protocol: the claim itself moves the job ``queued -> preparing_media``, so the
worker only POSTs ``/status`` for the *next* transitions (``analyzing``,
``detecting_moments``, ``generating_clips``, ``finalizing``); progress/message updates
within a status go through ``/heartbeat`` (which also extends the lease and reports
``cancelRequested``).

Errors: user/media errors -> ``/fail retryable=false`` (``invalid_media`` /
``render_failed``, charging the processed seconds); a failed transfer with the storage
(source download, transcript download is only a warning, clip/thumbnail/analysis upload:
HTTP errors and network errors after the retries) -> ``/fail code=transfer_failed
retryable=true`` with nothing charged — except the upload of one auto-rendered clip after
the storage accepted an earlier clip of the job, which is dropped with an error event like
a failed render (a second failure in a row is the storage's: ``transfer_failed``); a
missing ffmpeg/ffprobe binary -> ``/fail code=worker_error retryable=true`` (a deployment
problem, never the user's clip);
unexpected -> ``/fail retryable=true code=worker_error``; cancellation -> ``/status
cancelled`` (nothing is posted for a lost lease or a shutdown: the job is no longer
ours). A cancel wins over any error it provokes (killed ffmpeg, aborted transfer), and
the heartbeat keeps extending the lease while a cancelled job unwinds. The work directory
is always removed; directories left behind by a crashed worker are swept when the worker
starts (:meth:`JobRunner.sweep_stale_work_dirs`), which only ever removes directories that
carry the marker :meth:`JobRunner.run` writes when it creates them (``.clipforge-job``),
and again after every job for the directories this very worker created
(``WORK_DIR_ORPHAN_MINUTES``).
"""

from __future__ import annotations

import contextlib
import json
import math
import os
import shutil
import stat
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import numpy as np

from app.analysis.fusion import Candidate
from app.analysis.live import (
    WindowPlan,
    commit_range,
    contiguous,
    fixed_language,
    next_language,
    own_seconds,
    plan_window,
    post_roll_seconds,
    sample_seed,
    seconds_before,
    session_transcript_json,
    to_session_times,
    update_sample,
)
from app.analysis.pipeline import AnalysisOptions, AnalysisResult, Analyzer, WindowOptions
from app.analysis.transcription import Transcript, Word
from app.api_client import (
    EVENT_MESSAGE_MAX,
    ApiConflictError,
    ApiError,
    ApiUnavailableError,
    InternalApiClient,
    TransferAborted,
    TransferError,
)
from app.config import Settings
from app.logging_setup import get_logger
from app.media.concat import SegmentTimeline, concat_copy
from app.media.ffmpeg import FfmpegError, FfmpegNotFound, FfmpegTimeout, FfmpegTools
from app.media.probe import ProbeResult, probe_file
from app.models import (
    SAFE_JOB_ID_RE,
    AnalysisReport,
    AnalysisSummary,
    ClipReport,
    FramingSettings,
    JobPayload,
    RenderInfo,
    RenderResultReport,
    UploadFileSpec,
    UploadTarget,
    WebcamRegion,
)
from app.render.renderer import Renderer, RenderOutput, RenderRequest
from app.worker.errors import (
    CancelToken,
    JobCancelled,
    JobError,
    JobRejected,
    MediaError,
    RenderError,
)
from app.worker.heartbeat import CANCELLING_MESSAGE, HeartbeatThread, ProgressState

log = get_logger("runner")

EXT_WHITELIST = {
    "mp4",
    "mov",
    "mkv",
    "webm",
    "mp3",
    "wav",
    "m4a",
    "ts",
    "flv",
    "aac",
    "ogg",
    "avi",
    "mpg",
    "mpeg",
    "m4v",
}
EXT_BY_CONTENT_TYPE = {
    "video/mp4": "mp4",
    "video/quicktime": "mov",
    "video/x-matroska": "mkv",
    "video/webm": "webm",
    "audio/mpeg": "mp3",
    "audio/wav": "wav",
    "audio/x-wav": "wav",
    "audio/mp4": "m4a",
}
PROGRESS_MIN_INTERVAL = 2.0  # the UI polls every 2 s
ERROR_FRAGMENT_MAX = 400  # technical error text quoted inside an event message
LAYOUT_LABELS = {
    "letterbox": "com bordas",
    "center": "recorte central",
    "webcam-split": "webcam + jogo",
}
# Written into WORK_DIR/{jobId} right after the runner creates it: the proof that the
# directory is a job directory of this service. The sweep of leftover directories never
# removes a directory without it (WORK_DIR may point at a folder shared with other data).
WORK_DIR_MARKER = ".clipforge-job"
WORK_DIR_MARKER_MAX_BYTES = 4096
# A job directory holds a few dozen entries; a bigger tree is never judged stale.
WORK_DIR_WALK_LIMIT = 10_000


MB = 1024 * 1024
RENDER_FILE_SUFFIXES = (".mp4", ".jpg", ".ass")  # what Renderer.render writes per clip
# A live segment's duration as the API knows it (from the companion's cut list) is where the
# next segment starts; a file whose own duration differs by more than this is laid out by
# its own duration instead.
DURATION_TOLERANCE = 1.0


@dataclass(slots=True)
class _UploadedClip:
    """An auto-rendered clip already in the storage (its local files are gone)."""

    rank: int
    output: RenderOutput  # metadata only: the paths were deleted after the upload
    title: str
    video_key: str
    thumbnail_key: str | None


def _fragment(text: str | None, limit: int = ERROR_FRAGMENT_MAX) -> str:
    text = " ".join((text or "").split())
    return text if len(text) <= limit else text[: limit - 1] + "…"


def _transfer_detail(exc: TransferError) -> str:
    return f"HTTP {exc.status}" if exc.status else _fragment(str(exc), 200)


def _extension(file_name: str | None, content_type: str | None, default: str = "bin") -> str:
    """Extension of a downloaded file: the file name's when known, else the content type's."""
    ext = Path(file_name or "").suffix.lower().lstrip(".")
    if ext in EXT_WHITELIST:
        return ext
    return EXT_BY_CONTENT_TYPE.get((content_type or "").lower(), default)


def _same_streams(a: ProbeResult, b: ProbeResult) -> bool:
    """Two segments the concat demuxer can join without re-encoding: same video and audio
    formats (a recording restarted with other settings is not)."""
    return (
        a.has_video == b.has_video
        and a.has_audio == b.has_audio
        and (a.video_codec, a.width, a.height) == (b.video_codec, b.width, b.height)
        and (a.audio_codec, a.sample_rate, a.channels) == (b.audio_codec, b.sample_rate, b.channels)
    )


class JobContext:
    def __init__(
        self,
        payload: JobPayload,
        api: InternalApiClient,
        work_dir: Path,
        cancel: CancelToken,
        progress: ProgressState,
    ) -> None:
        self.payload = payload
        self.api = api
        self.work_dir = work_dir
        self.cancel = cancel
        self.progress = progress
        self.status: str | None = None
        self.processed_seconds = 0.0
        self._last_progress_call = 0.0
        self._last_sent: tuple[int, str] = (-1, "")
        self.log = log.bind(job_id=payload.job_id, kind=payload.kind)

    @property
    def job_id(self) -> str:
        return self.payload.job_id

    def check_cancel(self) -> None:
        self.cancel.raise_if_cancelled()

    def set_status(self, status: str, progress: int, message: str) -> None:
        """Report a state transition through ``/status`` (always sent).

        Only real transitions go here: the API rejects ``status -> same status`` with 409.
        """
        self.check_cancel()
        self.progress.set(progress, message)
        self.api.status(self.job_id, status, progress, message)
        self.status = status
        self._last_progress_call = time.monotonic()
        self._last_sent = (progress, message)
        self.log.info("job.status", status=status, progress=progress, message=message)

    def assume_status(self, status: str, progress: int, message: str) -> None:
        """Record a status the API already applied (the claim performs
        ``queued -> preparing_media``) without posting ``/status``; the progress and
        message are published through ``/heartbeat``."""
        self.check_cancel()
        self.status = status
        self.log.info("job.status", status=status, progress=progress, message=message)
        self.set_progress(progress, message, force=True)

    def set_progress(self, progress: int, message: str, *, force: bool = False) -> None:
        """Update progress within the current status.

        Throttled ``/heartbeat`` calls (never ``/status``: the status does not change) keep
        the UI fresh and extend the lease; the answer is honoured like the heartbeat
        thread does (``cancelRequested`` -> cancel, ``409`` -> the lease is lost).
        """
        self.check_cancel()
        self.progress.set(progress, message)
        now = time.monotonic()
        changed = (progress, message) != self._last_sent
        if not changed or not (force or now - self._last_progress_call >= PROGRESS_MIN_INTERVAL):
            return
        self._last_progress_call = now
        self._last_sent = (progress, message)
        try:
            response = self.api.heartbeat(self.job_id, progress, message)
        except ApiConflictError as exc:
            self.log.warning("job.lease_lost", detail=exc.detail)
            self.cancel.cancel("lease_lost")
        except ApiError as exc:
            self.log.warning("job.progress_failed", error=str(exc)[:200])
        else:
            if response.cancel_requested:
                self.log.info("job.cancel_requested")
                self.progress.set(message=CANCELLING_MESSAGE)
                self.cancel.cancel("cancel_requested")
        self.check_cancel()

    def event(self, level: str, message: str, data: dict[str, Any] | None = None) -> None:
        """Best-effort job event; the message is bounded to the API's 1000 characters (a
        longer one would be rejected with 422 and the event lost)."""
        if len(message) > EVENT_MESSAGE_MAX:
            message = message[: EVENT_MESSAGE_MAX - 1] + "…"
        try:
            self.api.event(self.job_id, level, message, data)
        except ApiError as exc:
            self.log.warning("job.event_failed", error=str(exc)[:200])
        getattr(self.log, "warning" if level != "info" else "info")(
            "job.event", level=level, message=message, data=data
        )


class JobRunner:
    def __init__(
        self,
        settings: Settings,
        api: InternalApiClient,
        tools: FfmpegTools,
        *,
        analyzer: Analyzer | None = None,
        renderer: Renderer | None = None,
    ) -> None:
        self.settings = settings
        self.api = api
        self.tools = tools
        self.analyzer = analyzer or Analyzer(settings, tools)
        self.renderer = renderer or Renderer(tools, settings.fonts_dir)
        self._current: JobContext | None = None
        self._lock = threading.Lock()

    # ------------------------------------------------------------------ public ----
    @property
    def current_job_id(self) -> str | None:
        ctx = self._current
        return ctx.job_id if ctx else None

    def abort_current(self, reason: str = "shutdown") -> None:
        ctx = self._current
        if ctx is not None:
            ctx.cancel.cancel(reason)

    def run(self, payload: JobPayload) -> str:
        """Run one job.

        Returns ``completed`` / ``cancelled`` / ``failed`` / ``rejected`` / ``api_error``.
        """
        work_dir = self.job_work_dir(payload.job_id)
        if work_dir is None:
            # An id that is not a plain path segment never reaches a URL or the file system:
            # the lease simply expires and the API's reaper deals with the job.
            log.error("job.invalid_id", job_id=payload.job_id[:120])
            return "failed"
        try:
            work_dir.mkdir(parents=True, exist_ok=True)
        except OSError as exc:
            return self._work_dir_failed(payload.job_id, exc)
        try:
            _write_work_dir_marker(work_dir, payload.job_id, self.api.worker_id)
        except OSError as exc:
            # An unmarked directory would never be swept after a crash: do not keep one
            # (removing it also frees the space when the disk is full).
            self._remove_work_dir(work_dir)
            return self._work_dir_failed(payload.job_id, exc)
        cancel = CancelToken()
        progress = ProgressState()
        ctx = JobContext(payload, self.api, work_dir, cancel, progress)
        heartbeat = HeartbeatThread(
            self.api,
            payload.job_id,
            payload.lease_seconds,
            progress,
            cancel,
            on_beat=lambda: _touch(work_dir),
        )
        with self._lock:
            self._current = ctx
        heartbeat.start()
        started = time.monotonic()
        outcome = "failed"
        try:
            try:
                self._execute(ctx)
            except JobCancelled, JobRejected:
                raise
            except Exception as exc:
                # A cancel (user, lost lease, shutdown) kills ffmpeg and aborts transfers,
                # which surfaces as all sorts of errors: the cancellation is the outcome.
                if cancel.cancelled:
                    raise JobCancelled(cancel.reason or "cancel_requested") from exc
                raise
            outcome = "completed"
        except JobCancelled as exc:
            outcome = "cancelled"
            ctx.log.info("job.cancelled", reason=exc.reason)
            if exc.reason == "cancel_requested":
                self._safe(
                    lambda: self.api.status(
                        payload.job_id, "cancelled", None, "Cancelado pelo usuário"
                    )
                )
        except JobRejected as exc:
            outcome = "rejected"
            ctx.log.warning("job.rejected", code=exc.code, detail=exc.detail)
        except TransferError as exc:
            # A signed-URL transfer failed (source download, clip/thumbnail/analysis upload;
            # HTTP error or network error after the retries): a storage/infrastructure
            # problem, never a media error and never charged to the user. Always retryable:
            # a fresh claim of the re-queued job issues new signed URLs, and the API bounds
            # the attempts.
            ctx.log.warning("job.transfer_failed", error=str(exc)[:300], status=exc.status)
            self._safe_fail(
                payload.job_id,
                "transfer_failed",
                "Falha na transferência de arquivos com o armazenamento: " + str(exc),
                retryable=True,
            )
        except FfmpegNotFound as exc:
            self._safe_fail(
                payload.job_id, "worker_error", f"FFmpeg indisponível: {exc}", retryable=True
            )
        except FfmpegTimeout as exc:
            self._safe_fail(
                payload.job_id,
                "worker_error",
                f"Tempo limite do FFmpeg excedido: {exc}",
                retryable=True,
            )
        except FfmpegError as exc:
            code = (
                "render_failed"
                if ctx.status in ("generating_clips", "finalizing")
                else "invalid_media"
            )
            message = (
                "Falha ao renderizar o clipe: "
                if code == "render_failed"
                else "Não foi possível decodificar o arquivo: "
            ) + str(exc)
            ctx.log.warning("job.ffmpeg_error", error=str(exc)[:300], stderr=exc.stderr[-800:])
            self._safe_fail(
                payload.job_id, code, message, retryable=False, processed=ctx.processed_seconds
            )
        except JobError as exc:
            ctx.log.warning("job.failed", code=exc.code, retryable=exc.retryable, error=exc.message)
            self._safe_fail(
                payload.job_id,
                exc.code,
                exc.message,
                retryable=exc.retryable,
                processed=ctx.processed_seconds,
            )
        except ApiConflictError as exc:
            outcome = "api_error"
            ctx.log.error("job.api_conflict", code=exc.code, detail=exc.detail)
        except ApiError as exc:
            outcome = "api_error"
            ctx.log.error("job.api_error", error=str(exc)[:300])
        except Exception as exc:
            ctx.log.exception("job.crashed")
            self._safe_fail(
                payload.job_id, "worker_error", f"{type(exc).__name__}: {exc}"[:500], retryable=True
            )
        finally:
            heartbeat.stop()
            with self._lock:
                self._current = None
            self._remove_work_dir(work_dir)
            ctx.log.info(
                "job.finished", outcome=outcome, seconds=round(time.monotonic() - started, 1)
            )
            self._sweep_after_job()
        return outcome

    def _sweep_after_job(self) -> None:
        """Remove the leftovers of this worker's earlier jobs (a directory ``_remove_tree``
        could not delete because a file was still locked, a crash just before a restart)
        without waiting for the next start: only directories whose marker names this
        worker, never the current job's, unchanged for ``WORK_DIR_ORPHAN_MINUTES``."""
        minutes = float(self.settings.work_dir_orphan_minutes)
        if minutes <= 0:
            return
        try:
            self.sweep_stale_work_dirs(minutes * 60.0, worker_id=self.api.worker_id)
        except Exception:
            log.exception("workdir.sweep_failed")

    def _execute(self, ctx: JobContext) -> None:
        kind = ctx.payload.kind
        if kind == "analyze_source":
            self._analyze_source(ctx)
        elif kind == "render_clip":
            self._render_clip(ctx)
        elif kind == "live_segment":
            self._live_segment(ctx)
        else:
            raise JobError(
                f"tipo de job não suportado: {kind}", code="unsupported_kind", retryable=False
            )

    # ------------------------------------------------------------------ work dir ----
    def job_work_dir(self, job_id: str) -> Path | None:
        """``WORK_DIR/{jobId}`` when the id is safe and the directory is a direct child of
        ``WORK_DIR`` once resolved; ``None`` otherwise (never mkdir/rmtree elsewhere)."""
        if not SAFE_JOB_ID_RE.match(job_id):
            return None
        root = self.settings.work_path
        candidate = root / job_id
        try:
            if candidate.resolve().parent != root.resolve():
                return None
        except OSError:
            return None
        return candidate

    def _remove_work_dir(self, work_dir: Path) -> None:
        if self.job_work_dir(work_dir.name) != work_dir:  # defence in depth before rmtree
            log.error("workdir.refused", path=str(work_dir))
            return
        _remove_tree(work_dir)

    def _work_dir_failed(self, job_id: str, exc: OSError) -> str:
        """The work directory cannot be created or marked: a worker problem (full disk,
        permissions), never the user's media, so the job fails retryable."""
        log.error("job.workdir_failed", job_id=job_id, error=str(exc)[:200])
        self._safe_fail(
            job_id,
            "worker_error",
            f"Não foi possível criar o diretório de trabalho: {exc}"[:500],
            retryable=True,
        )
        return "failed"

    def sweep_stale_work_dirs(
        self, max_age_seconds: float | None = None, *, worker_id: str | None = None
    ) -> list[str]:
        """Remove job directories that a worker which died mid-job (crash, OOM, host
        restart) left in ``WORK_DIR``; called when the worker starts (any worker's
        directories, ``WORK_DIR_STALE_HOURS``) and after every job (``worker_id``: only the
        directories whose marker names that worker, ``WORK_DIR_ORPHAN_MINUTES``).

        Only directories this service created are candidates: a direct child of
        ``WORK_DIR`` (not a link or junction) named like a job id that holds the
        ``.clipforge-job`` marker :meth:`run` writes for that very job id. A name proves
        nothing (``storage``, ``tools``, ``models`` or a GUID-named folder all match the
        job id pattern), so nothing unmarked is ever touched: other files, the data of
        other software when ``WORK_DIR`` points at a shared folder, directories of workers
        older than the marker. A marked directory is removed only when nothing in it
        changed for ``WORK_DIR_STALE_HOURS`` (default 6 h; 0 disables the sweep); a tree
        that cannot be walked completely (more than ``WORK_DIR_WALK_LIMIT`` entries, an
        unreadable subdirectory, an entry vanishing during the walk) counts as changed just
        now. A running job refreshes its directory's mtime on every heartbeat, so a worker
        starting on a ``WORK_DIR`` shared with another one never removes a live job's
        files. Returns the names of the removed directories.
        """
        if max_age_seconds is None:
            max_age_seconds = float(self.settings.work_dir_stale_hours) * 3600.0
        if max_age_seconds <= 0:
            return []
        try:
            entries = list(os.scandir(self.settings.work_path))
        except OSError:  # no WORK_DIR yet: nothing to sweep
            return []
        now = time.time()
        current = self.current_job_id
        removed: list[str] = []
        for entry in entries:
            name = entry.name
            if name == current or not SAFE_JOB_ID_RE.match(name):
                continue
            try:
                if not entry.is_dir(follow_symlinks=False) or entry.is_junction():
                    continue
            except OSError:
                continue
            if not _is_marked_work_dir(Path(entry.path), name, worker_id):
                continue  # not created by this service's runner: never ours to remove
            try:
                newest = _newest_mtime(Path(entry.path), WORK_DIR_WALK_LIMIT)
            except OSError:
                continue
            if math.isinf(newest):
                log.warning("workdir.sweep_skipped", dir=name, reason="incomplete_walk")
                continue
            if now - newest < max_age_seconds:
                continue
            path = self.job_work_dir(name)
            if path is None:
                continue
            _remove_tree(path)
            if not path.exists():
                removed.append(name)
        if removed:
            log.info("workdir.swept", count=len(removed), dirs=removed[:20])
        return removed

    # ------------------------------------------------------------------ helpers ----
    def _safe(self, fn: Callable[[], Any]) -> None:
        """Run a final report; a failure to report must not mask the job outcome."""
        try:
            fn()
        except ApiError as exc:
            log.warning("job.report_failed", error=str(exc)[:200])
        except Exception as exc:
            log.warning("job.report_failed", error=f"{type(exc).__name__}: {exc}"[:200])

    def _safe_fail(
        self,
        job_id: str,
        code: str,
        message: str,
        *,
        retryable: bool,
        processed: float | None = None,
    ) -> None:
        self._safe(
            lambda: self.api.fail(
                job_id, code, message, retryable=retryable, processed_seconds=processed
            )
        )

    def _source_path(self, ctx: JobContext) -> Path:
        src = ctx.payload.media
        return ctx.work_dir / f"source.{_extension(src.file_name, src.content_type)}"

    def _download_source(
        self,
        ctx: JobContext,
        *,
        progress_range: tuple[int, int],
        message: str = "Baixando arquivo de origem…",
    ) -> Path:
        src = ctx.payload.media
        return self._download(
            ctx,
            src.download_url,
            self._source_path(ctx),
            size=src.size_bytes,
            progress_range=progress_range,
            message=message,
            what="o arquivo de origem",
        )

    def _download(
        self,
        ctx: JobContext,
        url: str,
        dest: Path,
        *,
        size: int | None,
        progress_range: tuple[int, int],
        message: str,
        what: str,
    ) -> Path:
        """Stream a signed URL to ``dest`` with progress; ``what`` names the file in the
        error ("não foi possível baixar o arquivo de origem (HTTP 404)…")."""
        if dest.exists() and size and dest.stat().st_size == size:
            ctx.log.info("download.cached", path=dest.name, size=size)
            return dest
        lo, hi = progress_range
        ctx.set_progress(lo, message, force=True)
        last = [-1]

        def on_progress(written: int, total: int | None) -> None:
            total_bytes = total or size
            if total_bytes:
                pct = lo + int((hi - lo) * min(1.0, written / total_bytes))
                if pct != last[0]:
                    last[0] = pct
                    ctx.set_progress(pct, message)

        try:
            self.api.download_to_file(
                url,
                dest,
                progress=on_progress,
                should_stop=lambda: ctx.cancel.cancelled,
                expected_size=size,
                on_cancel=ctx.cancel.on_cancel,
            )
        except TransferAborted:
            # Stopped by the cancel token: report the cancellation, never invalid_media.
            ctx.check_cancel()
            raise
        except TransferError as exc:
            # A storage problem (expired signature, missing object, 5xx, network...), never
            # a media error: ``transfer_failed``, retryable (a fresh claim signs new URLs).
            ctx.check_cancel()
            hint = (
                "; o arquivo não foi encontrado no armazenamento"
                if exc.status in (404, 410)
                else ""
            )
            raise TransferError(
                f"não foi possível baixar {what} ({_transfer_detail(exc)}){hint}",
                status=exc.status,
            ) from exc
        ctx.check_cancel()
        if not dest.exists() or dest.stat().st_size == 0:
            # "arquivo de origem vazio", "segmento anterior vazio"...
            raise MediaError(f"{what.removeprefix('o ')} vazio")
        return dest

    def _probe(self, ctx: JobContext, path: Path, *, min_duration: float = 0.5) -> ProbeResult:
        """ffprobe ``path``; a file of ``min_duration`` seconds or less is refused (a live
        segment only needs a duration: the last one of a session can be a sliver, analysed
        with the previous segment as its context)."""
        try:
            probe = probe_file(self.tools, path)
        except FfmpegNotFound:
            raise
        except FfmpegError as exc:
            raise MediaError(f"arquivo não reconhecido pelo ffprobe: {exc}") from exc
        if not probe.duration or probe.duration <= min_duration:
            raise MediaError("não foi possível determinar a duração do arquivo")
        return probe

    def _upload_all(
        self, ctx: JobContext, files: list[tuple[UploadFileSpec, Path]]
    ) -> dict[str, UploadTarget]:
        if not files:
            return {}
        targets = self.api.upload_urls(ctx.job_id, [spec for spec, _ in files])
        by_name = {t.file_name: t for t in targets}
        missing = [spec.file_name for spec, _ in files if spec.file_name not in by_name]
        if missing:
            raise JobError(
                f"API não retornou URL de upload para: {', '.join(missing)}",
                code="worker_error",
                retryable=True,
            )
        for spec, path in files:
            ctx.check_cancel()
            try:
                self.api.upload_file(
                    by_name[spec.file_name],
                    path,
                    spec.content_type,
                    should_stop=lambda: ctx.cancel.cancelled,
                )
            except TransferAborted:
                ctx.check_cancel()
                raise
            except TransferError as exc:
                ctx.check_cancel()
                raise TransferError(
                    f"não foi possível enviar {spec.file_name} ({_transfer_detail(exc)})",
                    status=exc.status,
                ) from exc
            ctx.log.info("upload.done", file=spec.file_name, bytes=path.stat().st_size)
        return by_name

    def _upload_clip(
        self, ctx: JobContext, rank: int, output: RenderOutput
    ) -> tuple[str, str | None]:
        """Upload one auto-rendered clip and its thumbnail (one ``/upload-urls`` call per
        clip; the endpoint is idempotent) and return their storage keys (thumbnail
        ``None`` when the render has none)."""
        name = f"clip-{rank:02d}"
        files: list[tuple[UploadFileSpec, Path]] = [
            (
                UploadFileSpec(
                    kind="clip_render",
                    file_name=f"{name}.mp4",
                    content_type="video/mp4",
                    size_bytes=output.size_bytes,
                ),
                output.path,
            )
        ]
        if output.thumbnail_path is not None:
            files.append(
                (
                    UploadFileSpec(
                        kind="thumbnail",
                        file_name=f"{name}.jpg",
                        content_type="image/jpeg",
                        size_bytes=output.thumbnail_path.stat().st_size,
                    ),
                    output.thumbnail_path,
                )
            )
        targets = self._upload_all(ctx, files)
        thumbnail = targets.get(f"{name}.jpg")
        return targets[f"{name}.mp4"].key, thumbnail.key if thumbnail else None

    # ------------------------------------------------------------------ analyze ----
    def _analyze_source(self, ctx: JobContext) -> None:
        payload = ctx.payload
        settings = payload.settings
        limits = payload.limits

        # The claim already moved the job to preparing_media (posting it again is a 409).
        ctx.assume_status("preparing_media", 0, "Preparando arquivo…")
        path = self._download_source(ctx, progress_range=(0, 8))
        ctx.set_progress(9, "Verificando o arquivo…", force=True)
        probe = self._probe(ctx, path)
        self._report_probe(ctx, probe)
        duration = float(probe.duration or 0.0)
        if limits.max_source_duration_seconds and duration > limits.max_source_duration_seconds + 1:
            raise MediaError("arquivo mais longo do que o permitido pelo plano")

        ctx.set_status("analyzing", 10, "Analisando áudio…")
        result = self.analyzer.analyze(
            path,
            probe,
            settings,
            limits,
            options=AnalysisOptions(
                transcription=True,
                webcam=True,
                llm=True,
                transcription_model=limits.transcription_model,
            ),
            progress=self._analysis_progress(ctx),
            cancel=ctx.cancel,
        )
        ctx.processed_seconds = duration
        if ctx.status != "detecting_moments":
            ctx.set_status("detecting_moments", 62, "Detectando momentos…")
        self._report_signals(ctx, result)
        ids = self._post_candidates(ctx, result.candidates)

        # ---- render the top N (autoRenderTop; absent = every candidate) ----------------
        uploaded = self._render_candidates(
            ctx,
            result.candidates[: min(settings.render_top, len(ids))],
            source=path,
            probe=probe,
            words=result.transcript.words if result.transcript.available else None,
            webcam_region=settings.framing.webcam_region or result.webcam_region,
            # suggested by the LLM provider (validated), else the window rating's, else the
            # title the user set
            title_for=lambda cand: (
                cand.title or result.llm.title_for(cand.start, cand.end) or settings.title
            ),
        )

        # ---- analysis artifacts (the clips are already in the storage) -------------------
        ctx.set_status("finalizing", 95, "Enviando resultados…")
        targets = self._upload_artifacts(ctx, result.transcript_json(), result.signals_json())
        ctx.set_progress(97, "Registrando clipes…", force=True)
        clips_rendered = self._register_clips(ctx, uploaded, result.candidates, ids)

        summary = AnalysisSummary(
            signals=result.availability(),
            webcam_region=result.webcam_region,
            language=result.transcript.language,
            processed_seconds=round(duration, 3),
            candidates_found=len(result.candidates),
            clips_rendered=clips_rendered,
            speech_detected=result.speech_detected,
        )
        self.api.analysis(
            ctx.job_id,
            AnalysisReport(
                transcript_key=targets["transcript.json"].key,
                signals_key=targets["signals.json"].key,
                summary=summary,
            ),
        )
        ctx.set_progress(99, "Concluindo…", force=True)
        self.api.complete(ctx.job_id, duration, summary)
        ctx.progress.set(100, "Concluído")

    # ------------------------------------------------------------------ shared steps ----
    def _report_probe(self, ctx: JobContext, probe: ProbeResult) -> None:
        """``/probe``: a 409 means the API already failed the job (quota, plan, invalid
        media) and the worker just stops; a file without video is refused here too."""
        try:
            self.api.probe(ctx.job_id, probe.to_report())
        except ApiConflictError as exc:
            raise JobRejected(exc.code, exc.detail) from exc
        if not probe.has_video:
            raise MediaError("arquivo sem vídeo")

    def _analysis_progress(self, ctx: JobContext) -> Callable[[str, int, str], None]:
        """Analyzer progress -> ``/status`` on a phase change, ``/heartbeat`` within it."""
        phase_status = {"analyzing": "analyzing", "detecting": "detecting_moments"}

        def on_progress(phase: str, pct: int, message: str) -> None:
            status = phase_status.get(phase, "analyzing")
            if status != ctx.status:
                ctx.set_status(status, pct, message)
            else:
                ctx.set_progress(pct, message)

        return on_progress

    def _report_signals(
        self,
        ctx: JobContext,
        result: AnalysisResult,
        *,
        language_hint: str = "Se estiver errado, escolha o idioma nas configurações e analise "
        "de novo",
    ) -> None:
        """Job events for what the user should know about the analysis: the analyzer's
        notices, unavailable or partially covered signals, no speech, an unsure language."""
        for level, message, data in result.notices:
            ctx.event(level, message, data)
        for name, value in result.availability().items():
            if name == "llm":
                # an optional provider: no event when none is configured; the assist stage
                # reported its own stop above (notices)
                if value.get("error") and result.assist is None:
                    ctx.event(
                        "warning",
                        f"Sinal 'llm' indisponível: {_fragment(value['error'])}",
                        {"signal": "llm"},
                    )
                continue
            if not value.get("available"):
                ctx.event(
                    "warning",
                    f"Sinal '{name}' indisponível: {_fragment(value.get('error') or 'desativado')}",
                    {"signal": name},
                )
            elif name == "transcript" and value.get("speechDetected") is False:
                ctx.event(
                    "info",
                    "Nenhuma fala detectada no áudio: sem idioma e sem legendas nos clipes",
                    {"signal": "transcript", "speechDetected": False},
                )
            elif value.get("coverage") is not None and float(value["coverage"]) < 1.0:
                coverage = float(value["coverage"])
                ctx.event(
                    "warning",
                    f"Sinal '{name}' analisado em apenas {coverage:.0%} da gravação: "
                    f"{_fragment(value.get('error'))}",
                    {"signal": name, "coverage": coverage},
                )
            if name == "transcript" and value.get("languageConfidence") == "low":
                probability = float(value.get("languageProbability") or 0.0)
                ctx.event(
                    "warning",
                    f"Idioma da fala identificado com baixa confiança: {value.get('language')} "
                    f"({probability:.0%}). {language_hint}",
                    {
                        "signal": "transcript",
                        "language": value.get("language"),
                        "languageProbability": probability,
                    },
                )

    def _post_candidates(self, ctx: JobContext, candidates: list[Candidate]) -> list[str]:
        inputs = [c.to_input() for c in candidates]
        ids = self.api.candidates(ctx.job_id, inputs)
        if len(ids) < len(inputs):
            ctx.event(
                "warning",
                "API retornou menos ids de candidatos do que o enviado",
                {"sent": len(inputs), "received": len(ids)},
            )
        ctx.set_progress(65, f"{len(inputs)} momentos encontrados", force=True)
        return ids

    def _render_candidates(
        self,
        ctx: JobContext,
        to_render: list[Candidate],
        *,
        source: Path,
        probe: ProbeResult,
        words: list[Word] | None,
        webcam_region: WebcamRegion | None,
        title_for: Callable[[Candidate], str | None],
        to_file: Callable[[float], float] | None = None,
    ) -> list[_UploadedClip]:
        """Render and upload the candidates the job renders (status ``generating_clips``).

        One failing render is an error event, never the end of the others. Each clip is
        uploaded as soon as it is rendered and deleted locally: with up to 50 clips per job,
        ``WORK_DIR`` never holds more than the source and one clip. ``to_file`` maps the
        candidates' times to the source file's (a live window starts before the session
        time of its own first second); ``words`` are already in the file's times."""
        settings = ctx.payload.settings
        limits = ctx.payload.limits
        ctx.set_status(
            "generating_clips",
            65,
            f"Gerando clipe 1 de {len(to_render)}…" if to_render else "Nenhum clipe para gerar",
        )
        uploaded: list[_UploadedClip] = []
        last_upload_failed = False
        clips_dir = ctx.work_dir / "clips"
        min_free_mb = int(self.settings.render_min_free_mb)
        for index, cand in enumerate(to_render):
            ctx.check_cancel()
            pct = 65 + int(30 * index / max(1, len(to_render)))
            ctx.set_progress(pct, f"Gerando clipe {index + 1} de {len(to_render)}…", force=True)
            free = _disk_free_bytes(ctx.work_dir) if min_free_mb > 0 else None
            if free is not None and free < min_free_mb * MB:
                skipped = len(to_render) - index
                ctx.event(
                    "warning",
                    "Espaço em disco insuficiente no servidor de processamento "
                    f"({free // MB} MB livres, mínimo {min_free_mb} MB): {skipped} de "
                    f"{len(to_render)} clipes não foram gerados. Gere-os pela página do projeto",
                    {
                        "freeMb": free // MB,
                        "minFreeMb": min_free_mb,
                        "skipped": skipped,
                        "candidateRanks": [c.rank for c in to_render[index:]],
                    },
                )
                break
            title = title_for(cand)
            request = RenderRequest(
                source=source,
                src_w=int(probe.width or 0),
                src_h=int(probe.height or 0),
                start=to_file(cand.start) if to_file is not None else cand.start,
                end=to_file(cand.end) if to_file is not None else cand.end,
                aspect=settings.aspect_ratio,
                max_render_height=limits.max_render_height,
                framing_mode=settings.framing.mode,
                focus_x=settings.framing.focus_x,
                webcam_region=webcam_region,
                allow_webcam=limits.allow_webcam_layout,
                subtitles_enabled=settings.subtitles.enabled,
                subtitle_style=settings.subtitles.style,
                subtitle_position=settings.subtitles.position,
                words=words,
                title=title,
                normalize_audio=True,
                has_audio=probe.has_audio,
                output_name=f"clip-{cand.rank:02d}",
            )
            span = f"{cand.start:.1f}s–{cand.end:.1f}s"
            try:
                output = self.renderer.render(request, clips_dir, cancel=ctx.cancel)
            except JobCancelled, FfmpegNotFound:
                raise  # a missing ffmpeg is a deployment problem: the job fails retryable
            except (FfmpegTimeout, FfmpegError, OSError, ValueError) as exc:
                # a partial output (killed encoder, full disk) must not keep taking space
                _discard_files(
                    *(clips_dir / f"{request.output_name}{ext}" for ext in RENDER_FILE_SUFFIXES)
                )
                ctx.event(
                    "error",
                    f"Falha ao renderizar o clipe {index + 1} ({span}): {_fragment(str(exc))}",
                    {"candidateRank": cand.rank, "error": str(exc)[:500]},
                )
                continue
            for warning in output.warnings:
                ctx.event(
                    "warning",
                    f"Clipe {index + 1}: {_fragment(warning)}",
                    {"candidateRank": cand.rank},
                )
            ctx.event(
                "info",
                _render_summary(f"Clipe {index + 1} renderizado", output),
                {
                    "candidateRank": cand.rank,
                    "layout": output.layout,
                    "width": output.width,
                    "height": output.height,
                    "hasSubtitles": output.has_subtitles,
                },
            )
            ctx.set_progress(pct, f"Enviando clipe {index + 1} de {len(to_render)}…")
            try:
                video_key, thumbnail_key = self._upload_clip(ctx, cand.rank, output)
            except TransferAborted:
                raise  # a cancellation, never a failed clip
            except TransferError as exc:
                ctx.check_cancel()
                if not uploaded or last_upload_failed:
                    # the storage has not accepted any clip of this job, or failed twice in
                    # a row: a storage problem -> transfer_failed (retryable), as before
                    raise
                last_upload_failed = True
                ctx.event(
                    "error",
                    f"Falha ao enviar o clipe {index + 1} ({span}): {_fragment(str(exc))}",
                    {"candidateRank": cand.rank, "error": str(exc)[:500]},
                )
                continue
            finally:
                _discard_files(output.path, output.thumbnail_path, output.ass_path)
            last_upload_failed = False
            uploaded.append(_UploadedClip(cand.rank, output, title or "", video_key, thumbnail_key))
        return uploaded

    def _upload_artifacts(
        self, ctx: JobContext, transcript: dict[str, Any], signals: dict[str, Any]
    ) -> dict[str, UploadTarget]:
        """Write and upload ``transcript.json`` and ``signals.json``."""
        analysis_dir = ctx.work_dir / "analysis"
        analysis_dir.mkdir(parents=True, exist_ok=True)
        transcript_path = analysis_dir / "transcript.json"
        signals_path = analysis_dir / "signals.json"
        transcript_path.write_text(json.dumps(transcript, ensure_ascii=False), encoding="utf-8")
        signals_path.write_text(json.dumps(signals, ensure_ascii=False), encoding="utf-8")
        return self._upload_all(
            ctx,
            [
                (
                    UploadFileSpec(
                        kind="analysis",
                        file_name=artifact.name,
                        content_type="application/json",
                        size_bytes=artifact.stat().st_size,
                    ),
                    artifact,
                )
                for artifact in (transcript_path, signals_path)
            ],
        )

    def _register_clips(
        self,
        ctx: JobContext,
        uploaded: list[_UploadedClip],
        candidates: list[Candidate],
        ids: list[str],
    ) -> int | None:
        """``/clips`` for the uploaded clips. Returns how many were registered, or ``None``
        when the registration could not be confirmed (the API then counts the renders it
        stored: ``clipsRendered`` is left out of the summary)."""
        settings = ctx.payload.settings
        clip_reports: list[ClipReport] = []
        for clip in uploaded:
            cand = next(c for c in candidates if c.rank == clip.rank)
            output = clip.output
            plan = output.plan
            # The clip keeps the *requested* framing mode (normally "auto"), so a later
            # re-render decides the layout again; the region that produced a webcam-split
            # is stored with it. The resolved layout went to the job events.
            framing = FramingSettings(
                mode=settings.framing.mode,
                focus_x=settings.framing.focus_x,
                focus_y=settings.framing.focus_y,
                webcam_region=(
                    plan.webcam_region
                    if plan and plan.layout == "webcam-split"
                    else settings.framing.webcam_region
                ),
            )
            clip_reports.append(
                ClipReport(
                    candidate_id=ids[clip.rank - 1],
                    title=clip.title or None,
                    aspect_ratio=settings.aspect_ratio,
                    start_seconds=cand.start,
                    end_seconds=cand.end,
                    subtitles=settings.subtitles,
                    framing=framing,
                    render=RenderInfo(
                        storage_key=clip.video_key,
                        width=output.width,
                        height=output.height,
                        duration_seconds=output.duration,
                        size_bytes=output.size_bytes,
                        has_subtitles=output.has_subtitles,
                    ),
                    thumbnail_key=clip.thumbnail_key,
                )
            )
        if not clip_reports:
            return 0
        try:
            self.api.clips(ctx.job_id, clip_reports)
        except ApiUnavailableError as exc:
            if not exc.maybe_applied:
                raise
            # No answer, but the API may have created the clips: resending would duplicate
            # them. The analysis is still reported; the API counts the renders it actually
            # stored (clipsRendered is left out of the summary).
            ctx.event(
                "warning",
                "Não foi possível confirmar o registro dos clipes gerados (a API não "
                "respondeu a tempo); a análise foi concluída mesmo assim.",
                {"clips": len(clip_reports), "error": _fragment(str(exc), 300)},
            )
            return None
        return len(clip_reports)

    # ------------------------------------------------------------------ live ----
    def _live_segment(self, ctx: JobContext) -> None:
        """One segment of a live session (``docs/PROCESSING_PIPELINE.md``, live_segment):
        download it (and the previous one) -> ``/probe`` (this segment only) -> analysis of
        the previous tail + this segment on the session's grid -> the commit rule ->
        ``/candidates`` (session times) -> auto-render from the window file -> ``/clips``
        -> ``/analysis`` with ``summary.liveContext`` -> ``/complete`` (this segment's
        duration)."""
        payload = ctx.payload
        live = payload.live
        if live is None:  # guaranteed by the payload validation
            raise JobError("payload de live_segment sem 'live'", code="worker_error")
        settings = payload.settings
        limits = payload.limits
        context = payload.context

        ctx.assume_status("preparing_media", 0, "Preparando o segmento da live…")
        current = self._download_source(
            ctx, progress_range=(0, 5), message="Baixando o segmento da live…"
        )
        ctx.set_progress(5, "Verificando o segmento…", force=True)
        probe = self._probe(ctx, current, min_duration=0.0)
        self._report_probe(ctx, probe)
        segment_seconds = float(probe.duration or 0.0)
        # the commit rule works with the API's durations (the next segment computes the same
        # boundary from the same number); the file's own duration is what is charged
        segment_duration = (
            live.duration_seconds or payload.media.duration_seconds or segment_seconds
        )

        previous_path, previous_duration = self._live_previous(ctx, probe)
        window_path, window_probe, plan = self._live_window(
            ctx, current, probe, previous_path, previous_duration
        )

        post_roll = post_roll_seconds(settings.max_clip_seconds)
        committed_until = (
            context.committed_until
            if context is not None and context.sequence == live.sequence - 1
            else None
        )
        commit = commit_range(
            start=live.start_offset_seconds,
            duration=segment_duration,
            sequence=live.sequence,
            final=live.final,
            post_roll=post_roll,
            previous_duration=payload.previous.duration_seconds if payload.previous else None,
            committed_until=committed_until,
        )
        language, language_source = fixed_language(settings, context)
        sample = list(context.composite_sample) if context is not None else []
        llm_budget = min(
            float(self.settings.llm_budget_seconds), float(self.settings.live_llm_budget_seconds)
        )

        ctx.set_status("analyzing", 10, "Analisando o segmento…")
        result = self.analyzer.analyze(
            window_path,
            window_probe,
            settings,
            limits,
            options=AnalysisOptions(
                transcription=True,
                webcam=True,
                llm=llm_budget > 0,
                transcription_model=limits.transcription_model,
                language=language,
                language_source=language_source,
                media_offset=plan.media_offset,
                window_rating=False,
                llm_budget_seconds=llm_budget,
                webcam_samples=self.settings.live_webcam_samples,
                window=WindowOptions(
                    scope=plan.scope(
                        commit, starts_session=live.sequence == 0, ends_session=live.final
                    ),
                    score_reference=np.asarray(sample, dtype=np.float64) if sample else None,
                    whole_recording=live.sequence == 0 and live.final,
                ),
            ),
            progress=self._analysis_progress(ctx),
            cancel=ctx.cancel,
        )
        ctx.processed_seconds = segment_seconds
        if ctx.status != "detecting_moments":
            ctx.set_status("detecting_moments", 62, "Detectando momentos…")
        self._report_signals(
            ctx,
            result,
            language_hint="Ele vale para o resto da live; se estiver errado, escolha o idioma "
            "nas configurações antes da próxima",
        )
        window_info = {
            "sessionId": live.session_id or None,
            "segmentId": live.segment_id or None,
            "sequence": live.sequence,
            "windowSeconds": [round(plan.origin, 3), round(plan.origin + result.duration, 3)],
            "commitSeconds": commit.to_json(),
            "postRollSeconds": post_roll,
        }
        to_session_times(result.candidates, plan.origin, window_info)
        ids = self._post_candidates(ctx, result.candidates)

        # the transcript counts from the analysis origin; the render seeks in the window file
        offset = plan.media_offset
        words = (
            [
                Word(w.start + offset, w.end + offset, w.word, w.probability)
                for w in result.transcript.words
            ]
            if result.transcript.available
            else None
        )
        uploaded = self._render_candidates(
            ctx,
            result.candidates[: min(settings.render_top, len(ids))],
            source=window_path,
            probe=window_probe,
            words=words,
            webcam_region=settings.framing.webcam_region or result.webcam_region,
            title_for=lambda cand: cand.title or settings.title,
            to_file=plan.to_file,
        )

        ctx.set_status("finalizing", 95, "Enviando resultados…")
        signals = result.signals_json()
        signals["peaks"] = [round(plan.origin + p, 3) for p in signals.get("peaks", [])]
        signals["live"] = {
            **window_info,
            "originSeconds": round(plan.origin, 3),
            "tailSeconds": round(plan.tail_seconds, 3),
            "segmentStartSeconds": round(live.start_offset_seconds, 3),
            "segmentDurationSeconds": round(segment_duration, 3),
            "final": live.final,
            "timeBase": "per-second series and the times inside audio/transcript/visual "
            "count from originSeconds; peaks and candidates are session times",
        }
        targets = self._upload_artifacts(
            ctx, session_transcript_json(result.transcript_json(), plan.origin), signals
        )
        ctx.set_progress(97, "Registrando clipes…", force=True)
        clips_rendered = self._register_clips(ctx, uploaded, result.candidates, ids)

        # this segment's own seconds join the session sample (the tail's were the previous
        # segment's); the values are the composite without focus, the scores' scale
        neutral = result.fusion.neutral
        own = own_seconds(
            neutral if neutral is not None else result.fusion.composite,
            plan.origin,
            live.start_offset_seconds,
            math.inf if live.final else live.start_offset_seconds + segment_duration,
        )
        session_language = next_language(language, result.transcript)
        if language is None and session_language:
            ctx.event(
                "info",
                f"Idioma da live identificado: {session_language}. Os próximos segmentos "
                "usam o mesmo idioma",
                {"language": session_language},
            )
        live_context = {
            "compositeSample": update_sample(
                sample,
                own,
                seen_before=seconds_before(live.start_offset_seconds),
                seed=sample_seed(live.session_id, live.sequence),
            ),
            "language": session_language,
            "sequence": live.sequence,
            "committedUntil": None if math.isinf(commit.end) else round(commit.end, 3),
        }
        summary = AnalysisSummary(
            signals=result.availability(),
            webcam_region=result.webcam_region,
            language=result.transcript.language,
            processed_seconds=round(segment_seconds, 3),
            candidates_found=len(result.candidates),
            clips_rendered=clips_rendered,
            speech_detected=result.speech_detected,
            live_context=live_context,
        )
        self.api.analysis(
            ctx.job_id,
            AnalysisReport(
                transcript_key=targets["transcript.json"].key,
                signals_key=targets["signals.json"].key,
                summary=summary,
            ),
        )
        ctx.set_progress(99, "Concluindo…", force=True)
        self.api.complete(ctx.job_id, segment_seconds, summary)
        ctx.progress.set(100, "Concluído")

    def _live_previous(
        self, ctx: JobContext, probe: ProbeResult
    ) -> tuple[Path | None, float | None]:
        """Download the previous segment (the context of this one) and check it can be put
        before it: ``(path, duration)`` — the duration its media occupies in the session —
        or ``(None, None)`` with a warning event when it cannot (the segment is then
        analysed on its own: the moments right at its start may be missed)."""
        payload = ctx.payload
        live = payload.live
        previous = payload.previous
        if live is None or previous is None or live.sequence == 0:
            return None, None

        def skip(reason: str, data: dict[str, Any] | None = None) -> tuple[None, None]:
            ctx.event(
                "warning",
                f"Segmento anterior não usado como contexto ({reason}): o início deste "
                "segmento foi analisado sem ele",
                {"previousSequence": previous.sequence, **(data or {})},
            )
            return None, None

        if previous.sequence is not None and previous.sequence != live.sequence - 1:
            return skip(f"recebido o segmento {previous.sequence}, esperado {live.sequence - 1}")
        if not contiguous(
            previous.start_offset_seconds, previous.duration_seconds, live.start_offset_seconds
        ):
            return skip("ele não termina onde este começa")
        source = payload.media
        ext = _extension(
            previous.file_name,
            previous.content_type,
            default=_extension(source.file_name, source.content_type, default="mkv"),
        )
        dest = ctx.work_dir / f"previous.{ext}"
        try:
            self._download(
                ctx,
                previous.download_url,
                dest,
                size=previous.size_bytes,
                progress_range=(6, 8),
                message="Baixando o final do segmento anterior…",
                what="o segmento anterior",
            )
        except TransferAborted:
            raise
        except TransferError as exc:
            if exc.status in (404, 410):
                # gone for good (a retry would not bring it back): go on without it
                return skip("não encontrado no armazenamento", {"status": exc.status})
            raise  # storage trouble: transfer_failed, retryable, like the segment itself
        except MediaError as exc:
            return skip(exc.message)
        try:
            previous_probe = probe_file(self.tools, dest)
        except FfmpegNotFound:
            raise
        except FfmpegError as exc:
            _discard_files(dest)
            return skip(f"não reconhecido pelo ffprobe: {_fragment(str(exc), 200)}")
        if not _same_streams(previous_probe, probe):
            _discard_files(dest)
            return skip("formato de áudio ou vídeo diferente")
        file_seconds = float(previous_probe.duration or 0.0)
        duration = previous.duration_seconds
        if duration is None or abs(duration - file_seconds) > DURATION_TOLERANCE:
            duration = file_seconds
        if duration <= 0.5:
            _discard_files(dest)
            return skip("duração desconhecida")
        return dest, duration

    def _live_window(
        self,
        ctx: JobContext,
        current: Path,
        probe: ProbeResult,
        previous: Path | None,
        previous_duration: float | None,
    ) -> tuple[Path, ProbeResult, WindowPlan]:
        """The window file: the previous segment and this one stream-copied one after the
        other (the previous one occupying its session duration, so both keep the session's
        times), or this segment alone."""
        live = ctx.payload.live
        start = live.start_offset_seconds if live is not None else 0.0
        if previous is not None and previous_duration is not None:
            window = ctx.work_dir / "window.mkv"
            ctx.set_progress(9, "Juntando com o final do segmento anterior…", force=True)
            window_probe: ProbeResult | None = None
            problem = ""
            try:
                concat_copy(
                    self.tools,
                    [previous, current],
                    window,
                    durations=[previous_duration, None],
                    cancel=ctx.cancel,
                )
                window_probe = probe_file(self.tools, window)
            except FfmpegNotFound:
                raise
            except FfmpegError as exc:
                problem = _fragment(str(exc), 300)
            finally:
                _discard_files(previous)
            if window_probe is not None:
                expected = previous_duration + float(probe.duration or 0.0)
                if not window_probe.has_video:
                    problem = "o arquivo juntado não tem vídeo"
                elif abs(float(window_probe.duration or 0.0) - expected) > 2 * DURATION_TOLERANCE:
                    problem = (
                        f"duração {float(window_probe.duration or 0.0):.1f}s, "
                        f"esperada {expected:.1f}s"
                    )
                else:
                    return window, window_probe, plan_window(start, previous_duration)
            _discard_files(window)
            ctx.event(
                "warning",
                f"Não foi possível juntar o segmento anterior a este ({problem}): o início "
                "deste segmento foi analisado sem ele",
                {"error": problem},
            )
        return current, probe, plan_window(start, None)

    # ------------------------------------------------------------------ render ----
    def _render_clip(self, ctx: JobContext) -> None:
        payload = ctx.payload
        clip = payload.clip
        if clip is None:
            raise JobError(
                "payload de render_clip sem 'clip'", code="worker_error", retryable=False
            )
        limits = payload.limits

        ctx.assume_status("preparing_media", 0, "Preparando arquivo…")  # set by the claim
        to_local: Callable[[float], float] | None = None
        if payload.segments:
            # a clip of a live project: the segments overlapping it, joined
            path, probe, to_local = self._prepare_segments(ctx)
        else:
            path = self._download_source(ctx, progress_range=(0, 15))
            probe = self._probe(ctx, path)
        if not probe.has_video:
            raise MediaError("arquivo sem vídeo")

        transcript: Transcript | None = None
        if clip.subtitles.enabled and payload.analysis and payload.analysis.transcript_url:
            ctx.set_progress(18, "Baixando transcrição…", force=True)
            try:
                data = self.api.download_json(
                    payload.analysis.transcript_url, should_stop=lambda: ctx.cancel.cancelled
                )
                transcript = Transcript.from_json(data) if isinstance(data, dict) else None
            except TransferAborted:
                ctx.check_cancel()
                raise
            except (TransferError, ValueError) as exc:
                ctx.event(
                    "warning", f"Transcrição indisponível para as legendas: {_fragment(str(exc))}"
                )
                transcript = None
        words = transcript.words if transcript and transcript.available else None
        clip_start, clip_end = float(clip.start_seconds), float(clip.end_seconds)
        if to_local is not None:
            # session times (the clip and a live transcript's words) -> the joined file's
            clip_start, clip_end = to_local(clip_start), to_local(clip_end)
            if words:
                words = [
                    Word(to_local(w.start), to_local(w.end), w.word, w.probability) for w in words
                ]

        start = max(0.0, clip_start)
        end = min(float(probe.duration or clip_end), clip_end)
        if end - start < 0.5:
            raise RenderError("intervalo do clipe inválido")
        webcam_region: WebcamRegion | None = clip.framing.webcam_region or (
            payload.analysis.webcam_region if payload.analysis else None
        )

        ctx.set_status("generating_clips", 20, "Renderizando clipe…")
        request = RenderRequest(
            source=path,
            src_w=int(probe.width or 0),
            src_h=int(probe.height or 0),
            start=start,
            end=end,
            aspect=clip.aspect_ratio,
            max_render_height=limits.max_render_height,
            framing_mode=clip.framing.mode,
            focus_x=clip.framing.focus_x,
            webcam_region=webcam_region,
            allow_webcam=limits.allow_webcam_layout,
            subtitles_enabled=clip.subtitles.enabled,
            subtitle_style=clip.subtitles.style,
            subtitle_position=clip.subtitles.position,
            words=words,
            title=clip.title,
            normalize_audio=True,
            has_audio=probe.has_audio,
            output_name=f"render-v{clip.version}",
        )
        try:
            output = self.renderer.render(request, ctx.work_dir / "render", cancel=ctx.cancel)
        except JobCancelled, FfmpegNotFound, FfmpegTimeout:
            # a missing ffmpeg/ffprobe (deployment) or a timeout is a retryable worker_error,
            # never a failed render of the user's clip
            raise
        except (FfmpegError, OSError, ValueError) as exc:
            error_text = str(exc)[:500]
            failed = RenderResultReport(render_id=clip.render_id, status="failed", error=error_text)
            self._safe(lambda: self.api.render_result(ctx.job_id, failed))
            raise RenderError(f"falha ao renderizar o clipe: {error_text[:300]}") from exc
        for warning in output.warnings:
            ctx.event("warning", _fragment(warning))
        ctx.event(
            "info",
            _render_summary(f"Versão {clip.version} do clipe renderizada", output),
            {
                "renderId": clip.render_id,
                "layout": output.layout,
                "width": output.width,
                "height": output.height,
                "hasSubtitles": output.has_subtitles,
            },
        )

        ctx.set_status("finalizing", 90, "Enviando clipe…")
        files: list[tuple[UploadFileSpec, Path]] = [
            (
                UploadFileSpec(
                    kind="clip_render",
                    file_name=output.path.name,
                    content_type="video/mp4",
                    size_bytes=output.size_bytes,
                ),
                output.path,
            )
        ]
        if output.thumbnail_path is not None:
            files.append(
                (
                    UploadFileSpec(
                        kind="thumbnail",
                        file_name=output.thumbnail_path.name,
                        content_type="image/jpeg",
                        size_bytes=output.thumbnail_path.stat().st_size,
                    ),
                    output.thumbnail_path,
                )
            )
        targets = self._upload_all(ctx, files)
        thumb = targets.get(output.thumbnail_path.name) if output.thumbnail_path else None
        self.api.render_result(
            ctx.job_id,
            RenderResultReport(
                render_id=clip.render_id,
                status="ready",
                storage_key=targets[output.path.name].key,
                width=output.width,
                height=output.height,
                duration_seconds=output.duration,
                size_bytes=output.size_bytes,
                has_subtitles=output.has_subtitles,
                thumbnail_key=thumb.key if thumb else None,
            ),
        )
        ctx.set_progress(99, "Concluindo…", force=True)
        # No summary: the documented summary is the analysis one, and the render data was
        # already reported through /render-result (the layout went to the job events).
        self.api.complete(ctx.job_id, 0.0, None)
        ctx.progress.set(100, "Concluído")

    def _prepare_segments(
        self, ctx: JobContext
    ) -> tuple[Path, ProbeResult, Callable[[float], float]]:
        """Download the live segments of a ``render_clip`` and join them (concat demuxer,
        stream copy): ``(file, probe, session time -> file time)``. Each segment occupies
        its session duration in the joined file, so the clip's session times map onto it
        through the segments' ``startOffsetSeconds``."""
        payload = ctx.payload
        segments = sorted(payload.segments, key=lambda s: s.start_offset_seconds)
        count = len(segments)
        parts: list[Path] = []
        for index, segment in enumerate(segments):
            ext = _extension(segment.file_name, segment.content_type, default="mkv")
            dest = ctx.work_dir / f"part-{index:03d}.{ext}"
            self._download(
                ctx,
                segment.download_url,
                dest,
                size=segment.size_bytes,
                progress_range=(int(15 * index / count), int(15 * (index + 1) / count)),
                message=f"Baixando o segmento {index + 1} de {count} da live…",
                what=f"o segmento {index + 1} de {count} da live",
            )
            parts.append(dest)
        probes = [self._probe(ctx, part) for part in parts]
        durations: list[float] = []
        for segment, part_probe in zip(segments, probes, strict=True):
            file_seconds = float(part_probe.duration or 0.0)
            duration = segment.duration_seconds
            if duration is None or abs(duration - file_seconds) > DURATION_TOLERANCE:
                duration = file_seconds
            durations.append(duration)
        timeline = SegmentTimeline.from_segments(
            [s.start_offset_seconds for s in segments], durations
        )
        if count == 1:
            return parts[0], probes[0], timeline.to_local
        joined = ctx.work_dir / "source.mkv"
        ctx.set_progress(16, "Juntando os segmentos da live…", force=True)
        try:
            concat_copy(
                self.tools,
                parts,
                joined,
                durations=[*durations[:-1], None],
                cancel=ctx.cancel,
            )
        except FfmpegNotFound, FfmpegTimeout:
            raise  # deployment problem / stuck process: retryable worker_error
        except FfmpegError as exc:
            clip = payload.clip
            error_text = f"não foi possível juntar os segmentos da live: {exc}"[:500]
            if clip is not None:
                failed = RenderResultReport(
                    render_id=clip.render_id, status="failed", error=error_text
                )
                self._safe(lambda: self.api.render_result(ctx.job_id, failed))
            raise RenderError(error_text) from exc
        finally:
            _discard_files(*parts)
        return joined, self._probe(ctx, joined), timeline.to_local


def _render_summary(headline: str, output: RenderOutput) -> str:
    """pt-BR info event for a finished render (resolved layout, size, captions)."""
    layout = LAYOUT_LABELS.get(output.layout, output.layout)
    captions = "com legendas" if output.has_subtitles else "sem legendas"
    return f"{headline}: {output.width}x{output.height}, {layout}, {captions}"


def _disk_free_bytes(path: Path) -> int | None:
    """Free bytes on the volume of ``path`` (``None`` when it cannot be read)."""
    try:
        return int(shutil.disk_usage(path).free)
    except OSError:
        return None


def _discard_files(*paths: Path | None) -> None:
    """Best-effort removal of local files that are no longer needed (uploaded or partial
    renders); whatever stays is removed with the work directory at the end of the job."""
    for path in paths:
        if path is not None:
            with contextlib.suppress(OSError):
                path.unlink(missing_ok=True)


def _touch(path: Path) -> None:
    """Refresh a work directory's mtime (liveness marker for the stale-directory sweep)."""
    with contextlib.suppress(OSError):
        os.utime(path)


def _write_work_dir_marker(work_dir: Path, job_id: str, worker_id: str) -> None:
    """Write ``WORK_DIR/{jobId}/.clipforge-job``: the proof that this service's runner
    created the directory for that job (the stale-directory sweep removes nothing else).
    A retry of the job rewrites it, which also refreshes the directory's liveness."""
    marker = {
        "jobId": job_id,
        "workerId": worker_id,
        "createdAt": datetime.now(UTC).isoformat(timespec="seconds"),
    }
    (work_dir / WORK_DIR_MARKER).write_text(json.dumps(marker), encoding="utf-8")


def _is_marked_work_dir(path: Path, job_id: str, worker_id: str | None = None) -> bool:
    """Whether ``path`` holds the marker :func:`_write_work_dir_marker` wrote for
    ``job_id``: a small regular file (not a link) naming that very job, so a copied or
    renamed job directory is not taken for another one — and, with ``worker_id``, written
    by that worker."""
    marker = path / WORK_DIR_MARKER
    try:
        info = os.stat(marker, follow_symlinks=False)
        if not stat.S_ISREG(info.st_mode) or info.st_size > WORK_DIR_MARKER_MAX_BYTES:
            return False
        data = json.loads(marker.read_text(encoding="utf-8"))
    except OSError, ValueError:
        return False
    if not isinstance(data, dict) or data.get("jobId") != job_id:
        return False
    return worker_id is None or data.get("workerId") == worker_id


def _newest_mtime(path: Path, limit: int = WORK_DIR_WALK_LIMIT) -> float:
    """Newest mtime of a directory and of everything it contains (symlinks not followed).

    The walk is bounded and conservative: a tree with more than ``limit`` entries, or one
    that cannot be walked completely (an unreadable subdirectory, an entry that vanishes
    while walking: something is still working in it), answers ``math.inf`` ("changed just
    now"), so a partial view never makes a live directory look stale.
    """
    newest = os.stat(path, follow_symlinks=False).st_mtime
    incomplete = False

    def on_error(_exc: OSError) -> None:
        nonlocal incomplete
        incomplete = True

    seen = 0
    for dirpath, dirnames, filenames in os.walk(path, onerror=on_error):
        for name in (*dirnames, *filenames):
            seen += 1
            if seen > limit:
                return math.inf
            try:
                mtime = os.stat(os.path.join(dirpath, name), follow_symlinks=False).st_mtime
            except OSError:
                return math.inf
            newest = max(newest, mtime)
    return math.inf if incomplete else newest


def _remove_tree(path: Path, attempts: int = 4) -> None:
    for attempt in range(attempts):
        try:
            if path.exists():
                shutil.rmtree(path)
            return
        except OSError:
            time.sleep(0.5 * (attempt + 1))
    shutil.rmtree(path, ignore_errors=True)
    if path.exists():
        log.warning("workdir.not_removed", path=str(path))


def ceil_minutes(seconds: float) -> int:
    return int(math.ceil(max(0.0, seconds) / 60.0))
