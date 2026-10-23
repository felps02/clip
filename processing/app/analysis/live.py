"""Live capture: the pure logic of ``live_segment`` jobs (``docs/PROCESSING_PIPELINE.md``,
"live_segment").

A live session arrives as consecutive segments (``startOffsetSeconds``, ``durationSeconds``
in session time). Segment N is analysed in a **window** made of the last
``min(45 s, previous duration)`` of segment N-1 (its *tail*, the context) followed by
segment N itself. Every time the worker reports is an absolute session time.

* **Grid.** The window's analysis starts on a whole second of the session
  (:func:`plan_window`), so every window computes its per-second series on the same
  session-wide grid: the same moment lands on the same second in the two windows that see
  it, and the commit rule below decides it the same way in both.
* **Commit rule.** ``postRoll = clamp(maxClipSeconds / 2, 8, 30)``; segment N reports the
  candidates whose peak lies between the previous segment's hand-over point and its own
  (segment 0 from the start of the session, the final segment up to its end). The
  hand-over point (:func:`handover_point`) is ``end_N - postRoll`` (``start_N`` for a
  segment shorter than that): the moments after it are left to segment N+1, which detects
  them again with segment N's tail as context. It never lies before the next window's
  first whole second plus ``HANDOVER_CONTEXT`` (8 s: that window's first second can never
  be a peak, and a moment rising there needs some context in the only window allowed to
  report it), nor after ``end_N - 1`` (the last second of segment N's window cannot be a
  peak either). The previous segment's hand-over point travels in the context
  (``committedUntil``) when the API passes it back, so the two sides of a boundary always
  use the very same number.
* **Score.** The percentile of a peak is taken among the session's composite sample
  (``context.compositeSample``) plus the window's own composite; the sample is a reservoir
  (Algorithm R, at most 200 values, seeded by the session and the sequence so a retried job
  computes the same sample) of one value per second of the session, each second counted
  once (by the segment that holds its midpoint).
* **Language.** The session language is fixed by the first segment with speech
  (``liveContext.language``); the next segments transcribe with it and skip the detection.
"""

from __future__ import annotations

import hashlib
import math
import random
from dataclasses import dataclass
from typing import Any

import numpy as np

from app.analysis.fusion import Candidate, PeakScope
from app.analysis.transcription import Transcript
from app.models import AnalysisSettings, LiveContext

TAIL_SECONDS = 45.0
POST_ROLL_MIN = 8.0
POST_ROLL_MAX = 30.0
SAMPLE_SIZE = 200
SAMPLE_DIGITS = 3
# the previous segment must end where the current one starts (session times) to be used as
# its context; more than this apart (a gap or an overlap in the recording) it is not
CONTIGUITY_TOLERANCE = 2.0
# seconds of a segment the next window analyses before the hand-over point, at least (when
# the segment is long enough): the next window's first second cannot be a peak, and a
# moment that starts rising right after the window's origin would otherwise be decided
# without its beginning (rolling baselines, smoothing, the clip's start)
HANDOVER_CONTEXT = 8.0


def post_roll_seconds(max_clip_seconds: float) -> float:
    """``clamp(maxClipSeconds / 2, 8, 30)``: the media a segment wants after a peak before
    it reports it (the clip around the peak continues after it)."""
    return min(POST_ROLL_MAX, max(POST_ROLL_MIN, float(max_clip_seconds) / 2.0))


def handover_point(start: float, duration: float, post_roll: float) -> float:
    """Session time from which a segment leaves the peaks to the next one.

    ``end - min(postRoll, duration)``: the moments after ``end - postRoll`` need media the
    segment does not have yet. But the next window starts ``min(45 s, duration)`` before
    the end, on a whole second (:func:`plan_window`), and its first second can never be a
    peak: handing over at (or right after) that origin, as a segment shorter than about
    ``postRoll`` + 2 s would, loses the moments of that second, and those rising there have
    no context in the only window allowed to report them. So the hand-over point is never
    before that origin + :data:`HANDOVER_CONTEXT`, nor, to keep it inside this segment's
    window, after ``end - 1`` (the window's last second cannot be a peak either): a long
    segment hands over at ``end - postRoll``, a 20 s one with ``postRoll`` 30 s at its
    first whole second + 8 s, a 6 s one 1 s before its end."""
    duration = max(0.0, float(duration))
    end = float(start) + duration
    rule = end - min(float(post_roll), duration)
    next_origin = plan_window(end, duration).origin
    return round(max(rule, min(end - 1.0, next_origin + HANDOVER_CONTEXT)), 3)


@dataclass(slots=True)
class CommitRange:
    """Peaks a segment reports, in session seconds: ``[start, end)``."""

    start: float
    end: float

    def contains(self, peak: float) -> bool:
        return self.start <= peak < self.end

    def to_json(self) -> list[float | None]:
        return [
            None if math.isinf(self.start) else round(self.start, 3),
            None if math.isinf(self.end) else round(self.end, 3),
        ]


def commit_range(
    *,
    start: float,
    duration: float,
    sequence: int,
    final: bool,
    post_roll: float,
    previous_duration: float | None = None,
    committed_until: float | None = None,
) -> CommitRange:
    """The commit rule: from the previous segment's hand-over point to this one's
    (:func:`handover_point`), from the start of the session for segment 0 and up to the end
    for the final segment.

    The lower bound is ``committed_until`` when the previous segment's context carries it,
    else the previous segment's hand-over point recomputed from its duration
    (``start - postRoll`` when unknown). A hand-over point always lies within ``postRoll``
    (≤ 30 s) before the end of its segment; a ``committed_until`` elsewhere does not belong
    to this segment's predecessor and is ignored."""
    if sequence <= 0:
        low = -math.inf
    elif committed_until is not None and (
        float(start) - POST_ROLL_MAX - CONTIGUITY_TOLERANCE
        <= float(committed_until)
        <= float(start) + CONTIGUITY_TOLERANCE
    ):
        low = float(committed_until)
    elif previous_duration is not None and previous_duration > 0:
        previous = float(previous_duration)
        low = handover_point(float(start) - previous, previous, post_roll)
    else:
        low = round(float(start) - float(post_roll), 3)
    high = math.inf if final else handover_point(start, duration, post_roll)
    return CommitRange(low, high)


@dataclass(slots=True)
class WindowPlan:
    """Where a live window sits in the session.

    The window file holds the previous segment (whole) and the current one, stream-copied
    one after the other; the analysis skips ``media_offset`` seconds of it so that its
    second 0 is the whole session second ``origin``."""

    origin: float  # session time of the analysis' second 0 (a whole second)
    file_origin: float  # session time of the window file's first sample
    segment_start: float  # session time of the current segment
    tail_seconds: float  # previous media analysed before the current segment

    @property
    def media_offset(self) -> float:
        return max(0.0, self.origin - self.file_origin)

    def to_session(self, analysis_time: float) -> float:
        return self.origin + analysis_time

    def to_file(self, session_time: float) -> float:
        return session_time - self.file_origin

    def scope(self, commit: CommitRange, *, starts_session: bool, ends_session: bool) -> PeakScope:
        """The commit range in the window's own seconds; an edge of the window is a real
        edge only where the session itself starts (segment 0, or a window whose tail reaches
        back to the session's first second) or ends (final segment)."""
        return PeakScope(
            start=commit.start - self.origin,
            end=commit.end - self.origin,
            left_edge=starts_session or self.origin <= 0.0,
            right_edge=ends_session,
        )


def plan_window(segment_start: float, previous_file_duration: float | None) -> WindowPlan:
    """The analysis window of a segment: the tail of the previous segment
    (``min(45 s, its duration)``, rounded down to start on a whole session second) and the
    segment itself; without a previous segment the analysis starts on the first whole second
    of the segment."""
    segment_start = max(0.0, float(segment_start))
    if previous_file_duration is not None and previous_file_duration > 0:
        file_origin = segment_start - float(previous_file_duration)
        tail = min(TAIL_SECONDS, float(previous_file_duration))
    else:
        file_origin = segment_start
        tail = 0.0
    # round() first: 51.666667 - 26.666667 must give 25, not 25.0000001 -> 26. The origin
    # is never before the file's start (tail <= previous duration), nor before the session.
    origin = max(0.0, float(math.ceil(round(segment_start - tail, 6))))
    return WindowPlan(
        origin=origin,
        file_origin=file_origin,
        segment_start=segment_start,
        tail_seconds=max(0.0, segment_start - origin),
    )


def contiguous(
    previous_start: float | None, previous_duration: float | None, segment_start: float
) -> bool:
    """Whether the previous segment ends where the current one starts (unknown -> yes)."""
    if previous_start is None or previous_duration is None:
        return True
    return abs(previous_start + previous_duration - segment_start) <= CONTIGUITY_TOLERANCE


# --------------------------------------------------------------------------------------
# session score sample
# --------------------------------------------------------------------------------------


def seconds_before(segment_start: float) -> int:
    """Seconds of the session before the segment (each second belongs to the segment that
    holds its midpoint)."""
    return max(0, math.ceil(float(segment_start) - 0.5))


def own_seconds(
    neutral: np.ndarray, origin: float, segment_start: float, segment_end: float = math.inf
) -> np.ndarray:
    """The window's composite values of the seconds that belong to the segment itself
    (midpoint in ``[segment_start, segment_end)``): the tail's seconds were counted by the
    previous segment, and the second the segment ends in by the next one when its midpoint
    lies after the end."""
    values = np.asarray(neutral, dtype=np.float64)
    midpoints = origin + np.arange(values.size) + 0.5
    return values[(midpoints >= segment_start) & (midpoints < segment_end)]


def _rng(seed: str) -> random.Random:
    digest = hashlib.sha256(seed.encode("utf-8")).digest()
    return random.Random(int.from_bytes(digest[:8], "big"))


def update_sample(
    sample: list[float] | np.ndarray,
    values: np.ndarray | list[float],
    *,
    seen_before: int,
    seed: str,
    size: int = SAMPLE_SIZE,
) -> list[float]:
    """Reservoir sampling (Algorithm R) of the session's per-second composite: ``sample``
    represents the ``seen_before`` seconds before ``values``; the result represents them all
    uniformly with at most ``size`` values. Deterministic for a given ``seed``."""
    rng = _rng(seed)
    out = [float(v) for v in sample if math.isfinite(float(v))]
    if len(out) > size:
        out = rng.sample(out, size)
    seen = max(int(seen_before), len(out))
    for raw in values:
        value = float(raw)
        if not math.isfinite(value):
            continue
        if len(out) < size:
            out.append(value)
        else:
            j = rng.randrange(seen + 1)
            if j < size:
                out[j] = value
        seen += 1
    return [round(v, SAMPLE_DIGITS) for v in out]


def sample_seed(session_id: str, sequence: int) -> str:
    return f"{session_id or 'session'}:{int(sequence)}"


# --------------------------------------------------------------------------------------
# session language
# --------------------------------------------------------------------------------------


def fixed_language(
    settings: AnalysisSettings, context: LiveContext | None
) -> tuple[str | None, str | None]:
    """``(language, source)`` the segment transcribes with, without detection: the
    language of the settings (``"settings"``), else the session's (``"session"``), else
    ``(None, None)`` (detected in this segment)."""
    if settings.whisper_language:
        return settings.whisper_language, "settings"
    if context is not None and context.language:
        return context.language, "session"
    return None, None


def next_language(fixed: str | None, transcript: Transcript) -> str | None:
    """The session language after this segment: the fixed one, else the language detected
    in this segment when it had speech, else still unknown (``None``)."""
    if fixed:
        return fixed
    if transcript.available and transcript.speech_detected and transcript.language:
        return transcript.language
    return None


# --------------------------------------------------------------------------------------
# session times
# --------------------------------------------------------------------------------------


def to_session_times(candidates: list[Candidate], origin: float, info: dict[str, Any]) -> None:
    """Shift the window's candidates to session times (in place) and tag their signals."""
    for cand in candidates:
        cand.start = round(cand.start + origin, 2)
        cand.end = round(cand.end + origin, 2)
        cand.peak = cand.peak + origin
        cand.signals["peakSeconds"] = round(cand.peak, 1)
        cand.signals["live"] = dict(info)


def _shift_pairs(pairs: Any, offset: float) -> Any:
    if not isinstance(pairs, list):
        return pairs
    out = []
    for pair in pairs:
        if isinstance(pair, list | tuple) and len(pair) == 2:
            out.append([round(float(pair[0]) + offset, 3), round(float(pair[1]) + offset, 3)])
        else:
            out.append(pair)
    return out


def session_transcript_json(data: dict[str, Any], origin: float) -> dict[str, Any]:
    """``transcript.json`` of a window with every time in session seconds (``originSeconds``
    is the session time of the window's start): a later render of a clip of the session
    takes the words from here."""
    out = dict(data)
    segments = []
    for seg in data.get("segments") or []:
        seg = dict(seg)
        seg["start"] = round(float(seg.get("start", 0.0)) + origin, 3)
        seg["end"] = round(float(seg.get("end", 0.0)) + origin, 3)
        words = []
        for word in seg.get("words") or []:
            word = dict(word)
            word["start"] = round(float(word.get("start", 0.0)) + origin, 3)
            word["end"] = round(float(word.get("end", 0.0)) + origin, 3)
            words.append(word)
        seg["words"] = words
        segments.append(seg)
    out["segments"] = segments
    out["gaps"] = _shift_pairs(data.get("gaps") or [], origin)
    detection = data.get("languageDetection")
    if isinstance(detection, dict):
        detection = dict(detection)
        detection["spans"] = _shift_pairs(detection.get("spans") or [], origin)
        out["languageDetection"] = detection
    out["originSeconds"] = round(origin, 3)
    out["timeBase"] = "session"
    return out
