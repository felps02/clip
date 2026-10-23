"""Analysis orchestration: probe result + local file -> signals, transcript, candidates.

Used by the worker (``analyze_source`` jobs) and by the ``POST /analyze`` development
endpoint. Progress is reported through a callback ``(phase, percent, message)`` where
``phase`` is ``"analyzing"`` or ``"detecting"`` (the worker maps phases to job statuses).
Things the user should know about (a source shorter than the minimum clip, an LLM stage
that stopped early) are collected in ``AnalysisResult.notices`` and become job events.
"""

from __future__ import annotations

import math
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np

from app.analysis.audio_features import AudioFeatureExtractor, AudioFeatures, summarize_audio
from app.analysis.fusion import (
    Candidate,
    Focus,
    FusionResult,
    FusionSettings,
    PeakScope,
    detect_candidates,
)
from app.analysis.lexicon import Lexicon, default_lexicon, with_custom_keywords
from app.analysis.llm import (
    JsonLlmClient,
    LlmProvider,
    LlmResult,
    NoneProvider,
    build_llm_client,
    build_llm_provider,
    build_windows,
    llm_series,
)
from app.analysis.llm_assist import AssistOutcome, run_assist
from app.analysis.transcription import (
    Transcript,
    TranscriptionService,
    TranscriptSeries,
    transcribe_media,
    transcript_series,
)
from app.analysis.visual_features import VisualFeatures, compute_visual_features
from app.analysis.webcam import detect_webcam
from app.config import Settings
from app.logging_setup import get_logger
from app.media.audio import iter_pcm
from app.media.ffmpeg import FfmpegNotFound, FfmpegTools
from app.media.probe import ProbeResult
from app.models import AnalysisSettings, JobLimits, WebcamRegion
from app.worker.errors import CancelToken, JobCancelled, MediaError

log = get_logger("pipeline")

ProgressFn = Callable[[str, int, str], None]
Notice = tuple[str, str, dict[str, Any]]  # (level, pt-BR message, data) -> job event

# documented progress ranges (analyze_source)
P_AUDIO = (10, 20)
P_TRANSCRIPTION = (20, 45)
P_SPEECH_SCAN = (20, 24)  # voice activity scan, then language detection
P_TRANSCRIBE = (25, 45)
P_VISUAL = (45, 58)
P_WEBCAM = (58, 60)
P_DETECT = (60, 65)

SHORT_SOURCE_MESSAGE = "Vídeo mais curto que a duração mínima; usando o vídeo inteiro como limite"


@dataclass(slots=True)
class WindowOptions:
    """A live window (``live_segment``): the peaks it may report (the commit rule, in the
    window's own seconds, and which of its edges are the recording's) and the session's
    composite sample its scores are compared with."""

    scope: PeakScope = field(default_factory=PeakScope)
    score_reference: np.ndarray | None = None
    # the window is the whole recording (first and final segment of the session): the
    # short-source rules apply (whole-video candidate), as for an uploaded video
    whole_recording: bool = False


@dataclass(slots=True)
class AnalysisOptions:
    transcription: bool = True
    webcam: bool = True
    llm: bool = True
    transcription_model: str | None = None
    # fixed transcription language (no detection), taking precedence over the settings,
    # recorded with ``language_source`` ("session": the language of a live session)
    language: str | None = None
    language_source: str | None = None
    # seconds at the beginning of the file that the analysis skips: every time of the
    # result counts from there (a live window starts on a whole second of the session)
    media_offset: float = 0.0
    # the anthropic provider's rating of transcript windows before fusion (not for live)
    window_rating: bool = True
    # budget of the LLM assist stage (None = LLM_BUDGET_SECONDS)
    llm_budget_seconds: float | None = None
    webcam_samples: int | None = None  # None = WEBCAM_SAMPLES
    window: WindowOptions | None = None


@dataclass(slots=True)
class AnalysisResult:
    probe: ProbeResult
    duration: float
    audio: AudioFeatures
    transcript: Transcript
    transcript_series: TranscriptSeries
    visual: VisualFeatures
    webcam_region: WebcamRegion | None
    llm: LlmResult
    fusion: FusionResult
    timings: dict[str, float] = field(default_factory=dict)
    assist: AssistOutcome | None = None
    notices: list[Notice] = field(default_factory=list)
    # min/target/max actually used by the fusion (clamped for a short source), focus
    effective: dict[str, Any] = field(default_factory=dict)

    @property
    def candidates(self) -> list[Candidate]:
        return self.fusion.candidates

    @property
    def speech_detected(self) -> bool | None:
        return self.transcript.speech_detected if self.transcript.available else None

    def availability(self) -> dict[str, Any]:
        """Per-signal availability. ``coverage`` (transcript, visual) is the share of the
        recording the signal actually analysed: below 1 when a transcription chunk failed
        or the frame pipe stopped early (``error`` then says why). ``transcript`` also says
        whether speech was found (``speechDetected``) and how sure the language is."""
        transcript: dict[str, Any] = {"available": self.transcript.available}
        if self.transcript.model:
            transcript["model"] = self.transcript.model
        if self.transcript.available:
            transcript["speechDetected"] = self.transcript.speech_detected
            if self.transcript.speech_seconds is not None:
                transcript["speechSeconds"] = self.transcript.speech_seconds
        if self.transcript.language:
            transcript["language"] = self.transcript.language
            transcript["languageSource"] = self.transcript.language_source
            if self.transcript.language_probability is not None:
                transcript["languageProbability"] = round(self.transcript.language_probability, 3)
            if self.transcript.language_confidence:
                transcript["languageConfidence"] = self.transcript.language_confidence
        if self.transcript.available:
            transcript["coverage"] = round(self.transcript.coverage, 3)
        if self.transcript.error:
            transcript["error"] = self.transcript.error
        audio: dict[str, Any] = {"available": self.audio.available, "method": self.audio.method}
        if self.audio.error:
            audio["error"] = self.audio.error
        visual: dict[str, Any] = {"available": self.visual.available, "method": self.visual.method}
        if self.visual.available:
            visual["coverage"] = round(self.visual.coverage, 3)
        if self.visual.error:
            visual["error"] = self.visual.error
        return {
            "audio": audio,
            "transcript": transcript,
            "visual": visual,
            "llm": self.llm_availability(),
        }

    def llm_availability(self) -> dict[str, Any]:
        """``signals.llm``: provider, model, availability — the window rating (anthropic)
        and/or the assist stage (re-rank, titles)."""
        llm: dict[str, Any] = {"available": self.llm.available, "provider": self.llm.provider}
        if self.llm.model:
            llm["model"] = self.llm.model
        if self.llm.error:
            llm["error"] = self.llm.error
        if self.assist is not None:
            assist = self.assist.to_json()
            llm["available"] = bool(llm["available"] or assist["available"])
            llm["provider"] = assist["provider"]
            if assist.get("model"):
                llm["model"] = assist["model"]
            if assist.get("error") and not llm.get("error"):
                llm["error"] = assist["error"]
            llm.update(
                {
                    k: assist[k]
                    for k in ("rerank", "titles", "seconds", "budgetSeconds")
                    if k in assist
                }
            )
            if assist.get("budgetExhausted"):
                llm["budgetExhausted"] = True
        return llm

    def signals_json(self) -> dict[str, Any]:
        from app.analysis.common import rounded

        return {
            "version": 1,
            "durationSeconds": round(self.duration, 3),
            "resolutionSeconds": 1,
            "availability": self.availability(),
            "weights": {k: round(v, 3) for k, v in self.fusion.weights.items()},
            "threshold": round(self.fusion.threshold, 3),
            "effectiveSettings": self.effective,
            "composite": rounded(self.fusion.composite),
            "peaks": [p + 0.5 for p in self.fusion.peaks],
            "audio": self.audio.to_json(),
            "transcript": self.transcript_series.to_json(),
            "visual": self.visual.to_json(),
            "llm": self.llm.to_json(),
            "assist": self.assist.to_json() if self.assist is not None else None,
            "webcamRegion": self.webcam_region.to_api() if self.webcam_region else None,
            "candidates": [
                {
                    "rank": c.rank,
                    "startSeconds": c.start,
                    "endSeconds": c.end,
                    "score": c.score,
                    "category": c.category,
                    "peakSeconds": c.peak,
                    "title": c.title,
                }
                for c in self.candidates
            ],
            "timings": {k: round(v, 2) for k, v in self.timings.items()},
        }

    def transcript_json(self) -> dict[str, Any]:
        return self.transcript.to_json()


def effective_fusion_settings(
    job_settings: AnalysisSettings, duration: float, *, allow_short_source: bool = True
) -> tuple[FusionSettings, bool]:
    """Fusion settings for the job; a source shorter than ``minClipSeconds`` gets min,
    target and max clamped to its duration (the whole video becomes the limit) —
    ``(settings, short_source)``. A live window that is not the whole session passes
    ``allow_short_source=False``: its length says nothing about the recording's."""
    focus = Focus.from_categories(job_settings.focus_categories)
    short = allow_short_source and duration < job_settings.min_clip_seconds
    if short:
        return (
            FusionSettings(
                min_clip=duration,
                max_clip=duration,
                target_clip=duration,
                sensitivity=job_settings.sensitivity,
                max_candidates=job_settings.max_candidates,
                focus=focus,
                short_source=True,
            ),
            True,
        )
    return (
        FusionSettings(
            min_clip=job_settings.min_clip_seconds,
            max_clip=job_settings.max_clip_seconds,
            target_clip=job_settings.target_clip_seconds,
            sensitivity=job_settings.sensitivity,
            max_candidates=job_settings.max_candidates,
            focus=focus,
        ),
        False,
    )


class Analyzer:
    def __init__(
        self,
        settings: Settings,
        tools: FfmpegTools,
        *,
        transcription: TranscriptionService | None = None,
        llm_provider: LlmProvider | None = None,
        llm_client: JsonLlmClient | None = None,
        lexicon: Lexicon | None = None,
    ) -> None:
        self.settings = settings
        self.tools = tools
        self.transcription = transcription or TranscriptionService(settings)
        self.llm_provider = llm_provider or build_llm_provider(settings)
        self.llm_client = (
            llm_client
            if llm_client is not None
            else build_llm_client(settings, provider=self.llm_provider)
        )
        self.lexicon = lexicon or default_lexicon()

    def analyze(
        self,
        path: str | Path,
        probe: ProbeResult,
        job_settings: AnalysisSettings,
        limits: JobLimits | None = None,
        *,
        options: AnalysisOptions | None = None,
        progress: ProgressFn | None = None,
        cancel: CancelToken | None = None,
    ) -> AnalysisResult:
        options = options or AnalysisOptions()
        limits = limits or JobLimits()
        path = Path(path)
        media_offset = max(0.0, float(options.media_offset))
        duration = float(probe.duration or 0.0) - media_offset
        if duration <= 0.5:
            raise MediaError("não foi possível determinar a duração do arquivo")
        if not probe.has_audio and not probe.has_video:
            raise MediaError("arquivo sem áudio e sem vídeo")
        n = max(1, int(math.ceil(duration)))
        timings: dict[str, float] = {}
        notices: list[Notice] = []
        window = options.window

        def report(phase: str, pct: float, message: str) -> None:
            if progress is not None:
                progress(phase, int(round(pct)), message)

        def check() -> None:
            if cancel is not None:
                cancel.raise_if_cancelled()

        # ---- audio -------------------------------------------------------------------
        started = time.monotonic()
        report("analyzing", P_AUDIO[0], "Analisando áudio…")
        if probe.has_audio:
            extractor = AudioFeatureExtractor()
            total_samples = max(1.0, duration * 16000.0)
            last = -1
            for chunk in iter_pcm(
                self.tools,
                path,
                start=media_offset if media_offset > 0 else None,
                chunk_seconds=10.0,
                cancel=cancel,
                total_duration=duration,
            ):
                extractor.feed(chunk)
                pct = P_AUDIO[0] + (P_AUDIO[1] - P_AUDIO[0]) * min(
                    1.0, extractor.samples_seen / total_samples
                )
                if int(pct) != last:
                    last = int(pct)
                    report("analyzing", pct, "Analisando áudio…")
            audio = summarize_audio(extractor.finish(), duration)
        else:
            audio = AudioFeatures.unavailable(n, "no audio stream")
        timings["audio"] = time.monotonic() - started
        check()

        # ---- transcription -----------------------------------------------------------
        started = time.monotonic()
        if options.transcription and probe.has_audio:
            provider = self.transcription.provider_for(
                options.transcription_model or limits.transcription_model
            )
            total_min = int(math.ceil(duration / 60.0))
            report("analyzing", P_TRANSCRIPTION[0], "Procurando fala no áudio…")

            def on_phase(name: str, fraction: float) -> None:
                if name == "speech":
                    pct = P_SPEECH_SCAN[0] + (P_SPEECH_SCAN[1] - P_SPEECH_SCAN[0]) * fraction
                    report("analyzing", pct, "Procurando fala no áudio…")
                elif name == "language":
                    report("analyzing", P_SPEECH_SCAN[1], "Identificando o idioma da fala…")

            def t_progress(done: float, total: float) -> None:
                frac = min(1.0, done / max(total, 1.0))
                pct = P_TRANSCRIBE[0] + (P_TRANSCRIBE[1] - P_TRANSCRIBE[0]) * frac
                report(
                    "analyzing", pct, f"Transcrevendo áudio ({int(done // 60)} de {total_min} min)…"
                )

            transcript = transcribe_media(
                self.tools,
                path,
                duration,
                provider,
                language=options.language or job_settings.whisper_language,
                silences=audio.silences if audio.available else None,
                chunk_seconds=self.settings.transcription_chunk_seconds,
                progress=t_progress,
                cancel=cancel,
                on_phase=on_phase,
                ignore_languages=self.settings.whisper_ignored_languages,
                media_offset=media_offset,
            )
            if (
                options.language
                and options.language_source
                and transcript.language_source == "settings"
            ):
                transcript.language_source = options.language_source
        elif not probe.has_audio:
            transcript = Transcript.unavailable("no audio stream")
        else:
            transcript = Transcript.unavailable("transcription disabled")
        transcript.duration = duration
        lexicon = with_custom_keywords(self.lexicon, job_settings.custom_keywords)
        tseries = transcript_series(
            transcript, duration, lexicon, audio_burst=audio.burst if audio.available else None
        )
        timings["transcription"] = time.monotonic() - started
        check()

        # ---- visual --------------------------------------------------------------------
        started = time.monotonic()
        if probe.has_video and probe.width and probe.height:
            report("analyzing", P_VISUAL[0], "Analisando vídeo…")

            def v_progress(t: float, total: float) -> None:
                frac = min(1.0, t / max(total, 1.0))
                report(
                    "analyzing",
                    P_VISUAL[0] + (P_VISUAL[1] - P_VISUAL[0]) * frac,
                    "Analisando vídeo…",
                )

            visual = compute_visual_features(
                self.tools,
                path,
                duration,
                probe.width,
                probe.height,
                fps=self.settings.visual_fps,
                width=self.settings.visual_width,
                progress=v_progress,
                cancel=cancel,
                start=media_offset,
            )
        else:
            visual = VisualFeatures.unavailable(n, "no video stream")
        timings["visual"] = time.monotonic() - started
        check()

        # ---- webcam --------------------------------------------------------------------
        started = time.monotonic()
        webcam_region: WebcamRegion | None = None
        if (
            options.webcam
            and self.settings.webcam_detection
            and probe.has_video
            and probe.width
            and probe.height
        ):
            report("analyzing", P_WEBCAM[0], "Procurando a webcam…")
            try:
                webcam_region = detect_webcam(
                    self.tools,
                    path,
                    duration,
                    probe.width,
                    probe.height,
                    n_samples=options.webcam_samples or self.settings.webcam_samples,
                    cancel=cancel,
                    start=media_offset,
                )
            except JobCancelled, FfmpegNotFound:
                raise
            except Exception as exc:
                log.warning("webcam.failed", error=str(exc)[:200])
                webcam_region = None
        timings["webcam"] = time.monotonic() - started
        check()

        # ---- optional LLM window rating (opt-in anthropic provider) --------------------
        started = time.monotonic()
        provider_llm = (
            self.llm_provider if options.llm and options.window_rating else NoneProvider()
        )
        if provider_llm.name != "none" and transcript.available and transcript.segments:
            report("detecting", P_DETECT[0], "Avaliando trechos com o modelo de linguagem…")
            windows = build_windows(
                transcript,
                duration,
                job_settings.target_clip_seconds,
                self.settings.llm_max_windows,
            )
            llm_result = provider_llm.rate_windows(
                windows, language=transcript.language, cancel=cancel
            )
        else:
            llm_result = LlmResult(available=False, provider=provider_llm.name)
        llm_arr: np.ndarray | None = llm_series(llm_result, n)
        timings["llm"] = time.monotonic() - started
        check()

        # ---- fusion --------------------------------------------------------------------
        started = time.monotonic()
        report("detecting", P_DETECT[0] + 1, "Detectando momentos…")
        fusion_settings, short_source = effective_fusion_settings(
            job_settings,
            duration,
            allow_short_source=window is None or window.whole_recording,
        )
        if short_source:
            notices.append(
                (
                    "info",
                    SHORT_SOURCE_MESSAGE,
                    {
                        "durationSeconds": round(duration, 2),
                        "minClipSeconds": job_settings.min_clip_seconds,
                        "effectiveMinClipSeconds": round(fusion_settings.min_clip, 2),
                        "effectiveMaxClipSeconds": round(fusion_settings.max_clip, 2),
                    },
                )
            )
        fusion = detect_candidates(
            duration,
            fusion_settings,
            audio=audio,
            transcript=tseries,
            visual=visual,
            llm=llm_arr,
            llm_meta={
                "provider": llm_result.provider,
                "model": llm_result.model,
                "error": llm_result.error,
            },
            scope=window.scope if window is not None else None,
            score_reference=window.score_reference if window is not None else None,
        )
        timings["fusion"] = time.monotonic() - started
        check()

        # ---- optional LLM assist: re-rank against the instructions/focus, titles -------
        assist: AssistOutcome | None = None
        client = self.llm_client if options.llm else None
        if client is not None and fusion.candidates:
            started = time.monotonic()

            def a_progress(done: int, total: int) -> None:
                report(
                    "detecting",
                    P_DETECT[0] + 2 + 2 * (done - 1) / max(1, total),
                    f"Avaliando os momentos com o modelo de linguagem ({done} de {total})…",
                )

            assist = run_assist(
                client,
                fusion.candidates,
                job_settings,
                budget_seconds=(
                    self.settings.llm_budget_seconds
                    if options.llm_budget_seconds is None
                    else options.llm_budget_seconds
                ),
                timeout_seconds=self.settings.llm_timeout_seconds,
                cancel=cancel,
                progress=a_progress,
            )
            timings["assist"] = time.monotonic() - started
            notice = assist.notice()
            if notice is not None:
                notices.append(notice)
            check()

        report("detecting", P_DETECT[1], f"{len(fusion.candidates)} momentos encontrados")
        log.info(
            "analysis.done",
            duration=round(duration, 1),
            candidates=len(fusion.candidates),
            audio=audio.available,
            transcript=transcript.available,
            speech=transcript.speech_detected,
            language=transcript.language,
            visual=visual.available,
            llm=llm_result.available or bool(assist and assist.available),
            short_source=short_source,
            timings={k: round(v, 1) for k, v in timings.items()},
        )
        effective = {
            "minClipSeconds": round(fusion_settings.min_clip, 2),
            "targetClipSeconds": round(fusion_settings.target_clip, 2),
            "maxClipSeconds": round(fusion_settings.max_clip, 2),
            "shortSource": short_source,
            "focus": fusion_settings.focus.to_json(),
            "customKeywords": list(job_settings.custom_keywords),
        }
        return AnalysisResult(
            probe=probe,
            duration=duration,
            audio=audio,
            transcript=transcript,
            transcript_series=tseries,
            visual=visual,
            webcam_region=webcam_region,
            llm=llm_result,
            fusion=fusion,
            timings=timings,
            assist=assist,
            notices=notices,
            effective=effective,
        )
