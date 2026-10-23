"""FastAPI entry point: ``/health``, ``/version``, the ``POST /analyze`` development
endpoint (``DEV_ENDPOINTS=true``) and the worker thread (``WORKER_ENABLED=true``)."""

from __future__ import annotations

import platform
import time
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException
from fastapi.concurrency import run_in_threadpool

from app import __version__
from app.analysis.pipeline import AnalysisOptions, Analyzer
from app.analysis.transcription import TranscriptionService
from app.api_client import InternalApiClient
from app.config import Settings, get_settings
from app.logging_setup import configure_logging, get_logger
from app.media.ffmpeg import FfmpegError, FfmpegTools
from app.media.probe import probe_file
from app.models import AnalyzeRequest, AnalyzeResponse
from app.render.renderer import Renderer
from app.worker.errors import JobError
from app.worker.loop import WorkerLoop
from app.worker.runner import JobRunner

log = get_logger("main")


class ServiceState:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings
        self.tools = FfmpegTools.from_settings(settings)
        self.transcription = TranscriptionService(settings)
        self.analyzer = Analyzer(settings, self.tools, transcription=self.transcription)
        self.renderer = Renderer(self.tools, settings.fonts_dir)
        self.api: InternalApiClient | None = None
        self.worker: WorkerLoop | None = None
        self.started_at = time.time()

    def health(self) -> dict[str, Any]:
        ffmpeg_version = self.tools.ffmpeg_version()
        ffprobe_version = self.tools.ffprobe_version()
        worker: dict[str, Any]
        if self.worker is not None:
            worker = self.worker.status()
        else:
            worker = {
                "enabled": False,
                "running": False,
                "currentJobId": None,
                "workerId": self.settings.worker_id,
            }
        status = "ok" if ffmpeg_version and ffprobe_version else "degraded"
        return {
            "status": status,
            "version": __version__,
            "uptimeSeconds": round(time.time() - self.started_at, 1),
            "ffmpeg": {
                "available": ffmpeg_version is not None,
                "version": ffmpeg_version,
                "path": self.settings.ffmpeg_path,
            },
            "ffprobe": {
                "available": ffprobe_version is not None,
                "version": ffprobe_version,
                "path": self.settings.ffprobe_path,
            },
            "whisper": self.transcription.status(),
            "llm": self.llm_status(),
            "worker": worker,
            "devEndpoints": self.settings.dev_endpoints,
            "workDir": self.settings.work_dir,
        }

    def llm_status(self) -> dict[str, Any]:
        """Configured LLM provider (never contacted here; keys are never shown)."""
        client = self.analyzer.llm_client
        status: dict[str, Any] = {
            "provider": self.settings.llm_provider,
            "enabled": self.settings.llm_enabled,
            "model": (client.model if client is not None else None)
            or self.settings.llm_model_label,
            "budgetSeconds": self.settings.llm_budget_seconds,
        }
        if self.settings.llm_provider == "local":
            status["baseUrl"] = self.settings.llm_base_url
        return status


def analyze_local_file(state: ServiceState, request: AnalyzeRequest) -> AnalyzeResponse:
    path = Path(request.path)
    if not path.is_file():
        raise HTTPException(status_code=404, detail=f"file not found: {request.path}")
    started = time.monotonic()
    try:
        probe = probe_file(state.tools, path)
    except FfmpegError as exc:
        raise HTTPException(status_code=422, detail=f"ffprobe failed: {exc}") from exc
    if not probe.duration:
        raise HTTPException(status_code=422, detail="could not determine the media duration")
    try:
        result = state.analyzer.analyze(
            path,
            probe,
            request.settings,
            request.limits,
            options=AnalysisOptions(
                transcription=request.transcription,
                webcam=request.webcam,
                llm=request.llm,
                transcription_model=request.limits.transcription_model,
            ),
        )
    except JobError as exc:
        raise HTTPException(status_code=422, detail=exc.message) from exc
    timings = dict(result.timings)
    timings["total"] = time.monotonic() - started
    return AnalyzeResponse(
        probe=probe.to_report(),
        duration_seconds=round(result.duration, 3),
        candidates=[c.to_input() for c in result.candidates],
        signals=result.availability(),
        webcam_region=result.webcam_region,
        language=result.transcript.language,
        threshold=round(result.fusion.threshold, 3),
        weights={k: round(v, 3) for k, v in result.fusion.weights.items()},
        timings={k: round(v, 2) for k, v in timings.items()},
        notices=[
            {"level": level, "message": message, "data": data}
            for level, message, data in result.notices
        ],
        suggested_titles={str(c.rank): c.title for c in result.candidates if c.title},
    )


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or get_settings()
    configure_logging(settings.log_level, settings.log_format)
    state = ServiceState(settings)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        if settings.worker_enabled:
            state.api = InternalApiClient(
                settings.clipforge_api_base_url,
                settings.clipforge_internal_api_key,
                settings.worker_id,
                timeout=settings.api_timeout_seconds,
                max_retries=settings.api_max_retries,
            )
            runner = JobRunner(
                settings, state.api, state.tools, analyzer=state.analyzer, renderer=state.renderer
            )
            state.worker = WorkerLoop(settings, state.api, runner)
            state.worker.start()
        log.info(
            "service.started",
            version=__version__,
            ffmpeg=state.tools.ffmpeg_version(),
            worker=settings.worker_enabled,
            dev_endpoints=settings.dev_endpoints,
        )
        try:
            yield
        finally:
            if state.worker is not None:
                await run_in_threadpool(state.worker.stop, 15.0)
            if state.api is not None:
                state.api.close()
            log.info("service.stopped")

    app = FastAPI(title="ClipForge processing", version=__version__, lifespan=lifespan)
    app.state.service = state

    @app.get("/health")
    async def health() -> dict[str, Any]:
        return await run_in_threadpool(state.health)

    @app.get("/version")
    async def version() -> dict[str, Any]:
        return {
            "service": "clipforge-processing",
            "version": __version__,
            "python": platform.python_version(),
            "ffmpeg": state.tools.ffmpeg_version(),
            "whisperModel": settings.whisper_model if settings.transcription_enabled else None,
        }

    if settings.dev_endpoints:

        @app.post("/analyze", response_model=AnalyzeResponse)
        async def analyze(request: AnalyzeRequest) -> AnalyzeResponse:
            return await run_in_threadpool(analyze_local_file, state, request)

    return app


app = create_app()
