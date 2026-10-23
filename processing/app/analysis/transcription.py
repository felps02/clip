"""Transcription providers (faster-whisper) and transcript-derived series.

The provider protocol keeps the pipeline testable and swappable. Audio is decoded in
chunks (default 5 min, boundaries snapped to silences) so memory stays bounded for long
recordings and progress can be reported ("Transcrevendo áudio (12 de 60 min)…"). A chunk
that fails after the first one is recorded as a gap (``Transcript.gaps``/``coverage``)
instead of silently passing for a complete transcript.

A model that fails to load is not retried by every job, nor cached as broken forever: the
next attempt waits for a backoff (1 min, doubling up to 30 min), so a transient network or
disk problem on the first download heals by itself.
"""

from __future__ import annotations

import bisect
import math
import os
import re
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Protocol, runtime_checkable

import numpy as np

from app.analysis.common import (
    Gaps,
    coverage_fraction,
    covered_mask,
    moving_average,
    robust_rolling_z,
)
from app.analysis.lexicon import KeywordHit, Lexicon, default_lexicon, tokenize
from app.logging_setup import get_logger
from app.media.audio import SAMPLE_RATE, decode_pcm, pcm_to_float
from app.media.ffmpeg import FfmpegError, FfmpegNotFound, FfmpegTools
from app.worker.errors import CancelToken, JobCancelled

# huggingface_hub (imported by faster_whisper) warns on every model download on Windows
# without Developer Mode, where it cannot create symlinks and stores plain copies instead:
# the cache still works, so the warning is only noise in the worker logs. The flag is read
# when huggingface_hub is first imported, hence here, before the lazy faster_whisper import.
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

log = get_logger("transcription")

MODEL_ORDER = ["tiny", "base", "small", "medium", "large-v3", "large"]
LOAD_RETRY_BASE_SECONDS = 60.0
LOAD_RETRY_MAX_SECONDS = 1800.0

# Language detection (``language: "auto"``): once per job, over speech sampled across the
# whole recording, then every chunk is transcribed with that language.
LANGUAGE_DETECTION_WINDOWS = 6  # 30 s windows of speech -> language_detection_segments
LANGUAGE_DETECTION_WINDOW_SECONDS = 30.0
LANGUAGE_DETECTION_THRESHOLD = 0.5  # faster-whisper: a 30 s segment above it decides
LANGUAGE_LOW_CONFIDENCE = 0.5  # below it the transcript is flagged languageConfidence "low"
DETECTION_SPAN_MAX_SECONDS = 90.0  # wall-clock length decoded for one window of speech
DETECTION_SPAN_GAP_SECONDS = 0.5  # silence put between the sampled spans
# Languages never taken from the detection (WHISPER_DETECT_IGNORE): no live stream is in
# Latin or Sanskrit, yet Whisper's language identification falls back to them for accented
# or synthetic speech (base: an English text read by a pt-BR voice -> "la" 0.75). The best
# remaining candidate is used instead, with its own (honest, usually low) probability.
DEFAULT_IGNORED_LANGUAGES = ("la", "sa")


# --------------------------------------------------------------------------------------
# data
# --------------------------------------------------------------------------------------


@dataclass(slots=True)
class Word:
    start: float
    end: float
    word: str
    probability: float = 1.0

    def to_json(self) -> dict[str, Any]:
        return {
            "start": round(self.start, 3),
            "end": round(self.end, 3),
            "word": self.word,
            "probability": round(float(self.probability), 3),
        }


@dataclass(slots=True)
class Segment:
    start: float
    end: float
    text: str
    words: list[Word] = field(default_factory=list)

    def to_json(self) -> dict[str, Any]:
        return {
            "start": round(self.start, 3),
            "end": round(self.end, 3),
            "text": self.text,
            "words": [w.to_json() for w in self.words],
        }


@dataclass(slots=True)
class Transcript:
    available: bool = False
    model: str | None = None
    language: str | None = None
    language_probability: float | None = None
    segments: list[Segment] = field(default_factory=list)
    error: str | None = None
    duration: float = 0.0
    # time ranges that could not be transcribed (failed chunks): the transcript stays
    # available but only covers ``coverage`` of the recording
    gaps: Gaps = field(default_factory=list)
    # False when the voice activity detector found no speech, or the speech it found gave
    # no word (then no language either); None when it could not be checked
    speech_detected: bool | None = None
    speech_seconds: float | None = None  # speech found by the VAD (seconds)
    language_source: str | None = None  # "settings" | "detected"
    language_confidence: str | None = None  # "high" | "low" (detected languages only)
    language_detection: dict[str, Any] | None = None  # sampled windows, top candidates

    @property
    def coverage(self) -> float:
        """Share of the recording that was transcribed (0..1; 0 when unavailable)."""
        if not self.available:
            return 0.0
        return coverage_fraction(self.gaps, 0.0, self.duration) if self.duration > 0 else 1.0

    @property
    def words(self) -> list[Word]:
        return [w for s in self.segments for w in s.words]

    @property
    def text(self) -> str:
        return " ".join(s.text.strip() for s in self.segments if s.text.strip())

    def text_between(self, start: float, end: float) -> str:
        words = [w.word.strip() for w in self.words if w.end > start and w.start < end]
        return " ".join(w for w in words if w)

    def boundaries(self) -> list[float]:
        out: list[float] = []
        for s in self.segments:
            out.append(s.start)
            out.append(s.end)
        return sorted(set(out))

    def to_json(self) -> dict[str, Any]:
        return {
            "available": self.available,
            "model": self.model,
            "language": self.language,
            "languageProbability": (
                round(self.language_probability, 3)
                if self.language_probability is not None
                else None
            ),
            "languageSource": self.language_source,
            "languageConfidence": self.language_confidence,
            "languageDetection": self.language_detection,
            "speechDetected": self.speech_detected,
            "speechSeconds": self.speech_seconds,
            "durationSeconds": round(self.duration, 3),
            "error": self.error,
            "coverage": round(self.coverage, 3),
            "gaps": [[round(s, 2), round(e, 2)] for s, e in self.gaps],
            "segments": [s.to_json() for s in self.segments],
        }

    @classmethod
    def from_json(cls, data: dict[str, Any]) -> Transcript:
        segments: list[Segment] = []
        for seg in data.get("segments", []) or []:
            words = [
                Word(
                    float(w.get("start", 0.0)),
                    float(w.get("end", 0.0)),
                    str(w.get("word", "")),
                    float(w.get("probability", 1.0)),
                )
                for w in seg.get("words", []) or []
            ]
            segments.append(
                Segment(
                    float(seg.get("start", 0.0)),
                    float(seg.get("end", 0.0)),
                    str(seg.get("text", "")),
                    words,
                )
            )
        gaps: Gaps = []
        for gap in data.get("gaps", []) or []:
            if isinstance(gap, list | tuple) and len(gap) == 2:
                gaps.append((float(gap[0]), float(gap[1])))
        speech = data.get("speechDetected")
        speech_seconds = data.get("speechSeconds")
        detection = data.get("languageDetection")
        return cls(
            available=bool(data.get("available", bool(segments))),
            model=data.get("model"),
            language=data.get("language"),
            language_probability=data.get("languageProbability"),
            segments=segments,
            error=data.get("error"),
            duration=float(data.get("durationSeconds", 0.0) or 0.0),
            gaps=gaps,
            speech_detected=speech if isinstance(speech, bool) else None,
            speech_seconds=(
                float(speech_seconds) if isinstance(speech_seconds, int | float) else None
            ),
            language_source=data.get("languageSource"),
            language_confidence=data.get("languageConfidence"),
            language_detection=detection if isinstance(detection, dict) else None,
        )

    @classmethod
    def unavailable(cls, error: str, model: str | None = None) -> Transcript:
        return cls(available=False, model=model, error=error)


@dataclass(slots=True)
class LanguageDetection:
    """Result of the once-per-job language detection over sampled speech."""

    language: str
    probability: float
    candidates: list[tuple[str, float]] = field(default_factory=list)  # top 5
    spans: list[tuple[float, float]] = field(default_factory=list)  # decoded wall-clock spans
    speech_seconds: float = 0.0  # speech inside the spans (VAD)
    ignored: tuple[str, float] | None = None  # Whisper's pick when it is an ignored language

    @property
    def confidence(self) -> str:
        return "low" if self.probability < LANGUAGE_LOW_CONFIDENCE else "high"

    def to_json(self) -> dict[str, Any]:
        return {
            "language": self.language,
            "probability": round(self.probability, 3),
            "confidence": self.confidence,
            "candidates": [[lang, round(p, 3)] for lang, p in self.candidates[:5]],
            "spans": [[round(s, 2), round(e, 2)] for s, e in self.spans],
            "speechSeconds": round(self.speech_seconds, 1),
            "ignored": (
                {"language": self.ignored[0], "probability": round(self.ignored[1], 3)}
                if self.ignored
                else None
            ),
            "method": "faster-whisper detect_language (vad_filter, "
            f"{len(self.spans)} x {LANGUAGE_DETECTION_WINDOW_SECONDS:.0f}s windows of speech)",
        }


# --------------------------------------------------------------------------------------
# providers
# --------------------------------------------------------------------------------------


@runtime_checkable
class TranscriptionProvider(Protocol):
    name: str

    def available(self) -> bool: ...

    def transcribe(
        self,
        audio: np.ndarray,
        *,
        language: str | None = None,
        offset: float = 0.0,
        cancel: CancelToken | None = None,
    ) -> Transcript: ...


class UnavailableProvider:
    """Provider used when transcription is disabled or the model cannot be loaded."""

    name = "unavailable"

    def __init__(self, reason: str, model: str | None = None) -> None:
        self.reason = reason
        self.model = model

    def available(self) -> bool:
        return False

    def transcribe(
        self,
        audio: np.ndarray,
        *,
        language: str | None = None,
        offset: float = 0.0,
        cancel: CancelToken | None = None,
    ) -> Transcript:
        return Transcript.unavailable(self.reason, self.model)


def bound_model(requested: str | None, maximum: str) -> str:
    """Pick the requested model size bounded by the configured maximum."""
    maximum = (maximum or "base").strip().lower()
    requested = (requested or maximum).strip().lower()

    def rank(name: str) -> int:
        return MODEL_ORDER.index(name) if name in MODEL_ORDER else len(MODEL_ORDER)

    if rank(requested) == len(MODEL_ORDER):
        return maximum if rank(maximum) < len(MODEL_ORDER) else "base"
    return requested if rank(requested) <= rank(maximum) else maximum


class FasterWhisperProvider:
    """faster-whisper (CTranslate2) with word timestamps and VAD filter.

    A failed model load is remembered for a backoff period only (``LOAD_RETRY_BASE_SECONDS``
    doubling per consecutive failure up to ``LOAD_RETRY_MAX_SECONDS``): jobs in between
    report the transcript as unavailable without waiting for another failing download,
    and the first job after the backoff tries again.
    """

    name = "faster-whisper"

    def __init__(
        self,
        model_size: str = "base",
        *,
        device: str = "cpu",
        compute_type: str = "int8",
        download_root: str | Path | None = None,
        local_files_only: bool = False,
        cpu_threads: int = 0,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self.model_size = model_size
        self.device = device
        self.compute_type = compute_type
        self.download_root = str(download_root) if download_root else None
        self.local_files_only = local_files_only
        self.cpu_threads = cpu_threads
        self._model: Any = None
        self._error: str | None = None
        self._failures = 0
        self._retry_at = 0.0
        self._clock = clock
        self._lock = threading.Lock()

    @property
    def model_name(self) -> str:
        return f"faster-whisper/{self.model_size}"

    @property
    def loaded(self) -> bool:
        return self._model is not None

    @property
    def error(self) -> str | None:
        return self._error

    @property
    def failures(self) -> int:
        """Consecutive failed load attempts (reset by a successful load)."""
        return self._failures

    @property
    def retry_in(self) -> float | None:
        """Seconds until the next load attempt after a failure; ``None`` when no failure is
        pending (loaded, or never tried)."""
        if self._model is not None or self._error is None:
            return None
        return max(0.0, self._retry_at - self._clock())

    def load(self) -> bool:
        with self._lock:
            if self._model is not None:
                return True
            if self._error is not None and self._clock() < self._retry_at:
                return False
            try:
                from faster_whisper import WhisperModel

                started = time.monotonic()
                self._model = WhisperModel(
                    self.model_size,
                    device=self.device,
                    compute_type=self.compute_type,
                    download_root=self.download_root,
                    local_files_only=self.local_files_only,
                    cpu_threads=self.cpu_threads,
                )
            except Exception as exc:
                self._failures += 1
                delay = min(
                    LOAD_RETRY_MAX_SECONDS, LOAD_RETRY_BASE_SECONDS * 2 ** (self._failures - 1)
                )
                self._retry_at = self._clock() + delay
                self._error = f"{type(exc).__name__}: {exc}"[:300]
                log.warning(
                    "whisper.unavailable",
                    model=self.model_size,
                    error=self._error,
                    failures=self._failures,
                    retry_in=round(delay),
                )
                return False
            if self._error is not None:
                log.info("whisper.recovered", model=self.model_size, failures=self._failures)
            self._error = None
            self._failures = 0
            log.info(
                "whisper.loaded",
                model=self.model_size,
                seconds=round(time.monotonic() - started, 1),
            )
            return True

    def available(self) -> bool:
        return self.load()

    def speech_timestamps(self, audio: np.ndarray) -> list[tuple[float, float]]:
        """Speech regions in seconds from the start of ``audio``: the Silero VAD bundled with
        faster-whisper, with the options ``transcribe(vad_filter=True)`` uses (so a region
        found here is a region the transcription decodes)."""
        from faster_whisper.vad import VadOptions, get_speech_timestamps

        samples = np.asarray(audio, dtype=np.float32)
        if samples.size < SAMPLE_RATE // 10:
            return []
        chunks = get_speech_timestamps(samples, VadOptions(), sampling_rate=SAMPLE_RATE)
        return [(c["start"] / SAMPLE_RATE, c["end"] / SAMPLE_RATE) for c in chunks]

    def detect_language(
        self, audio: np.ndarray, *, segments: int, threshold: float = LANGUAGE_DETECTION_THRESHOLD
    ) -> tuple[str, float, list[tuple[str, float]]] | None:
        """``WhisperModel.detect_language(audio, vad_filter=True, language_detection_segments=
        segments, language_detection_threshold=threshold)``: ``(language, probability, top 5
        candidates)`` or ``None`` when the model cannot answer."""
        if not self.load():
            return None
        model = self._model
        if not getattr(getattr(model, "model", None), "is_multilingual", True):
            return "en", 1.0, [("en", 1.0)]
        try:
            language, probability, ranked = model.detect_language(
                np.asarray(audio, dtype=np.float32),
                vad_filter=True,
                language_detection_segments=max(1, int(segments)),
                language_detection_threshold=threshold,
            )
        except Exception as exc:
            log.warning(
                "whisper.language_detection_failed", error=f"{type(exc).__name__}: {exc}"[:300]
            )
            return None
        top = [(str(lang), float(p)) for lang, p in list(ranked or [])[:20]]
        return str(language), float(probability), top

    def transcribe(
        self,
        audio: np.ndarray,
        *,
        language: str | None = None,
        offset: float = 0.0,
        cancel: CancelToken | None = None,
    ) -> Transcript:
        if not self.load():
            return Transcript.unavailable(self._error or "model not loaded", self.model_name)
        audio = np.asarray(audio, dtype=np.float32)
        duration = float(audio.size) / SAMPLE_RATE
        if audio.size < SAMPLE_RATE // 2:
            return Transcript(
                available=True, model=self.model_name, language=language, duration=duration
            )
        try:
            segments_iter, info = self._model.transcribe(
                audio,
                language=language,
                task="transcribe",
                beam_size=5,
                word_timestamps=True,
                vad_filter=True,
                condition_on_previous_text=False,
            )
            segments: list[Segment] = []
            for seg in segments_iter:
                if cancel is not None:
                    cancel.raise_if_cancelled()
                words = [
                    Word(
                        start=float(w.start) + offset,
                        end=float(w.end) + offset,
                        word=str(w.word),
                        probability=float(getattr(w, "probability", 1.0) or 0.0),
                    )
                    for w in (seg.words or [])
                ]
                segments.append(
                    Segment(
                        start=float(seg.start) + offset,
                        end=float(seg.end) + offset,
                        text=str(seg.text).strip(),
                        words=words,
                    )
                )
        except JobCancelled:
            raise
        except Exception as exc:
            err = f"{type(exc).__name__}: {exc}"[:300]
            log.warning("whisper.failed", error=err)
            return Transcript.unavailable(err, self.model_name)
        return Transcript(
            available=True,
            model=self.model_name,
            language=getattr(info, "language", language),
            language_probability=float(getattr(info, "language_probability", 0.0) or 0.0),
            segments=segments,
            duration=duration,
        )


class TranscriptionService:
    """Creates/caches providers per model size (one loaded model at a time)."""

    def __init__(self, settings: Any) -> None:
        self.settings = settings
        self._providers: dict[str, FasterWhisperProvider] = {}
        self._lock = threading.Lock()

    @property
    def enabled(self) -> bool:
        return bool(self.settings.transcription_enabled)

    def provider_for(self, requested_model: str | None) -> TranscriptionProvider:
        if not self.enabled:
            return UnavailableProvider("transcription disabled (WHISPER_ENABLED=false)")
        model = bound_model(requested_model, self.settings.whisper_model)
        with self._lock:
            provider = self._providers.get(model)
            if provider is None:
                # keep memory bounded: drop other loaded models
                self._providers.clear()
                provider = FasterWhisperProvider(
                    model,
                    device=self.settings.whisper_device,
                    compute_type=self.settings.whisper_compute_type,
                    download_root=self.settings.models_dir,
                    local_files_only=self.settings.whisper_local_files_only,
                    cpu_threads=self.settings.whisper_cpu_threads,
                )
                self._providers[model] = provider
            return provider

    def status(self) -> dict[str, Any]:
        providers = list(self._providers.items())
        loaded = any(p.loaded for _, p in providers)
        errors = {m: p.error for m, p in providers if p.error}
        retry = {m: round(p.retry_in, 1) for m, p in providers if p.retry_in is not None}
        return {
            "enabled": self.enabled,
            "model": self.settings.whisper_model,
            "loaded": loaded,
            "device": self.settings.whisper_device,
            "computeType": self.settings.whisper_compute_type,
            "modelsDir": self.settings.models_dir,
            "errors": errors or None,
            # seconds until the next load attempt of a model that failed to load (0 = the
            # next job retries)
            "retryInSeconds": retry or None,
        }


# --------------------------------------------------------------------------------------
# chunked transcription of a media file
# --------------------------------------------------------------------------------------


def chunk_boundaries(
    duration: float, chunk_seconds: float, silences: list[tuple[float, float]] | None
) -> list[tuple[float, float]]:
    """Split ``[0, duration]`` into chunks of ~``chunk_seconds`` snapped to silences."""
    duration = max(0.0, float(duration))
    if duration <= chunk_seconds * 1.5:
        return [(0.0, duration)] if duration > 0 else []
    mids = np.array([(s + e) / 2.0 for s, e in (silences or []) if e - s >= 0.3], dtype=np.float64)
    bounds = [0.0]
    cursor = 0.0
    while duration - cursor > chunk_seconds * 1.5:
        target = cursor + chunk_seconds
        if mids.size:
            window = mids[
                (mids > target - chunk_seconds * 0.2) & (mids < target + chunk_seconds * 0.2)
            ]
            if window.size:
                target = float(window[np.argmin(np.abs(window - target))])
        bounds.append(target)
        cursor = target
    bounds.append(duration)
    return [(bounds[i], bounds[i + 1]) for i in range(len(bounds) - 1)]


PhaseFn = Callable[[str, float], None]


def merge_regions(
    regions: list[tuple[float, float]], gap: float = 0.0
) -> list[tuple[float, float]]:
    """Sorted, overlapping (or closer than ``gap``) regions merged."""
    out: list[tuple[float, float]] = []
    for start, end in sorted((float(s), float(e)) for s, e in regions if e > s):
        if out and start - out[-1][1] <= gap:
            out[-1] = (out[-1][0], max(out[-1][1], end))
        else:
            out.append((start, end))
    return out


def _speech_between(regions: list[tuple[float, float]], start: float, end: float) -> float:
    return sum(max(0.0, min(e, end) - max(s, start)) for s, e in regions)


def _wall_time(regions: list[tuple[float, float]], speech_time: float) -> float:
    """Wall-clock time at which ``speech_time`` seconds of speech have been spoken."""
    elapsed = 0.0
    for start, end in regions:
        length = end - start
        if elapsed + length >= speech_time:
            return start + max(0.0, speech_time - elapsed)
        elapsed += length
    return regions[-1][1] if regions else 0.0


def detection_spans(
    regions: list[tuple[float, float]],
    *,
    count: int = LANGUAGE_DETECTION_WINDOWS,
    window: float = LANGUAGE_DETECTION_WINDOW_SECONDS,
    max_span: float = DETECTION_SPAN_MAX_SECONDS,
) -> list[tuple[float, float]]:
    """Wall-clock spans holding ~``window`` seconds of speech each, spread evenly over the
    speech of the whole recording (not only its beginning): up to ``count`` spans, all the
    speech when there is less than ``count`` windows of it. Each span is at most
    ``max_span`` long (sparse speech). Ordered by speech density, densest first, because
    faster-whisper stops at the first 30 s segment whose language is clear enough."""
    merged = merge_regions(regions)
    total = sum(e - s for s, e in merged)
    if total <= 0.0:
        return []
    n = max(1, min(int(count), int(math.ceil(total / window))))
    if total <= n * window:
        offsets = [i * window for i in range(n)]
    else:
        step = (total - window) / (n - 1) if n > 1 else 0.0
        offsets = [i * step for i in range(n)]
    # speech time at the end of each region (to keep a window inside its part of speech)
    ends: list[float] = []
    for s, e in merged:
        ends.append((ends[-1] if ends else 0.0) + (e - s))
    spans: list[tuple[float, float]] = []
    for offset in offsets:
        start = _wall_time(merged, offset)
        end = _wall_time(merged, min(total, offset + window))
        if end - start > max_span:
            # the window reaches across a long pause: take the window of speech that ends
            # where the speech around ``offset`` ends instead, when that stays compact
            region_end = next(t for t in ends if t >= offset - 1e-9)
            back = _wall_time(merged, max(0.0, region_end - window))
            stop = _wall_time(merged, region_end)
            if stop - back <= max_span:
                start, end = back, stop
        end = min(end, start + max_span)
        if end - start > 0.05 and (not spans or start >= spans[-1][1] - 1e-6):
            spans.append((start, end))
    spans.sort(key=lambda span: (-_speech_between(merged, *span) / (span[1] - span[0]), span[0]))
    return spans


def scan_speech(
    tools: FfmpegTools,
    path: str | Path,
    chunks: list[tuple[float, float]],
    vad: Callable[[np.ndarray], list[tuple[float, float]]],
    *,
    cancel: CancelToken | None = None,
    on_progress: Callable[[float], None] | None = None,
    media_offset: float = 0.0,
) -> list[list[tuple[float, float]] | None]:
    """Speech regions (seconds of the analysis) of every chunk; ``None`` for a chunk that
    could not be decoded or checked (it is then transcribed as before, and fails there if it
    must). ``media_offset``: seconds of the file before the analysis origin."""
    out: list[list[tuple[float, float]] | None] = []
    for index, (start, end) in enumerate(chunks):
        if cancel is not None:
            cancel.raise_if_cancelled()
        try:
            pcm = decode_pcm(
                tools,
                path,
                sample_rate=SAMPLE_RATE,
                start=media_offset + start,
                duration=end - start,
                cancel=cancel,
            )
            regions = vad(pcm_to_float(pcm))
        except FfmpegNotFound, JobCancelled:
            raise
        except Exception as exc:  # decoder error/timeout, VAD failure
            log.warning("transcription.vad_failed", start=start, end=end, error=str(exc)[:200])
            out.append(None)
        else:
            out.append([(start + s, min(end, start + e)) for s, e in regions if e > s])
        if on_progress is not None:
            on_progress((index + 1) / max(1, len(chunks)))
    return out


def _detect_language(
    tools: FfmpegTools,
    path: str | Path,
    provider: Any,
    regions: list[tuple[float, float]],
    cancel: CancelToken | None,
    ignore: tuple[str, ...] = DEFAULT_IGNORED_LANGUAGES,
    media_offset: float = 0.0,
) -> LanguageDetection | None:
    spans = detection_spans(regions)
    if not spans:
        return None
    gap = np.zeros(int(DETECTION_SPAN_GAP_SECONDS * SAMPLE_RATE), dtype=np.float32)
    parts: list[np.ndarray] = []
    for start, end in spans:
        if cancel is not None:
            cancel.raise_if_cancelled()
        try:
            pcm = decode_pcm(
                tools,
                path,
                sample_rate=SAMPLE_RATE,
                start=media_offset + start,
                duration=end - start,
                cancel=cancel,
            )
        except FfmpegNotFound:
            raise
        except FfmpegError as exc:
            log.warning("transcription.detection_decode_failed", start=start, error=str(exc)[:200])
            continue
        parts.extend((pcm_to_float(pcm), gap))
    if not parts:
        return None
    answer = provider.detect_language(np.concatenate(parts), segments=len(spans))
    if not answer:
        return None
    language, probability, candidates = answer
    ignored: tuple[str, float] | None = None
    if language in ignore:
        allowed = [(lang, p) for lang, p in candidates if lang not in ignore]
        if not allowed:
            return None
        ignored = (str(language), float(probability))
        language, probability = allowed[0]
    merged = merge_regions(regions)
    return LanguageDetection(
        language=str(language),
        probability=float(probability),
        candidates=list(candidates),
        spans=sorted(spans),
        speech_seconds=sum(_speech_between(merged, s, e) for s, e in spans),
        ignored=ignored,
    )


def transcribe_media(
    tools: FfmpegTools,
    path: str | Path,
    duration: float,
    provider: TranscriptionProvider,
    *,
    language: str | None,
    silences: list[tuple[float, float]] | None = None,
    chunk_seconds: float = 300.0,
    progress: Callable[[float, float], None] | None = None,
    cancel: CancelToken | None = None,
    on_phase: PhaseFn | None = None,
    ignore_languages: tuple[str, ...] = DEFAULT_IGNORED_LANGUAGES,
    media_offset: float = 0.0,
) -> Transcript:
    """Decode the audio in chunks and transcribe each one; merges the segments.

    With a provider that has a voice activity detector (``speech_timestamps``) the chunks
    are first scanned for speech: no speech at all -> an available transcript with
    ``speech_detected=False``, no language and nothing transcribed; chunks without speech
    are skipped (not gaps). With ``language=None`` the language is then detected **once**
    (``detect_language`` over up to 6 windows of 30 s of speech spread over the recording;
    a language of ``ignore_languages`` is replaced by the best other candidate) and every
    chunk is transcribed with it.

    The first chunk failing makes the transcript unavailable (the model is unusable); a
    later chunk that fails (decode error/timeout, model error) becomes a gap: the
    transcript stays available with ``gaps``/``coverage`` and an ``error`` describing the
    missing part, instead of passing for a complete transcript.

    ``media_offset``: seconds of the file before the analysis origin (a live window skips
    the beginning of its file); ``duration`` and every time of the transcript are counted
    from that origin.
    """
    media_offset = max(0.0, float(media_offset))
    if not provider.available():
        probe = provider.transcribe(np.zeros(0, dtype=np.float32), language=language)
        return Transcript.unavailable(probe.error or "transcription unavailable", probe.model)

    def phase(name: str, fraction: float) -> None:
        if on_phase is not None:
            on_phase(name, min(1.0, max(0.0, fraction)))

    model_name = getattr(provider, "model_name", None)
    chunks = chunk_boundaries(duration, chunk_seconds, silences)
    speech_by_chunk: list[list[tuple[float, float]] | None] | None = None
    vad = getattr(provider, "speech_timestamps", None)
    if callable(vad) and chunks:
        phase("speech", 0.0)
        speech_by_chunk = scan_speech(
            tools,
            path,
            chunks,
            vad,
            cancel=cancel,
            on_progress=lambda f: phase("speech", f),
            media_offset=media_offset,
        )
    regions = [r for found in speech_by_chunk or [] if found for r in found]
    speech_seconds = (
        round(sum(e - s for s, e in merge_regions(regions)), 2)
        if speech_by_chunk is not None
        else None
    )
    if speech_by_chunk is not None and not regions and all(f is not None for f in speech_by_chunk):
        # no speech anywhere: nothing to detect or transcribe (no language made up from noise)
        return Transcript(
            available=True,
            model=model_name,
            language=None,
            duration=duration,
            speech_detected=False,
            speech_seconds=0.0,
            language_source="settings" if language else None,
        )

    detection: LanguageDetection | None = None
    if language is None and regions and callable(getattr(provider, "detect_language", None)):
        phase("language", 0.0)
        detection = _detect_language(
            tools, path, provider, regions, cancel, ignore_languages, media_offset
        )
        phase("language", 1.0)
        if detection is not None:
            log.info(
                "transcription.language_detected",
                language=detection.language,
                probability=round(detection.probability, 3),
                spans=len(detection.spans),
            )
    fixed_language = language or (detection.language if detection else None)

    merged = Transcript(available=True, model=model_name, language=language, duration=duration)
    detected_language: str | None = None
    detected_prob: float | None = None
    last_error: str | None = None
    attempted = 0
    for index, (start, end) in enumerate(chunks):
        if cancel is not None:
            cancel.raise_if_cancelled()
        found = speech_by_chunk[index] if speech_by_chunk is not None else None
        if found is not None and not found:
            # the VAD found no speech here: nothing to transcribe (not a gap)
            if progress is not None:
                progress(end, duration)
            continue
        attempted += 1
        try:
            pcm = decode_pcm(
                tools,
                path,
                sample_rate=SAMPLE_RATE,
                start=media_offset + start,
                duration=end - start,
                cancel=cancel,
            )
        except FfmpegNotFound:
            raise
        except FfmpegError as exc:  # includes FfmpegTimeout
            part = Transcript.unavailable(f"{type(exc).__name__}: {exc}"[:300])
        else:
            part = provider.transcribe(
                pcm_to_float(pcm),
                language=fixed_language or detected_language,
                offset=start,
                cancel=cancel,
            )
        if not part.available:
            if attempted == 1:
                return Transcript.unavailable(part.error or "transcription failed", part.model)
            log.warning("transcription.chunk_failed", start=start, end=end, error=part.error)
            merged.gaps.append((float(start), float(end)))
            last_error = part.error or "transcription failed"
            continue
        merged.model = part.model or merged.model
        if detected_language is None and part.language:
            detected_language = part.language
            detected_prob = part.language_probability
        merged.segments.extend(part.segments)
        if progress is not None:
            progress(end, duration)
    merged.segments.sort(key=lambda s: s.start)
    merged.speech_seconds = speech_seconds
    merged.language_detection = detection.to_json() if detection else None
    if merged.words:
        merged.speech_detected = True
        if language:
            merged.language = language
            merged.language_source = "settings"
        elif detection is not None:
            merged.language = detection.language
            merged.language_probability = detection.probability
            merged.language_source = "detected"
            merged.language_confidence = detection.confidence
        else:  # provider without detect_language: the first chunk's detection
            merged.language = detected_language
            merged.language_probability = detected_prob
            merged.language_source = "detected"
            if detected_prob is not None:
                merged.language_confidence = (
                    "low" if detected_prob < LANGUAGE_LOW_CONFIDENCE else "high"
                )
    else:
        # speech-like sound that gave no word (music, noise): no language either
        merged.speech_detected = False
        merged.language = None
        merged.language_source = "settings" if language else None
    if merged.gaps:
        merged.error = (
            f"{len(merged.gaps)} of {len(chunks)} chunks not transcribed "
            f"(coverage {merged.coverage:.0%}): {last_error}"
        )[:300]
    return merged


# --------------------------------------------------------------------------------------
# derived series
# --------------------------------------------------------------------------------------


@dataclass(slots=True)
class TimedKeyword:
    time: float
    end: float
    phrase: str  # as spoken (lower-case, punctuation stripped)
    category: str
    weight: float
    lemma: str = ""  # normalised lexicon phrase that matched
    custom: bool = False  # one of the user's keywords (settings.customKeywords)

    def to_json(self) -> dict[str, Any]:
        data: dict[str, Any] = {
            "time": round(self.time, 2),
            "word": self.phrase,
            "category": self.category,
            "weight": self.weight,
        }
        if self.custom:
            data["custom"] = True
        return data


def keyword_score_series(
    keywords: list[TimedKeyword], n: int, multipliers: dict[str, float] | None = None
) -> np.ndarray:
    """Per-second keyword score: each hit adds its weight (times the multiplier of its
    category, when given) to its second, half of it to the next and a quarter to the one
    before."""
    score = np.zeros(max(0, int(n)))
    for kw in keywords:
        weight = kw.weight * (multipliers.get(kw.category, 1.0) if multipliers else 1.0)
        idx = int(kw.time)
        if 0 <= idx < len(score):
            score[idx] += weight
            if idx + 1 < len(score):
                score[idx + 1] += weight * 0.5
            if idx - 1 >= 0:
                score[idx - 1] += weight * 0.25
    return score


@dataclass(slots=True)
class TranscriptSeries:
    available: bool
    model: str | None
    language: str | None
    seconds: int
    speech_rate: np.ndarray = field(default_factory=lambda: np.zeros(0))
    speech_rate_z: np.ndarray = field(default_factory=lambda: np.zeros(0))
    keyword_score: np.ndarray = field(default_factory=lambda: np.zeros(0))
    exclamation: np.ndarray = field(default_factory=lambda: np.zeros(0))
    pause_before: np.ndarray = field(default_factory=lambda: np.zeros(0))
    keywords: list[TimedKeyword] = field(default_factory=list)
    boundaries: list[float] = field(default_factory=list)
    words: list[Word] = field(default_factory=list)
    error: str | None = None
    gaps: Gaps = field(default_factory=list)  # not transcribed (see ``Transcript.gaps``)
    speech_detected: bool | None = None  # see ``Transcript.speech_detected``
    language_probability: float | None = None
    language_confidence: str | None = None
    _starts: list[float] | None = None  # word start times (bisect cache)

    @property
    def coverage(self) -> float:
        if not self.available:
            return 0.0
        return coverage_fraction(self.gaps, 0.0, float(self.seconds)) if self.seconds else 1.0

    @property
    def has_speech(self) -> bool:
        """Available and not known to be speechless."""
        return self.available and self.speech_detected is not False

    def keywords_between(self, start: float, end: float) -> list[TimedKeyword]:
        return [k for k in self.keywords if start <= k.time < end]

    def text_between(self, start: float, end: float) -> str:
        """Words overlapping ``[start, end)`` (binary search over the start times: the words
        are in time order)."""
        if self._starts is None or len(self._starts) != len(self.words):
            self._starts = [w.start for w in self.words]
        hi = bisect.bisect_left(self._starts, end)
        lo = bisect.bisect_left(self._starts, start - 30.0)  # no word lasts 30 s
        words = [w.word.strip() for w in self.words[lo:hi] if w.end > start and w.start < end]
        return " ".join(w for w in words if w)

    def to_json(self) -> dict[str, Any]:
        from app.analysis.common import rounded

        data: dict[str, Any] = {
            "available": self.available,
            "model": self.model,
            "language": self.language,
        }
        if self.error:
            data["error"] = self.error
        if self.available:
            data.update(
                {
                    "speechDetected": self.speech_detected,
                    "languageProbability": (
                        round(self.language_probability, 3)
                        if self.language_probability is not None
                        else None
                    ),
                    "languageConfidence": self.language_confidence,
                    "coverage": round(self.coverage, 3),
                    "gaps": [[round(s, 2), round(e, 2)] for s, e in self.gaps],
                    "speechRate": rounded(self.speech_rate, 2),
                    "speechRateZ": rounded(self.speech_rate_z),
                    "keywordScore": rounded(self.keyword_score, 2),
                    "exclamation": rounded(self.exclamation, 0),
                    "pauseBefore": rounded(self.pause_before, 0),
                    "keywords": [k.to_json() for k in self.keywords],
                }
            )
        return data


_SPOKEN_STRIP = re.compile(r"[^\w\s'-]+", re.UNICODE)


def _spoken_form(word: str) -> str:
    return _SPOKEN_STRIP.sub("", word).strip().lower()


def transcript_series(
    transcript: Transcript,
    duration: float,
    lexicon: Lexicon | None = None,
    *,
    audio_burst: np.ndarray | None = None,
) -> TranscriptSeries:
    """Per-second series derived from the transcript. ``lexicon`` defaults to the built-in
    one; pass :func:`app.analysis.lexicon.with_custom_keywords` to also match the user's
    keywords (flagged ``custom``)."""
    n_sec = max(1, int(math.ceil(max(float(duration or 0.0), transcript.duration))))
    if not transcript.available:
        return TranscriptSeries(
            False, transcript.model, transcript.language, n_sec, error=transcript.error
        )
    lexicon = lexicon or default_lexicon()
    words = transcript.words

    counts = np.zeros(n_sec)
    for w in words:
        mid = (w.start + w.end) / 2.0
        idx = int(mid)
        if 0 <= idx < n_sec:
            counts[idx] += 1.0
    speech_rate = moving_average(counts, 3) if n_sec > 1 else counts
    speech_rate_z = robust_rolling_z(speech_rate, 91, min_sigma=0.5) if words else np.zeros(n_sec)

    keywords: list[TimedKeyword] = []
    tokens: list[str] = []
    token_words: list[Word] = []
    for w in words:
        for tok in tokenize(w.word):
            tokens.append(tok)
            token_words.append(w)
    hits: list[KeywordHit] = lexicon.match_tokens(tokens)
    for hit in hits:
        first = token_words[hit.index]
        last = token_words[min(hit.index + hit.length - 1, len(token_words) - 1)]
        span = token_words[hit.index : hit.index + hit.length]
        spoken = " ".join(dict.fromkeys(_spoken_form(w.word) for w in span)).strip() or hit.phrase
        keywords.append(
            TimedKeyword(
                first.start, last.end, spoken, hit.category, hit.weight, hit.phrase, hit.custom
            )
        )
    keyword_score = keyword_score_series(keywords, n_sec)

    exclamation = np.zeros(n_sec)
    for seg in transcript.segments:
        text = seg.text
        bangs = text.count("!") + text.count("?!")
        if bangs:
            idx = int(min(seg.end, max(seg.start, (seg.start + seg.end) / 2.0)))
            if 0 <= idx < n_sec:
                exclamation[idx] += bangs

    pause_before = np.zeros(n_sec)
    if words:
        cumulative = np.concatenate([[0.0], np.cumsum(counts)])
        for s in range(3, n_sec):
            before = cumulative[s - 1] - cumulative[max(0, s - 3)]  # words in [s-3, s-1)
            after = cumulative[min(n_sec, s + 2)] - cumulative[s]  # words in [s, s+2)
            burst_here = bool(
                audio_burst is not None and s < len(audio_burst) and audio_burst[s] > 0
            )
            if before == 0 and (after >= 2 or burst_here):
                pause_before[s] = 1.0

    if transcript.gaps:
        # an untranscribed chunk is not a silence: no "pause then reaction" right after it
        covered = covered_mask(transcript.gaps, n_sec)
        for s in np.flatnonzero(pause_before):
            if not covered[max(0, s - 3) : s].all():
                pause_before[s] = 0.0

    return TranscriptSeries(
        available=True,
        model=transcript.model,
        language=transcript.language,
        seconds=n_sec,
        speech_rate=speech_rate,
        speech_rate_z=speech_rate_z,
        keyword_score=keyword_score,
        exclamation=exclamation,
        pause_before=pause_before,
        keywords=keywords,
        boundaries=transcript.boundaries(),
        words=words,
        error=transcript.error,
        gaps=list(transcript.gaps),
        speech_detected=transcript.speech_detected,
        language_probability=transcript.language_probability,
        language_confidence=transcript.language_confidence,
    )
