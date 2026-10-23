"""Pydantic models mirroring the internal API contract (``docs/API.md``).

All models serialise as ``camelCase`` (the API convention) and accept both the alias and
the python name on input. Unknown fields sent by the API are ignored so that the worker
keeps working while the contract grows.
"""

from __future__ import annotations

import math
import re
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator
from pydantic.alias_generators import to_camel

AspectRatio = Literal["9:16", "16:9", "1:1"]
SubtitleStyle = Literal["bold", "clean", "boxed"]
SubtitlePosition = Literal["bottom", "center", "top"]
FramingMode = Literal["auto", "center", "webcam-split"]
JobKind = Literal["analyze_source", "render_clip", "live_segment"]
Category = Literal["highlight", "reaction", "funny", "hype", "tension", "gameplay", "quote"]
JobStatus = Literal[
    "pending",
    "queued",
    "preparing_media",
    "analyzing",
    "detecting_moments",
    "generating_clips",
    "finalizing",
    "completed",
    "failed",
    "cancelled",
]

# Job ids are GUIDs; the worker uses them as a URL path segment and as the name of the job's
# work directory (``WORK_DIR/{jobId}``, removed with ``rmtree`` at the end), so an id must be
# a plain single path segment: no ``.``/``..``, no drive or stream separators (``:``), no
# separators of any kind (``docs/PROCESSING_PIPELINE.md``).
SAFE_JOB_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$")

CATEGORIES: tuple[str, ...] = (
    "highlight",
    "reaction",
    "funny",
    "hype",
    "tension",
    "gameplay",
    "quote",
)

# AnalysisSettings limits shared with the API (round 3): the API validates them, the worker
# parses tolerantly (drops what it does not understand, cuts what is too long).
MAX_CUSTOM_KEYWORDS = 20
CUSTOM_KEYWORD_MAX_CHARS = 40
INSTRUCTIONS_MAX_CHARS = 500
_LIST_SPLIT_RE = re.compile(r"[,;\n]+")
_KEYWORD_KEY_RE = re.compile(r"[^\w]+", re.UNICODE)


class CamelModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        validate_by_name=True,
        validate_by_alias=True,
        serialize_by_alias=True,
        extra="ignore",
    )

    def to_api(self) -> dict[str, Any]:
        """camelCase dict ready for ``json=``."""
        return self.model_dump(mode="json", by_alias=True)


# --------------------------------------------------------------------------------------
# settings carried by the job payload
# --------------------------------------------------------------------------------------


class WebcamRegion(CamelModel):
    x: float
    y: float
    w: float
    h: float
    confidence: float | None = None


class SubtitleSettings(CamelModel):
    enabled: bool = True
    style: SubtitleStyle = "bold"
    position: SubtitlePosition = "bottom"


class FramingSettings(CamelModel):
    mode: FramingMode = "auto"
    focus_x: float = 0.5
    focus_y: float = 0.5
    webcam_region: WebcamRegion | None = None

    @model_validator(mode="after")
    def _clamp(self) -> FramingSettings:
        self.focus_x = min(1.0, max(0.0, float(self.focus_x)))
        self.focus_y = min(1.0, max(0.0, float(self.focus_y)))
        return self


def _as_items(value: Any) -> list[str]:
    """A list of strings from whatever the payload carries: ``None`` -> ``[]``, a string ->
    its comma/semicolon/newline separated parts, numbers -> their text; nested values are
    ignored."""
    if value is None:
        return []
    if isinstance(value, str):
        return _LIST_SPLIT_RE.split(value)
    if isinstance(value, list | tuple | set):
        return [str(item) for item in value if isinstance(item, str | int | float)]
    return []


def parse_focus_categories(value: Any) -> list[str]:
    """Known categories only (lower-cased, deduplicated, payload order kept)."""
    out: list[str] = []
    for item in _as_items(value):
        category = item.strip().lower()
        if category in CATEGORIES and category not in out:
            out.append(category)
    return out


def _keyword_key(keyword: str) -> str:
    """Comparison key of a custom keyword: case, accents and punctuation ignored (the way
    the lexicon matches them)."""
    import unicodedata

    decomposed = unicodedata.normalize("NFKD", keyword.casefold())
    plain = "".join(ch for ch in decomposed if not unicodedata.combining(ch))
    return " ".join(_KEYWORD_KEY_RE.sub(" ", plain).split())


def parse_custom_keywords(value: Any) -> list[str]:
    """Trimmed, whitespace collapsed, cut to 40 characters, deduplicated without regard to
    case and accents (they are matched that way), at most 20; entries without a letter or
    a digit are dropped."""
    out: list[str] = []
    seen: set[str] = set()
    for item in _as_items(value):
        keyword = " ".join(item.split())[:CUSTOM_KEYWORD_MAX_CHARS].strip()
        key = _keyword_key(keyword)
        if not key or key in seen:
            continue
        seen.add(key)
        out.append(keyword)
        if len(out) == MAX_CUSTOM_KEYWORDS:
            break
    return out


def parse_instructions(value: Any) -> str | None:
    """Trimmed, cut to 500 characters; empty -> ``None``."""
    if value is None or not isinstance(value, str | int | float):
        return None
    text = str(value).strip()[:INSTRUCTIONS_MAX_CHARS].strip()
    return text or None


class AnalysisSettings(CamelModel):
    min_clip_seconds: float = 15
    max_clip_seconds: float = 60
    target_clip_seconds: float = 30
    sensitivity: int = 50
    max_candidates: int = 20
    # clips rendered by the analysis; absent = every candidate (``maxCandidates``). Always an
    # int after validation (see ``render_top``).
    auto_render_top: int | None = None
    aspect_ratio: AspectRatio = "9:16"
    subtitles: SubtitleSettings = Field(default_factory=SubtitleSettings)
    language: str = "auto"
    framing: FramingSettings = Field(default_factory=FramingSettings)
    title: str | None = None
    # round 3: categories the user wants more of (fusion weights + rank priority), words to
    # look for in the transcript, and free-text instructions (used by an LLM provider only)
    focus_categories: list[str] = Field(default_factory=list)
    custom_keywords: list[str] = Field(default_factory=list)
    instructions: str | None = None

    @field_validator("focus_categories", mode="before")
    @classmethod
    def _focus(cls, value: Any) -> list[str]:
        return parse_focus_categories(value)

    @field_validator("custom_keywords", mode="before")
    @classmethod
    def _keywords(cls, value: Any) -> list[str]:
        return parse_custom_keywords(value)

    @field_validator("instructions", mode="before")
    @classmethod
    def _instructions(cls, value: Any) -> str | None:
        return parse_instructions(value)

    @model_validator(mode="after")
    def _normalise(self) -> AnalysisSettings:
        self.min_clip_seconds = max(3.0, float(self.min_clip_seconds))
        self.max_clip_seconds = max(self.min_clip_seconds + 2.0, float(self.max_clip_seconds))
        self.target_clip_seconds = min(
            self.max_clip_seconds, max(self.min_clip_seconds, float(self.target_clip_seconds))
        )
        self.sensitivity = int(min(100, max(0, self.sensitivity)))
        self.max_candidates = max(1, int(self.max_candidates))
        if self.auto_render_top is None:
            self.auto_render_top = self.max_candidates
        self.auto_render_top = max(0, int(self.auto_render_top))
        self.language = (self.language or "auto").strip().lower() or "auto"
        if self.title is not None:
            self.title = self.title.strip()[:120] or None
        return self

    @property
    def render_top(self) -> int:
        """``autoRenderTop`` as an int (``maxCandidates`` when the payload left it out)."""
        return self.max_candidates if self.auto_render_top is None else self.auto_render_top

    @property
    def whisper_language(self) -> str | None:
        """Language code for faster-whisper (``None`` = auto-detect)."""
        if self.language in ("", "auto"):
            return None
        return self.language.split("-")[0]


class JobLimits(CamelModel):
    max_render_height: int = 1080
    max_source_duration_seconds: float | None = None
    remaining_processing_seconds: float | None = None
    transcription_model: str | None = None
    allow_webcam_layout: bool = True


class JobSource(CamelModel):
    asset_id: str = ""
    download_url: str
    file_name: str = "source"
    content_type: str = "application/octet-stream"
    size_bytes: int | None = None
    duration_seconds: float | None = None


class ClipSpec(CamelModel):
    clip_id: str
    render_id: str
    version: int = 1
    title: str | None = None
    start_seconds: float
    end_seconds: float
    aspect_ratio: AspectRatio = "9:16"
    subtitles: SubtitleSettings = Field(default_factory=SubtitleSettings)
    framing: FramingSettings = Field(default_factory=FramingSettings)


class AnalysisRef(CamelModel):
    transcript_url: str | None = None
    webcam_region: WebcamRegion | None = None


# --------------------------------------------------------------------------------------
# live capture (``live_segment`` jobs, ``render_clip`` of live projects)
# --------------------------------------------------------------------------------------


def _positive_or_none(value: Any) -> float | None:
    """A duration that is a finite number above zero, else ``None`` (unknown)."""
    if value is None or isinstance(value, bool):
        return None
    try:
        number = float(value)
    except TypeError, ValueError:
        return None
    return number if math.isfinite(number) and number > 0 else None


class LiveSegmentInfo(CamelModel):
    """``live`` of a ``live_segment`` payload: where the segment sits in the session.
    ``sequence`` and ``startOffsetSeconds`` are required: the commit rule depends on them."""

    session_id: str = ""
    segment_id: str = ""
    sequence: int = Field(ge=0)
    start_offset_seconds: float = Field(ge=0)
    duration_seconds: float | None = None
    final: bool = False
    segment_seconds: float | None = None

    @field_validator("duration_seconds", "segment_seconds", mode="before")
    @classmethod
    def _duration(cls, value: Any) -> float | None:
        return _positive_or_none(value)

    @field_validator("final", mode="before")
    @classmethod
    def _final(cls, value: Any) -> bool:
        return bool(value) if value is not None else False


class LivePrevious(CamelModel):
    """``previous`` of a ``live_segment`` payload: the segment before this one (its last
    seconds are the context of this segment's analysis). ``null`` for sequence 0."""

    segment_id: str = ""
    sequence: int | None = None
    download_url: str
    start_offset_seconds: float | None = None
    duration_seconds: float | None = None
    # not in the contract (the worker takes the current segment's container otherwise)
    file_name: str | None = None
    content_type: str | None = None
    size_bytes: int | None = None

    @field_validator("duration_seconds", mode="before")
    @classmethod
    def _duration(cls, value: Any) -> float | None:
        return _positive_or_none(value)


def _finite_floats(value: Any) -> list[float]:
    if not isinstance(value, list | tuple):
        return []
    out: list[float] = []
    for item in value:
        if isinstance(item, bool) or not isinstance(item, int | float):
            continue
        number = float(item)
        if math.isfinite(number):
            out.append(number)
    return out


def _language_code(value: Any) -> str | None:
    """``"pt-BR"`` -> ``"pt"``; empty, ``"auto"`` or anything that is not a language code ->
    ``None``."""
    if not isinstance(value, str):
        return None
    code = value.strip().lower().split("-")[0].split("_")[0]
    if code in ("", "auto") or not code.isalpha() or len(code) > 8:
        return None
    return code


class LiveContext(CamelModel):
    """``context`` of a ``live_segment`` payload: the ``liveContext`` the previous segment's
    job reported in its ``/analysis`` summary (``null`` for the first segment).

    ``compositeSample`` (≤ 200 values of the session's composite, the population of the
    score percentile) and ``language`` (the session language, fixed after the first segment
    with speech) are the contract; ``sequence`` (the segment that wrote the context) and
    ``committedUntil`` (where that segment handed the peaks over) are extras the worker
    writes and reads when present."""

    composite_sample: list[float] = Field(default_factory=list)
    language: str | None = None
    sequence: int | None = None
    committed_until: float | None = None

    @field_validator("composite_sample", mode="before")
    @classmethod
    def _sample(cls, value: Any) -> list[float]:
        return _finite_floats(value)

    @field_validator("language", mode="before")
    @classmethod
    def _language(cls, value: Any) -> str | None:
        return _language_code(value)

    @field_validator("sequence", mode="before")
    @classmethod
    def _sequence(cls, value: Any) -> int | None:
        if isinstance(value, bool) or not isinstance(value, int | float):
            return None
        return int(value) if math.isfinite(float(value)) else None

    @field_validator("committed_until", mode="before")
    @classmethod
    def _committed(cls, value: Any) -> float | None:
        if isinstance(value, bool) or not isinstance(value, int | float):
            return None
        number = float(value)
        return number if math.isfinite(number) and number >= 0 else None


class SegmentSource(CamelModel):
    """One recorded segment of a live session (``render_clip`` of a live project carries the
    segments overlapping the clip range instead of a single ``source``)."""

    download_url: str
    start_offset_seconds: float = Field(ge=0)
    duration_seconds: float | None = None
    file_name: str = "segment.mkv"
    content_type: str | None = None
    size_bytes: int | None = None

    @field_validator("duration_seconds", mode="before")
    @classmethod
    def _duration(cls, value: Any) -> float | None:
        return _positive_or_none(value)


class JobPayload(CamelModel):
    job_id: str
    kind: JobKind
    attempt: int = 1
    user_id: str = ""
    project_id: str = ""
    lease_seconds: int = 180
    # required for analyze_source and live_segment; render_clip carries it or ``segments``
    source: JobSource | None = None
    settings: AnalysisSettings = Field(default_factory=AnalysisSettings)
    limits: JobLimits = Field(default_factory=JobLimits)
    clip: ClipSpec | None = None
    analysis: AnalysisRef | None = None
    # live_segment
    live: LiveSegmentInfo | None = None
    previous: LivePrevious | None = None
    context: LiveContext | None = None
    # render_clip of a live project: the segments overlapping the clip range
    segments: list[SegmentSource] = Field(default_factory=list)

    @field_validator("segments", mode="before")
    @classmethod
    def _segments(cls, value: Any) -> Any:
        return [] if value is None else value

    @model_validator(mode="after")
    def _fields_of_the_kind(self) -> JobPayload:
        if self.kind in ("analyze_source", "live_segment") and self.source is None:
            raise ValueError(f"'source' is required for {self.kind} jobs")
        if self.kind == "live_segment" and self.live is None:
            raise ValueError("'live' is required for live_segment jobs")
        if self.kind == "render_clip" and self.source is None and not self.segments:
            raise ValueError("render_clip jobs need 'source' or 'segments'")
        return self

    @property
    def media(self) -> JobSource:
        """The single source file (``analyze_source``, ``live_segment``, ``render_clip`` of an
        uploaded video); the validator guarantees it for those kinds."""
        if self.source is None:
            raise ValueError("payload without 'source'")
        return self.source


# --------------------------------------------------------------------------------------
# reports sent to the API
# --------------------------------------------------------------------------------------


class ProbeReport(CamelModel):
    duration_seconds: float
    width: int | None = None
    height: int | None = None
    fps: float | None = None
    has_audio: bool
    has_video: bool
    container: str | None = None
    video_codec: str | None = None
    audio_codec: str | None = None
    size_bytes: int | None = None


class ProbeResponse(CamelModel):
    ok: bool = True
    remaining_processing_seconds: float | None = None


class HeartbeatResponse(CamelModel):
    cancel_requested: bool = False
    lease_expires_at: str | None = None


class CandidateInput(CamelModel):
    rank: int
    start_seconds: float
    end_seconds: float
    score: int = Field(ge=0, le=100)
    category: Category
    reasoning: str
    transcript: str = ""
    signals: dict[str, Any] = Field(default_factory=dict)


class UploadFileSpec(CamelModel):
    kind: Literal["clip_render", "thumbnail", "analysis", "preview"]
    file_name: str
    content_type: str
    size_bytes: int | None = None


class UploadTarget(CamelModel):
    file_name: str
    key: str
    url: str
    method: str = "PUT"
    headers: dict[str, str] = Field(default_factory=dict)


class RenderInfo(CamelModel):
    storage_key: str
    width: int
    height: int
    duration_seconds: float
    size_bytes: int
    has_subtitles: bool = False


class ClipReport(CamelModel):
    candidate_id: str
    title: str | None = None
    aspect_ratio: AspectRatio
    start_seconds: float
    end_seconds: float
    subtitles: SubtitleSettings
    framing: FramingSettings
    render: RenderInfo
    thumbnail_key: str | None = None


class RenderResultReport(CamelModel):
    render_id: str
    status: Literal["ready", "failed"]
    storage_key: str | None = None
    width: int | None = None
    height: int | None = None
    duration_seconds: float | None = None
    size_bytes: int | None = None
    has_subtitles: bool | None = None
    thumbnail_key: str | None = None
    error: str | None = None


class AnalysisSummary(CamelModel):
    """``summary`` of ``/analysis`` and ``/complete`` (analyze_source). ``clipsRendered`` is
    omitted (``None``) when the worker could not confirm the ``/clips`` registration: the
    API then counts the renders it stored."""

    signals: dict[str, Any] = Field(default_factory=dict)
    webcam_region: WebcamRegion | None = None
    language: str | None = None
    processed_seconds: float = 0.0
    candidates_found: int | None = None
    clips_rendered: int | None = None
    # False when the voice activity detector found no speech (no language, no subtitles);
    # the same flag is in ``signals.transcript.speechDetected``. Omitted when unknown.
    speech_detected: bool | None = None
    # live_segment only: the context the next segment of the session receives
    # (``compositeSample``, ``language``, ``sequence``, ``committedUntil``)
    live_context: dict[str, Any] | None = None

    def to_api(self) -> dict[str, Any]:
        data = super().to_api()
        for key in ("clipsRendered", "speechDetected", "liveContext"):
            if data.get(key) is None:
                data.pop(key, None)
        return data


class AnalysisReport(CamelModel):
    transcript_key: str | None = None
    signals_key: str | None = None
    summary: AnalysisSummary


# --------------------------------------------------------------------------------------
# development endpoint
# --------------------------------------------------------------------------------------


class AnalyzeRequest(CamelModel):
    path: str
    settings: AnalysisSettings = Field(default_factory=AnalysisSettings)
    limits: JobLimits = Field(default_factory=JobLimits)
    transcription: bool = True
    webcam: bool = True
    llm: bool = False


class AnalyzeResponse(CamelModel):
    probe: ProbeReport
    duration_seconds: float
    candidates: list[CandidateInput]
    signals: dict[str, Any]
    webcam_region: WebcamRegion | None = None
    language: str | None = None
    threshold: float
    weights: dict[str, float]
    timings: dict[str, float]
    # job events the worker would post for this analysis (short source, LLM problems...)
    notices: list[dict[str, Any]] = Field(default_factory=list)
    # titles suggested by the LLM provider, by candidate rank ("1", "2", ...)
    suggested_titles: dict[str, str] = Field(default_factory=dict)
