"""Signal fusion and candidate detection (``docs/PROCESSING_PIPELINE.md`` step 7).

composite(t) = Σ wᵢ · clamp(zᵢ(t), −1, 4) over the available modalities with weights
audio 0.45 · transcript 0.30 · visual 0.25 (renormalised when a modality is missing);
``llm_score`` adds 0.2 when present. ``threshold = 2.4 − 1.8·sensitivity/100``.

A modality that covers only part of the recording (``gaps``: untranscribed chunks, frames
missing after a decoder failure) is left out of the seconds it did not analyse and the
weights are renormalised per second over the modalities that did, so a gap neither
counts as a calm moment nor drags the composite down there. A transcript of a recording
without speech is left out the same way (it is not a calm moment either).

The user's focus (``settings.focusCategories``) multiplies some signal terms and lexicon
categories before peak picking and gives the candidates of those categories +10 rank
priority; the stored score stays the unmodified internal estimate (:class:`Focus`).

A live window (``live_segment``) reports only the peaks of its :class:`PeakScope` (the
commit rule) and scores them against the session's composite sample as well as its own
(``score_reference``); its edges are not the recording's, so a second on an edge is never
a peak there (the true maximum may lie outside the window), and such an edge does not bound
the clip windows while the merge and the suppression decide (pushed inside the window, the
clip of a moment next to the edge could swallow its neighbour, which the whole recording
keeps apart); the reported windows are then brought back inside the window's media.

Pure numpy functions, so the behaviour can be unit-tested on synthetic series.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any

import numpy as np

from app.analysis.audio_features import AudioFeatures
from app.analysis.common import coverage_fraction, covered_mask, sigmoid
from app.analysis.transcription import TimedKeyword, TranscriptSeries, keyword_score_series
from app.analysis.visual_features import VisualFeatures
from app.models import CandidateInput

BASE_WEIGHTS: dict[str, float] = {"audio": 0.45, "transcript": 0.30, "visual": 0.25}
LLM_WEIGHT = 0.2
CLAMP_LOW, CLAMP_HIGH = -1.0, 4.0
# Reasoning: the level of the window's loudest second is stated in dB above the recording's
# typical level when the difference is at least this large (a clearly audible change).
LEVEL_STATEMENT_MIN_DB = 3.0
PRE_ROLL = 3.0
POST_ROLL = 4.0
SNAP_TOLERANCE = 2.0
MERGE_GAP = 2.0
NMS_IOU = 0.5
# a visual stream "has something" when consecutive frames differ by more than this (mean
# absolute difference, 0-255) somewhere, or a scene cut was seen
VISUAL_ACTIVITY_MIN = 1.0

SCORE_NOTE = "internal relevance estimate (0-100), not a probability of going viral"
WHOLE_VIDEO_REASON = "Vídeo inteiro usado como clipe por ser mais curto que a duração mínima"

# --------------------------------------------------------------------------------------
# focus (settings.focusCategories)
# --------------------------------------------------------------------------------------

# multipliers per focused category; keys: "audio.<term>", "transcript.<term>",
# "visual.<term>", "lexicon.<category>" (keyword weights) and "weight.transcript" (the
# modality's base weight, renormalised afterwards)
FOCUS_ADJUSTMENTS: dict[str, dict[str, float]] = {
    "funny": {"audio.laughter": 1.8, "lexicon.funny": 1.5},
    "hype": {
        "audio.scream": 1.5,
        "audio.burst": 1.5,
        "lexicon.hype": 1.5,
        "lexicon.reaction": 1.5,
    },
    "reaction": {
        "audio.scream": 1.5,
        "audio.burst": 1.5,
        "lexicon.hype": 1.5,
        "lexicon.reaction": 1.5,
    },
    "quote": {"weight.transcript": 1.5, "lexicon.quote": 1.5},
    "gameplay": {"visual.motion": 1.5, "lexicon.gameplay": 1.5},
    "tension": {"lexicon.tension": 1.5, "transcript.pause": 1.5},
    "highlight": {},
}
FOCUS_RANK_BONUS = 10


@dataclass(slots=True)
class Focus:
    """Adjustments derived from the focused categories. Several categories never stack on
    one term: the largest multiplier applies."""

    categories: tuple[str, ...] = ()
    adjustments: dict[str, float] = field(default_factory=dict)

    @classmethod
    def from_categories(cls, categories: list[str] | tuple[str, ...] | None) -> Focus:
        cats = tuple(c for c in dict.fromkeys(categories or ()) if c in FOCUS_ADJUSTMENTS)
        adjustments: dict[str, float] = {}
        for category in cats:
            for key, value in FOCUS_ADJUSTMENTS[category].items():
                adjustments[key] = max(adjustments.get(key, 1.0), value)
        return cls(cats, dict(sorted(adjustments.items())))

    def factor(self, key: str) -> float:
        return self.adjustments.get(key, 1.0)

    @property
    def lexicon_multipliers(self) -> dict[str, float]:
        return {
            key.split(".", 1)[1]: value
            for key, value in self.adjustments.items()
            if key.startswith("lexicon.")
        }

    def rank_bonus(self, category: str) -> int:
        return FOCUS_RANK_BONUS if category in self.categories else 0

    def to_json(self) -> dict[str, Any]:
        return {
            "categories": list(self.categories),
            "adjustments": dict(self.adjustments),
            "rankBonus": FOCUS_RANK_BONUS if self.categories else 0,
        }


NO_FOCUS = Focus()


@dataclass(slots=True)
class FusionSettings:
    min_clip: float = 15.0
    max_clip: float = 60.0
    target_clip: float = 30.0
    sensitivity: int = 50
    max_candidates: int = 20
    focus: Focus = field(default_factory=Focus)
    # source shorter than the requested minimum: min/target/max were clamped to its
    # duration and the whole video may become the candidate (round-3 P2)
    short_source: bool = False


@dataclass(slots=True)
class Candidate:
    rank: int
    start: float
    end: float
    peak: float
    composite: float
    score: int
    category: str
    reasoning: str
    transcript: str
    signals: dict[str, Any]
    contributors: list[str] = field(default_factory=list)
    # ordering key: score + focus bonus (+ LLM bonus); never stored as the score
    priority: float = 0.0
    title: str | None = None  # suggested by the LLM provider (validated), if any

    @property
    def duration(self) -> float:
        return self.end - self.start

    def to_input(self) -> CandidateInput:
        return CandidateInput(
            rank=self.rank,
            start_seconds=round(self.start, 2),
            end_seconds=round(self.end, 2),
            score=int(self.score),
            category=self.category,  # type: ignore[arg-type]
            reasoning=self.reasoning,
            transcript=self.transcript,
            signals=self.signals,
        )


@dataclass(slots=True)
class FusionResult:
    candidates: list[Candidate]
    composite: np.ndarray
    threshold: float
    weights: dict[str, float]
    components: dict[str, np.ndarray]
    peaks: list[int]
    # effective weight of each modality per second (equal to ``weights`` wherever every
    # available modality covers the second; see the module docstring)
    weight_series: dict[str, np.ndarray] = field(default_factory=dict)
    whole_video: bool = False  # the short-source fallback produced the candidate
    # composite without the focus: the one the scores come from (and the values a live
    # session keeps in its composite sample)
    neutral: np.ndarray | None = None


@dataclass(slots=True)
class PeakScope:
    """The peaks a window may report: peak time (second + 0.5) in ``[start, end)`` — the
    commit rule of a live segment, in the window's own seconds — and whether a second on an
    edge of the window may be a peak at all: only where the window starts or ends with the
    recording itself (a live window cut out of a longer session does not)."""

    start: float = -math.inf
    end: float = math.inf
    left_edge: bool = True
    right_edge: bool = True

    def contains(self, peak_time: float) -> bool:
        return self.start <= peak_time < self.end


@dataclass(slots=True)
class Window:
    start: float
    end: float
    peak: int
    value: float


# --------------------------------------------------------------------------------------
# modality z-series
# --------------------------------------------------------------------------------------


def _fit(values: np.ndarray | None, n: int) -> np.ndarray:
    if values is None:
        return np.zeros(n)
    arr = np.asarray(values, dtype=np.float64)
    if arr.size >= n:
        return arr[:n]
    return np.concatenate([arr, np.zeros(n - arr.size)])


def audio_z(audio: AudioFeatures, n: int, focus: Focus = NO_FOCUS) -> np.ndarray:
    return (
        _fit(audio.energy_z, n)
        + 0.5 * focus.factor("audio.burst") * _fit(audio.burst, n)
        + 0.75 * focus.factor("audio.scream") * _fit(audio.scream_like, n)
        + 0.75 * focus.factor("audio.laughter") * _fit(audio.laughter_like, n)
        + 0.25 * _fit(audio.sustained, n)
    )


def focused_keyword_score(series: TranscriptSeries, n: int, focus: Focus = NO_FOCUS) -> np.ndarray:
    """The keyword score with the focused lexicon categories weighted up: the series' own
    score plus ``(multiplier − 1)`` times the weight of each hit of those categories."""
    base = _fit(series.keyword_score, n)
    extra = {cat: m - 1.0 for cat, m in focus.lexicon_multipliers.items() if m != 1.0}
    if not extra:
        return base
    focused = [k for k in series.keywords if k.category in extra]
    return base + keyword_score_series(focused, n, extra)


def transcript_z(series: TranscriptSeries, n: int, focus: Focus = NO_FOCUS) -> np.ndarray:
    return (
        0.5 * _fit(series.speech_rate_z, n)
        + 0.6 * focused_keyword_score(series, n, focus)
        + 0.4 * np.minimum(_fit(series.exclamation, n), 3.0)
        + 0.5 * focus.factor("transcript.pause") * _fit(series.pause_before, n)
    )


def visual_z(visual: VisualFeatures, n: int, focus: Focus = NO_FOCUS) -> np.ndarray:
    return (
        0.6 * focus.factor("visual.motion") * _fit(visual.motion_z, n)
        + 1.0 * np.minimum(_fit(visual.scene_cut, n), 2.0)
        + 0.5 * _fit(visual.flash, n)
    )


def llm_z(scores: np.ndarray, n: int) -> np.ndarray:
    return (_fit(scores, n) - 0.5) * 6.0


def threshold_for(sensitivity: int | float) -> float:
    s = min(100.0, max(0.0, float(sensitivity)))
    return 2.4 - 1.8 * s / 100.0


def _transcript_counts(transcript: TranscriptSeries | None) -> bool:
    """A transcript takes part in the composite when it is available and not known to be
    speechless (no speech is not a calm moment: the other modalities carry it)."""
    return transcript is not None and transcript.available and transcript.has_speech


def composite_parts(
    n: int,
    *,
    audio: AudioFeatures | None,
    transcript: TranscriptSeries | None,
    visual: VisualFeatures | None,
    llm: np.ndarray | None = None,
    focus: Focus = NO_FOCUS,
) -> tuple[np.ndarray, dict[str, float], dict[str, np.ndarray], dict[str, np.ndarray]]:
    """``(composite, nominal weights, components, per-second weights)``.

    The nominal weights renormalise the base weights (the transcript's times the focus
    multiplier) over the available modalities; in a second that a modality did not analyse
    (one of its ``gaps``) the weights are renormalised again over the modalities that did.
    Without gaps the per-second weights equal the nominal ones everywhere.
    """
    base = dict(BASE_WEIGHTS)
    base["transcript"] *= focus.factor("weight.transcript")
    components: dict[str, np.ndarray] = {}
    covered: dict[str, np.ndarray] = {}
    if audio is not None and audio.available:
        components["audio"] = audio_z(audio, n, focus)
        covered["audio"] = np.ones(n, dtype=bool)
    if transcript is not None and _transcript_counts(transcript):
        components["transcript"] = transcript_z(transcript, n, focus)
        covered["transcript"] = covered_mask(getattr(transcript, "gaps", []), n)
    if visual is not None and visual.available:
        components["visual"] = visual_z(visual, n, focus)
        covered["visual"] = covered_mask(getattr(visual, "gaps", []), n)
    total = sum(base[k] for k in components)
    weights = {k: base[k] / total for k in components} if total > 0 else {}
    covered_total = np.zeros(n)
    for key in components:
        covered_total += base[key] * covered[key]
    weight_series: dict[str, np.ndarray] = {}
    composite = np.zeros(n)
    for key, series in components.items():
        w = np.divide(
            base[key] * covered[key],
            covered_total,
            out=np.zeros(n),
            where=covered_total > 0,
        )
        weight_series[key] = w
        composite += w * np.clip(series, CLAMP_LOW, CLAMP_HIGH)
    if llm is not None:
        components["llm"] = llm_z(llm, n)
        weights["llm"] = LLM_WEIGHT
        weight_series["llm"] = np.full(n, LLM_WEIGHT)
        composite += LLM_WEIGHT * np.clip(components["llm"], CLAMP_LOW, CLAMP_HIGH)
    return composite, weights, components, weight_series


def compute_composite(
    n: int,
    *,
    audio: AudioFeatures | None,
    transcript: TranscriptSeries | None,
    visual: VisualFeatures | None,
    llm: np.ndarray | None = None,
    focus: Focus = NO_FOCUS,
) -> tuple[np.ndarray, dict[str, float], dict[str, np.ndarray]]:
    composite, weights, components, _ = composite_parts(
        n, audio=audio, transcript=transcript, visual=visual, llm=llm, focus=focus
    )
    return composite, weights, components


def has_any_signal(
    audio: AudioFeatures | None,
    transcript: TranscriptSeries | None,
    visual: VisualFeatures | None,
) -> bool:
    """Something happens in the recording: non-silent audio, a transcribed word, or frames
    that change (motion or a scene cut)."""
    if audio is not None and audio.available and audio.typical_level() is not None:
        return True
    if transcript is not None and transcript.available and transcript.words:
        return True
    if visual is not None and visual.available:
        motion = np.asarray(visual.motion, dtype=np.float64)
        cuts = np.asarray(visual.scene_cut, dtype=np.float64)
        if (motion.size and float(np.max(motion)) > VISUAL_ACTIVITY_MIN) or (
            cuts.size and float(np.sum(cuts)) > 0
        ):
            return True
    return False


# --------------------------------------------------------------------------------------
# peaks and windows
# --------------------------------------------------------------------------------------


def pick_peaks(
    composite: np.ndarray,
    threshold: float,
    min_distance: float,
    *,
    left_edge: bool = True,
    right_edge: bool = True,
) -> list[int]:
    """Local maxima above ``threshold`` with greedy minimum-distance suppression.

    ``left_edge``/``right_edge``: whether the first/last second may be a peak. A series cut
    out of a longer recording (a live window) passes ``False`` for an edge that is not the
    recording's: the composite may still rise beyond it, so a maximum on the edge is an
    artefact of the cut (and must not suppress the real peaks next to it)."""
    n = len(composite)
    if n == 0:
        return []
    left = np.concatenate([[-np.inf if left_edge else np.inf], composite[:-1]])
    right = np.concatenate([composite[1:], [-np.inf if right_edge else np.inf]])
    is_peak = (composite > threshold) & (composite >= left) & (composite >= right)
    # a flat plateau yields several equal maxima; keep the first of each plateau
    candidates = [int(i) for i in np.flatnonzero(is_peak)]
    candidates.sort(key=lambda i: (-composite[i], i))
    kept: list[int] = []
    min_distance = max(1.0, float(min_distance))
    for i in candidates:
        if all(abs(i - k) >= min_distance for k in kept):
            kept.append(i)
    return sorted(kept)


def _bound_window(
    start: float, end: float, peak: int, max_clip: float, low: float, high: float
) -> tuple[float, float]:
    """Keep ``[start, end]`` inside ``[low, high]`` — moved rather than shortened while it
    fits — and no longer than ``max_clip`` (cut around the peak)."""
    if start < low:
        end += low - start
        start = low
    if end > high:
        start -= end - high
        end = high
        start = max(low, start)
    if end - start > max_clip:
        start = max(start, peak + 0.5 - max_clip * 0.4)
        end = start + max_clip
        if end > high:
            end = high
            start = max(low, end - max_clip)
    return float(start), float(end)


def expand_window(
    composite: np.ndarray,
    peak: int,
    threshold: float,
    settings: FusionSettings,
    duration: float,
    *,
    pre_roll: float = PRE_ROLL,
    post_roll: float = POST_ROLL,
    bounds: tuple[float, float] | None = None,
) -> tuple[float, float]:
    """Grow from the peak while composite > threshold/2, apply pre/post roll and bounds.

    ``bounds`` (default ``(0, duration)``) is where the window must fit; a live window
    passes an infinite bound for an edge that is not the recording's (see
    :func:`detect_candidates`)."""
    n = len(composite)
    half = threshold / 2.0
    min_clip, max_clip = settings.min_clip, settings.max_clip
    s = peak
    while s - 1 >= 0 and composite[s - 1] > half and (peak - (s - 1)) <= max_clip * 0.6:
        s -= 1
    e = peak + 1
    while e < n and composite[e] > half and ((e + 1) - peak) <= max_clip * 0.7:
        e += 1
    start = min(float(s), peak + 0.5 - pre_roll)
    end = max(float(e), peak + 0.5 + post_roll)

    if end - start < min_clip:
        deficit = min_clip - (end - start)
        start -= deficit * 0.4
        end += deficit * 0.6
    low, high = bounds if bounds is not None else (0.0, float(duration))
    return _bound_window(start, end, peak, max_clip, low, high)


def snap_window(
    start: float,
    end: float,
    peak: int,
    *,
    start_anchors: np.ndarray,
    end_anchors: np.ndarray,
    settings: FusionSettings,
    duration: float,
    tolerance: float = SNAP_TOLERANCE,
    bounds: tuple[float, float] | None = None,
) -> tuple[float, float]:
    """Move start/end to the nearest anchor (silence / transcript boundary) within ±tolerance;
    the result is clipped to ``bounds`` (default ``(0, duration)``)."""
    peak_time = peak + 0.5

    def nearest(anchors: np.ndarray, value: float) -> float | None:
        if anchors.size == 0:
            return None
        idx = int(np.argmin(np.abs(anchors - value)))
        candidate = float(anchors[idx])
        if abs(candidate - value) <= tolerance:
            return candidate
        return None

    new_start = nearest(start_anchors, start)
    if (
        new_start is not None
        and new_start <= peak_time - 1.0
        and settings.min_clip <= end - new_start <= settings.max_clip
    ):
        start = new_start
    new_end = nearest(end_anchors, end)
    if (
        new_end is not None
        and new_end >= peak_time + 1.0
        and settings.min_clip <= new_end - start <= settings.max_clip
    ):
        end = new_end
    low, high = bounds if bounds is not None else (0.0, float(duration))
    start = max(low, start)
    end = min(high, end)
    return start, end


def _within_media(
    window: Window, computed: tuple[Window, Window], settings: FusionSettings, duration: float
) -> Window:
    """A window the merge and the suppression kept, inside the media of the analysis:
    ``computed`` is its peak's window as they compared it and as computed inside the media;
    the latter when the window is that very one (not merged), else the window moved inside
    ``[0, duration]``."""
    decision, bounded = computed
    if (window.start, window.end) == (decision.start, decision.end):
        return bounded
    if window.start >= 0.0 and window.end <= duration:
        return window
    start, end = _bound_window(
        window.start, window.end, window.peak, settings.max_clip, 0.0, float(duration)
    )
    return Window(start, end, window.peak, window.value)


def merge_windows(
    windows: list[Window], settings: FusionSettings, gap: float = MERGE_GAP
) -> list[Window]:
    """Merge overlapping/adjacent windows (gap < 2 s); when the union would exceed the
    maximum clip length keep the stronger window only."""
    if not windows:
        return []
    ordered = sorted(windows, key=lambda w: w.start)
    merged: list[Window] = [ordered[0]]
    for win in ordered[1:]:
        cur = merged[-1]
        if win.start - cur.end < gap:
            union_start = min(cur.start, win.start)
            union_end = max(cur.end, win.end)
            stronger = cur if cur.value >= win.value else win
            if union_end - union_start <= settings.max_clip:
                merged[-1] = Window(union_start, union_end, stronger.peak, stronger.value)
            else:
                merged[-1] = stronger
        else:
            merged.append(win)
    return merged


def iou(a: Window, b: Window) -> float:
    inter = max(0.0, min(a.end, b.end) - max(a.start, b.start))
    union = (a.end - a.start) + (b.end - b.start) - inter
    return inter / union if union > 0 else 0.0


def non_max_suppression(windows: list[Window], threshold: float = NMS_IOU) -> list[Window]:
    kept: list[Window] = []
    for win in sorted(windows, key=lambda w: -w.value):
        if all(iou(win, k) <= threshold for k in kept):
            kept.append(win)
    return kept


def score_window(peak_value: float, composite: np.ndarray, threshold: float) -> int:
    """0-100 internal relevance estimate: percentile within the recording blended with the
    absolute composite above the threshold."""
    if composite.size == 0:
        return 0
    pct = float(np.mean(composite < peak_value))
    score = int(round(100.0 * (0.5 * pct + 0.5 * sigmoid(peak_value - threshold))))
    return max(0, min(100, score))


def rank_candidates(candidates: list[Candidate]) -> None:
    """Order by rank priority (score + focus bonus + LLM bonus), then score, composite and
    start, and number the ranks 1..N in that order."""
    candidates.sort(key=lambda c: (-c.priority, -c.score, -c.composite, c.start))
    for rank, cand in enumerate(candidates, start=1):
        cand.rank = rank


# --------------------------------------------------------------------------------------
# categories and reasoning (pt-BR)
# --------------------------------------------------------------------------------------


def audio_energy_text(energy_z: float, above_typical_db: float | None) -> str:
    """pt-BR fragment for the audio energy contribution, stated from the audio itself.

    ``energy_z`` is a robust z-score against the rolling ~90 s baseline (how unusual the
    level is *for that part* of the recording), not a ratio: it is never written as "N×".
    When the window's loudest second is clearly above the recording's typical level the
    text gives that difference in dB; otherwise it describes the z-score in bands relative
    to the surrounding part of the recording.
    """
    if energy_z <= 0.5:
        return ""
    if above_typical_db is not None and above_typical_db >= LEVEL_STATEMENT_MIN_DB:
        return f"volume {round(above_typical_db)} dB acima do nível típico da gravação"
    if energy_z >= 4.0:
        return "energia de áudio muito acima do normal naquele trecho"
    if energy_z >= 2.0:
        return "energia de áudio bem acima do normal naquele trecho"
    return "energia de áudio acima do normal naquele trecho"


def _kw_text(keywords: list[TimedKeyword]) -> str:
    """'menção a …' for the two strongest keywords; the user's own keywords come first."""
    phrases: list[str] = []
    for k in sorted(keywords, key=lambda k: (not k.custom, -k.weight)):
        if k.phrase not in phrases:
            phrases.append(k.phrase)
        if len(phrases) == 2:
            break
    if not phrases:
        return ""
    if len(phrases) == 1:
        return f"menção a '{phrases[0]}'"
    return f"menções a '{phrases[0]}' e '{phrases[1]}'"


def categorize(
    *,
    audio_sig: dict[str, Any],
    transcript_sig: dict[str, Any],
    visual_sig: dict[str, Any],
    keywords: list[TimedKeyword],
    contributions: dict[str, float],
) -> str:
    cats = {k.category for k in keywords}
    scream = float(audio_sig.get("screamLike") or 0.0)
    laughter = float(audio_sig.get("laughterLike") or 0.0)
    pause = bool(transcript_sig.get("pauseBefore"))
    burst = bool(audio_sig.get("burst"))
    motion_z = float(visual_sig.get("motionZ") or 0.0)
    audio_c = contributions.get("audio", 0.0)
    transcript_c = contributions.get("transcript", 0.0)

    if scream >= 0.5 or "reaction" in cats:
        return "reaction"
    if "hype" in cats:
        return "hype"
    if laughter >= 0.5 or "funny" in cats:
        return "funny"
    if "tension" in cats or (pause and burst):
        return "tension"
    if motion_z >= 1.5 and "gameplay" in cats:
        return "gameplay"
    if transcript_sig.get("available") and transcript_c >= 0.6 and audio_c < 0.3:
        return "quote"
    return "highlight"


def build_reasoning(
    items: list[tuple[str, float, str]],
    *,
    pinned: list[str] | None = None,
    lead: str | None = None,
) -> str:
    """``items``: (contributor id, contribution, pt-BR fragment). Top 2-3 by contribution.

    ``pinned`` fragments are always stated (the mention of the user's own keywords): they
    take the place of the weakest of the three. ``lead`` opens the sentence ("Vídeo
    inteiro usado como clipe…") and the fragments follow after a colon."""
    ordered = [it for it in sorted(items, key=lambda it: -it[1]) if it[1] > 0.1 and it[2]]
    fragments = [it[2] for it in ordered[:3]]
    for fragment in pinned or []:
        if fragment and fragment not in fragments:
            fragments = [*fragments[:2], fragment]
    if lead:
        if not fragments:
            return lead + "."
        body = fragments[0] if len(fragments) == 1 else ", ".join(fragments[:-1])
        if len(fragments) > 1:
            body += " e " + fragments[-1]
        return f"{lead}: {body}."
    if not fragments:
        return "Momento acima do limiar de relevância da gravação."
    if len(fragments) == 1:
        sentence = fragments[0]
    else:
        sentence = ", ".join(fragments[:-1]) + " e " + fragments[-1]
    sentence = sentence[0].upper() + sentence[1:]
    return sentence + "."


# --------------------------------------------------------------------------------------
# main entry point
# --------------------------------------------------------------------------------------


def _window_max(series: np.ndarray | None, s: int, e: int) -> float:
    if series is None or len(series) == 0:
        return 0.0
    s = max(0, min(s, len(series) - 1))
    e = max(s + 1, min(e, len(series)))
    return float(np.max(series[s:e]))


def _window_sum(series: np.ndarray | None, s: int, e: int) -> float:
    if series is None or len(series) == 0:
        return 0.0
    s = max(0, min(s, len(series) - 1))
    e = max(s + 1, min(e, len(series)))
    return float(np.sum(series[s:e]))


def detect_candidates(
    duration: float,
    settings: FusionSettings,
    *,
    audio: AudioFeatures | None,
    transcript: TranscriptSeries | None,
    visual: VisualFeatures | None,
    llm: np.ndarray | None = None,
    llm_meta: dict[str, Any] | None = None,
    scope: PeakScope | None = None,
    score_reference: np.ndarray | None = None,
) -> FusionResult:
    """Candidates of the recording (or of a live window: ``scope`` keeps the peaks the
    window commits — after the merge and the suppression of near-duplicates, which see every
    peak of the window — and ``score_reference`` joins the population of the score
    percentile: the session's composite sample)."""
    n = max(1, int(math.ceil(float(duration))))
    focus = settings.focus
    composite, weights, components, weight_series = composite_parts(
        n, audio=audio, transcript=transcript, visual=visual, llm=llm, focus=focus
    )
    # The focus steers detection and ranking only: the stored score is computed from the
    # composite without it (the same moment gets the same internal estimate either way).
    if focus.adjustments:
        neutral = composite_parts(n, audio=audio, transcript=transcript, visual=visual, llm=llm)[0]
    else:
        neutral = composite
    population = neutral
    if score_reference is not None and len(score_reference):
        population = np.concatenate([np.asarray(score_reference, dtype=np.float64), neutral])
    threshold = threshold_for(settings.sensitivity)
    peaks = pick_peaks(
        composite,
        threshold,
        settings.min_clip,
        left_edge=scope.left_edge if scope is not None else True,
        right_edge=scope.right_edge if scope is not None else True,
    )

    silences = audio.silences if audio is not None and audio.available else []
    boundaries = transcript.boundaries if transcript is not None and transcript.available else []
    start_anchors = np.array(sorted({e for _, e in silences} | set(boundaries)), dtype=np.float64)
    end_anchors = np.array(sorted({s for s, _ in silences} | set(boundaries)), dtype=np.float64)

    def peak_window(peak: int, bounds: tuple[float, float]) -> Window:
        start, end = expand_window(
            composite, peak, threshold, settings, float(duration), bounds=bounds
        )
        start, end = snap_window(
            start,
            end,
            peak,
            start_anchors=start_anchors,
            end_anchors=end_anchors,
            settings=settings,
            duration=float(duration),
            bounds=bounds,
        )
        if end - start < min(settings.min_clip, float(duration)) - 1e-6:
            # recording shorter than the minimum clip: use what exists
            start, end = 0.0, float(duration)
        return Window(start, end, peak, float(composite[peak]))

    # An edge of a live window that is not the recording's does not bound the clip windows
    # the merge and the suppression compare: pushed inside the window, the clip of a moment
    # next to that edge could reach (and swallow) a neighbour that the whole recording keeps
    # apart, and the window owning the neighbour would lose it. A window reported as is
    # keeps the one computed inside the media; a merged one is brought back inside.
    media = (0.0, float(duration))
    decision_bounds = (
        0.0 if scope is None or scope.left_edge else -math.inf,
        float(duration) if scope is None or scope.right_edge else math.inf,
    )
    windows: list[Window] = []
    inside: dict[int, tuple[Window, Window]] = {}  # peak -> (decision window, media window)
    for peak in peaks:
        decision = peak_window(peak, decision_bounds)
        inside[peak] = (
            decision,
            decision if decision_bounds == media else peak_window(peak, media),
        )
        windows.append(decision)

    windows = merge_windows(windows, settings)
    windows = non_max_suppression(windows)
    windows.sort(key=lambda w: (-w.value, w.start))
    if scope is not None:
        # the commit rule: a live window reports the moments whose peak is its own
        windows = [
            _within_media(w, inside[w.peak], settings, float(duration))
            for w in windows
            if scope.contains(w.peak + 0.5)
        ]
    whole_video = False
    if not windows and settings.short_source and has_any_signal(audio, transcript, visual):
        # a source shorter than the minimum clip with no moment above the threshold: the
        # whole video is the candidate (its score stays what the signals say)
        peak = int(np.argmax(composite)) if composite.size else 0
        windows = [Window(0.0, float(duration), peak, float(composite[peak]))]
        whole_video = True
    # Without focus the order by score is the order by composite: the top N windows are
    # the top N candidates. With focus the +10 priority can lift a candidate of a focused
    # category over stronger ones, so a larger pool is built and cut after ranking.
    pool = settings.max_candidates * (3 if focus.categories else 1)
    windows = windows[:pool]
    keyword_scores = (
        focused_keyword_score(transcript, n, focus)
        if transcript is not None and transcript.available
        else None
    )

    candidates: list[Candidate] = []
    for win in windows:
        s, e = int(math.floor(win.start)), int(math.ceil(win.end))
        peak = win.peak
        # weights in effect at the peak (the nominal ones unless a modality has a gap there)
        point_weights = {key: float(series[peak]) for key, series in weight_series.items()}
        contributions = {
            key: float(
                point_weights.get(key, 0.0)
                * float(np.clip(components[key][peak], CLAMP_LOW, CLAMP_HIGH))
            )
            for key in components
        }

        audio_sig: dict[str, Any] = {"available": False}
        transcript_sig: dict[str, Any] = {"available": False}
        visual_sig: dict[str, Any] = {"available": False}
        llm_sig: dict[str, Any] = {"available": False}
        keywords: list[TimedKeyword] = []
        items: list[tuple[str, float, str]] = []
        pinned: list[str] = []

        def window_weight(key: str, s: int = s, e: int = e) -> float:
            """Largest effective weight of a modality inside the window (its nominal
            weight unless a gap or another modality's gap changes it there)."""
            return _window_max(weight_series.get(key), s, e)

        if audio is not None and audio.available:
            energy = _window_max(audio.energy_z, s, e)
            scream = _window_max(audio.scream_like, s, e)
            laughter = _window_max(audio.laughter_like, s, e)
            burst = _window_max(audio.burst, s, e)
            typical = audio.typical_level()
            above_typical = (
                _window_max(audio.energy_db, s, e) - typical
                if typical is not None and len(audio.energy_db)
                else None
            )
            audio_sig = {
                "available": True,
                "method": audio.method,
                "energyZ": round(energy, 2),
                "levelAboveTypicalDb": (
                    round(above_typical, 1) if above_typical is not None else None
                ),
                "peakDbfs": round(_window_max(audio.peak_dbfs, s, e), 1),
                "burst": bool(burst >= 1.0),
                "screamLike": round(scream, 2),
                "laughterLike": round(laughter, 2),
            }
            w_a = window_weight("audio")
            items.append(
                (
                    "audio.energy",
                    w_a * min(4.0, max(0.0, energy)),
                    audio_energy_text(energy, above_typical),
                )
            )
            items.append(
                (
                    "audio.scream",
                    w_a * 0.75 * focus.factor("audio.scream") * scream * 4,
                    "grito detectado" if scream >= 0.4 else "",
                )
            )
            items.append(
                (
                    "audio.laughter",
                    w_a * 0.75 * focus.factor("audio.laughter") * laughter * 4,
                    "risadas detectadas" if laughter >= 0.4 else "",
                )
            )
            items.append(
                (
                    "audio.burst",
                    w_a * 0.5 * focus.factor("audio.burst") * burst,
                    "explosão sonora curta" if burst >= 1.0 else "",
                )
            )

        if transcript is not None and transcript.available:
            keywords = transcript.keywords_between(win.start, win.end)
            rate_z = _window_max(transcript.speech_rate_z, s, e)
            excl = int(_window_sum(transcript.exclamation, s, e))
            pause = _window_max(transcript.pause_before, s, e)
            kw_score = _window_max(keyword_scores, s, e)
            transcript_sig = {
                "available": True,
                "speechDetected": transcript.speech_detected,
                "model": transcript.model,
                "language": transcript.language,
                "languageProbability": (
                    round(transcript.language_probability, 3)
                    if transcript.language_probability is not None
                    else None
                ),
                "languageConfidence": transcript.language_confidence,
                # share of the window that was transcribed (< 1 next to a failed chunk)
                "coverage": round(coverage_fraction(transcript.gaps, win.start, win.end), 3),
                "speechRateZ": round(rate_z, 2),
                "keywords": [
                    {"word": k.phrase, "category": k.category, "weight": k.weight}
                    | ({"custom": True} if k.custom else {})
                    for k in sorted(keywords, key=lambda k: not k.custom)[:8]
                ],
                "exclamations": excl,
                "pauseBefore": bool(pause >= 1.0),
            }
            w_t = window_weight("transcript")
            keyword_text = _kw_text(keywords)
            if any(k.custom for k in keywords):
                pinned.append(keyword_text)
            items.append(
                (
                    "transcript.speech_rate",
                    w_t * 0.5 * min(4.0, max(0.0, rate_z)),
                    "fala rápida" if rate_z >= 1.5 else "",
                )
            )
            items.append(("transcript.keyword", w_t * 0.6 * kw_score, keyword_text))
            items.append(
                (
                    "transcript.exclamation",
                    w_t * 0.4 * min(3, excl),
                    "exclamações na fala" if excl else "",
                )
            )
            items.append(
                (
                    "transcript.pause",
                    w_t * 0.5 * focus.factor("transcript.pause") * pause,
                    "silêncio seguido de reação" if pause >= 1.0 else "",
                )
            )

        if visual is not None and visual.available:
            motion = _window_max(visual.motion_z, s, e)
            cuts = int(_window_sum(visual.scene_cut, s, e))
            flash = _window_max(visual.flash, s, e)
            visual_sig = {
                "available": True,
                # share of the window covered by decoded frames
                "coverage": round(coverage_fraction(visual.gaps, win.start, win.end), 3),
                "motionZ": round(motion, 2),
                "sceneCuts": cuts,
                "flash": bool(flash >= 1.0),
            }
            w_v = window_weight("visual")
            items.append(
                (
                    "visual.motion",
                    w_v * 0.6 * focus.factor("visual.motion") * min(4.0, max(0.0, motion)),
                    "muito movimento na tela" if motion >= 1.5 else "",
                )
            )
            items.append(
                ("visual.scene_cut", w_v * 1.0 * min(2, cuts), "corte de cena" if cuts else "")
            )
            items.append(
                ("visual.flash", w_v * 0.5 * flash, "flash de luz" if flash >= 1.0 else "")
            )

        if llm is not None:
            llm_score = _window_max(llm, s, e)
            llm_sig = {"available": True, "score": round(llm_score, 2)}
            if llm_meta:
                llm_sig.update({k: v for k, v in llm_meta.items() if k in ("provider", "model")})
            items.append(
                (
                    "llm.score",
                    LLM_WEIGHT * max(0.0, (llm_score - 0.5) * 6.0),
                    "trecho bem avaliado pelo modelo de linguagem" if llm_score >= 0.6 else "",
                )
            )
        elif llm_meta:
            llm_sig = {
                "available": False,
                **{k: v for k, v in llm_meta.items() if k in ("provider", "error")},
            }

        contributors = [it[0] for it in sorted(items, key=lambda it: -it[1]) if it[1] > 0.1][:3]
        category = categorize(
            audio_sig=audio_sig,
            transcript_sig=transcript_sig,
            visual_sig=visual_sig,
            keywords=keywords,
            contributions=contributions,
        )
        reasoning = build_reasoning(
            items, pinned=pinned, lead=WHOLE_VIDEO_REASON if whole_video else None
        )
        text = (
            transcript.text_between(win.start, win.end)
            if transcript is not None and transcript.available
            else ""
        )
        score = score_window(float(neutral[peak]), population, threshold)
        bonus = focus.rank_bonus(category)
        custom_found = sorted({k.phrase for k in keywords if k.custom})
        signals: dict[str, Any] = {
            "audio": audio_sig,
            "transcript": transcript_sig,
            "visual": visual_sig,
            "llm": llm_sig,
            "focus": {
                "categories": list(focus.categories),
                "adjustments": dict(focus.adjustments),
                "inFocus": bool(bonus),
                "rankBonus": bonus,
                "customKeywords": custom_found,
                # composite without the focus (the one the score comes from)
                "neutralComposite": round(float(neutral[peak]), 3),
            },
            "composite": round(win.value, 3),
            "threshold": round(threshold, 3),
            # weights in effect at the peak: the nominal ones unless a modality has a gap
            "weights": {k: round(v, 3) for k, v in point_weights.items()},
            "contributors": contributors,
            "peakSeconds": round(peak + 0.5, 1),
            "scoreNote": SCORE_NOTE,
        }
        if settings.short_source:
            signals["shortSource"] = {
                "durationSeconds": round(float(duration), 2),
                "wholeVideo": True,
                "belowThreshold": bool(win.value <= threshold),
            }
        candidates.append(
            Candidate(
                rank=0,
                start=round(win.start, 2),
                end=round(win.end, 2),
                peak=peak + 0.5,
                composite=win.value,
                score=score,
                category=category,
                reasoning=reasoning,
                transcript=text,
                signals=signals,
                contributors=contributors,
                priority=float(score + bonus),
            )
        )

    rank_candidates(candidates)
    del candidates[settings.max_candidates :]
    return FusionResult(
        candidates,
        composite,
        threshold,
        weights,
        components,
        peaks,
        weight_series,
        whole_video=whole_video,
        neutral=neutral,
    )
