"""Live capture logic (``app.analysis.live``): the commit rule on synthetic composite series
split into segments, the window plan on the session grid, the session score sample and the
session language."""

from __future__ import annotations

import itertools
import math
import random
from collections import Counter
from collections.abc import Iterator

import numpy as np
import pytest

from app.analysis.audio_features import AudioFeatures
from app.analysis.common import sigmoid
from app.analysis.fusion import FusionSettings, PeakScope, detect_candidates, pick_peaks
from app.analysis.live import (
    SAMPLE_SIZE,
    CommitRange,
    commit_range,
    contiguous,
    fixed_language,
    handover_point,
    next_language,
    own_seconds,
    plan_window,
    post_roll_seconds,
    sample_seed,
    seconds_before,
    session_transcript_json,
    update_sample,
)
from app.analysis.transcription import Segment, Transcript, Word
from app.models import AnalysisSettings, LiveContext

# --------------------------------------------------------------------------------------
# synthetic sessions
# --------------------------------------------------------------------------------------


def audio_only(values: np.ndarray) -> AudioFeatures:
    """Audio features whose composite is ``values`` (audio is the only modality: weight 1,
    and the other audio terms are zero)."""
    n = len(values)
    zeros = np.zeros(n)
    return AudioFeatures(
        available=True,
        seconds=n,
        energy_db=np.full(n, -30.0),
        energy_z=np.asarray(values, dtype=np.float64),
        onset_z=zeros,
        burst=zeros,
        sustained=zeros,
        scream_like=zeros,
        laughter_like=zeros,
        silence=zeros,
        peak_dbfs=np.full(n, -10.0),
        centroid_hz=zeros,
        silences=[],
        typical_db=-30.0,
    )


def bumps(total: int, seconds: list[int], height: float = 3.0, width: float = 1.0) -> np.ndarray:
    """A session composite (one value per session second) with a narrow bump whose maximum
    is session second ``k`` (peak time ``k + 0.5``) for every ``k``."""
    t = np.arange(total, dtype=np.float64)
    out = np.zeros(total)
    for k in seconds:
        out = np.maximum(out, height * np.exp(-0.5 * ((t - k) / width) ** 2))
    return out


def fusion_settings(min_clip: float, max_clip: float) -> FusionSettings:
    return FusionSettings(
        min_clip=min_clip,
        max_clip=max_clip,
        target_clip=min_clip,
        sensitivity=50,
        max_candidates=50,
    )


def run_live_session(
    composite: np.ndarray,
    segments: list[tuple[float, float]],
    *,
    min_clip: float,
    max_clip: float,
    use_context: bool = True,
) -> list[tuple[int, float]]:
    """Every segment analysed the way a live_segment job does (window = previous tail +
    segment on the session grid, fusion with the commit scope): ``(sequence, peak)`` of the
    committed candidates, peaks in session seconds."""
    settings = fusion_settings(min_clip, max_clip)
    post_roll = post_roll_seconds(max_clip)
    reported: list[tuple[int, float]] = []
    committed_until: float | None = None
    for sequence, (start, duration) in enumerate(segments):
        final = sequence == len(segments) - 1
        previous = segments[sequence - 1][1] if sequence else None
        plan = plan_window(start, previous)
        commit = commit_range(
            start=start,
            duration=duration,
            sequence=sequence,
            final=final,
            post_roll=post_roll,
            previous_duration=previous,
            committed_until=committed_until if use_context else None,
        )
        length = start + duration - plan.origin
        first = int(plan.origin)
        window = composite[first : first + math.ceil(length)]
        fusion = detect_candidates(
            length,
            settings,
            audio=audio_only(window),
            transcript=None,
            visual=None,
            scope=plan.scope(commit, starts_session=sequence == 0, ends_session=final),
        )
        for cand in fusion.candidates:
            peak = plan.origin + cand.peak
            assert commit.contains(peak), (sequence, peak, commit)
            reported.append((sequence, peak))
        committed_until = None if math.isinf(commit.end) else commit.end
    return reported


def session_peaks(
    composite: np.ndarray,
    segments: list[tuple[float, float]],
    *,
    min_clip: float,
    max_clip: float,
    use_context: bool = True,
) -> list[float]:
    """The peaks a live session reports, sorted (each one as many times as reported)."""
    reported = run_live_session(
        composite, segments, min_clip=min_clip, max_clip=max_clip, use_context=use_context
    )
    return sorted(peak for _, peak in reported)


def batch_peaks(composite: np.ndarray, duration: float, min_clip: float, max_clip: float) -> list:
    fusion = detect_candidates(
        duration,
        fusion_settings(min_clip, max_clip),
        audio=audio_only(composite[: math.ceil(duration)]),
        transcript=None,
        visual=None,
    )
    return sorted(c.peak for c in fusion.candidates)


# segments with fractional boundaries; postRoll = clamp(20 / 2, 8, 30) = 10 s, so the
# hand-over points are 50, 110.5 and 171.25 (the final segment reports up to its end)
SEGMENTS = [(0.0, 60.0), (60.0, 60.5), (120.5, 60.75), (181.25, 48.75)]
# session second -> the segment expected to report the peak at second + 0.5
PLANTED = {
    0: 0,  # the very first second of the session (an edge peak only segment 0 may report)
    20: 0,
    49: 0,  # 49.5 < 50: just before the first hand-over point
    59: 1,  # inside segment 0's post-roll (its last second): left to segment 1
    99: 1,
    110: 2,  # 110.5 is the hand-over point itself: [.., 110.5) excludes it, [110.5, ..) not
    120: 2,  # the last (partial) second of segment 1's window, a peak of segment 2's
    160: 2,
    171: 3,  # 171.5 >= 171.25
    215: 3,
    229: 3,  # the last second of the session: the final segment reports it
}


@pytest.mark.parametrize("use_context", [True, False])
def test_every_peak_is_reported_exactly_once_across_segments(use_context: bool) -> None:
    composite = bumps(230, list(PLANTED))
    # the planted moments are what a batch analysis of the whole session finds
    assert batch_peaks(composite, 230.0, 5.0, 20.0) == sorted(k + 0.5 for k in PLANTED)

    reported = run_live_session(
        composite, SEGMENTS, min_clip=5.0, max_clip=20.0, use_context=use_context
    )
    peaks = Counter(peak for _, peak in reported)
    assert sorted(peaks) == sorted(k + 0.5 for k in PLANTED)
    assert set(peaks.values()) == {1}, "a peak was reported twice"
    assert {peak - 0.5: sequence for sequence, peak in reported} == PLANTED


def test_a_segment_shorter_than_the_post_roll_keeps_its_moments_up_to_1_s_before_its_end() -> None:
    """A 6 s segment (postRoll 10 s): the next window's tail is only those 6 s, so it
    starts at 60 and its first second can never be a peak. Handing over at 66 - 10 = 56
    would leave [56, 60) to a window that cannot see it; handing over at the segment's
    start (60) would leave it the moment of second 60, on that first second. The segment
    keeps the moments up to 1 s before its end (65), its own window's last second
    excluded."""
    segments = [(0.0, 60.0), (60.0, 6.0), (66.0, 60.0)]
    planted = {40: 0, 50: 1, 60: 1, 70: 2, 100: 2, 125: 2}
    composite = bumps(126, list(planted))
    assert batch_peaks(composite, 126.0, 5.0, 20.0) == sorted(k + 0.5 for k in planted)
    for use_context in (True, False):
        reported = run_live_session(
            composite, segments, min_clip=5.0, max_clip=20.0, use_context=use_context
        )
        assert {peak - 0.5: sequence for sequence, peak in reported} == planted
        assert len(reported) == len(planted)
    assert handover_point(60.0, 6.0, 10.0) == 65.0
    assert commit_range(
        start=66.0, duration=60.0, sequence=2, final=True, post_roll=10.0, previous_duration=6.0
    ) == CommitRange(65.0, math.inf)


# Layouts whose boundaries lost moments when a segment handed over at
# ``end - min(postRoll, duration)``: segments shorter than about postRoll + 2 s (the API's
# segmentSeconds 20-31 with the default maxClipSeconds 60), fractional cuts, a short
# segment 0. A moment is planted on the second of every boundary (the next window's first
# second when the cut is whole, which can never be a peak there). The moments are 10 s
# apart, 13 s or more next to the session's start or end (where a clip cannot extend, so it
# is moved inside the recording and would merge with its neighbour's).
BOUNDARY_LAYOUTS: dict[str, tuple[list[tuple[float, float]], float, list[int]]] = {
    "20 s segments, postRoll 30": (
        [(20.0 * i, 20.0) for i in range(6)],
        60.0,
        [5, 20, 30, 40, 50, 60, 70, 80, 90, 100, 119],
    ),
    "fractional 20 s cuts, postRoll 30": (
        [(0.0, 20.033), (20.033, 20.0), (40.033, 20.067), (60.1, 20.0), (80.1, 20.0)],
        60.0,
        [5, 20, 30, 40, 50, 60, 70, 80, 99],
    ),
    "30 s segments, postRoll 30": (
        [(30.0 * i, 30.0) for i in range(5)],
        60.0,
        [5, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 149],
    ),
    "short segment 0, postRoll 10": (
        [(0.0, 5.0), (5.0, 60.0), (65.0, 30.0)],
        20.0,
        [0, 13, 23, 33, 43, 53, 65, 75, 94],
    ),
    "segment 0 shorter than 1.5 s, postRoll 10": (
        [(0.0, 1.2), (1.2, 40.0), (41.2, 30.0)],
        20.0,
        [0, 13, 23, 33, 43, 53, 70],
    ),
}


@pytest.mark.parametrize("layout", list(BOUNDARY_LAYOUTS))
@pytest.mark.parametrize("use_context", [True, False])
def test_short_segments_lose_no_moment_at_their_boundaries(layout: str, use_context: bool) -> None:
    segments, max_clip, planted = BOUNDARY_LAYOUTS[layout]
    end = segments[-1][0] + segments[-1][1]
    composite = bumps(math.ceil(end), planted)
    expected = [k + 0.5 for k in planted]
    assert batch_peaks(composite, end, 5.0, max_clip) == expected
    peaks = session_peaks(
        composite, segments, min_clip=5.0, max_clip=max_clip, use_context=use_context
    )
    assert peaks == expected  # every moment, each one once


def test_a_window_that_starts_with_the_session_may_peak_on_its_first_second() -> None:
    """A segment 0 of 1.2 s commits nothing after 0.2 s (1 s before its end): the moment of
    the session's first second is segment 1's, whose window starts at 0 too, a real
    edge."""
    segments, max_clip, planted = BOUNDARY_LAYOUTS["segment 0 shorter than 1.5 s, postRoll 10"]
    composite = bumps(72, planted)
    reported = run_live_session(composite, segments, min_clip=5.0, max_clip=max_clip)
    assert (1, 0.5) in reported
    plan = plan_window(1.2, 1.2)
    assert plan.origin == 0.0
    assert plan.scope(CommitRange(0.2, 31.2), starts_session=False, ends_session=False).left_edge


def test_a_moment_next_to_the_window_start_does_not_swallow_its_neighbour() -> None:
    """minClipSeconds 15, postRoll 30: segment 1's window starts at 15. The moment at 18.5
    is segment 0's; pushed inside segment 1's window, its clip ([15, 30] instead of the
    [12.3, 27.3] it has in the recording) would reach the clip of the moment at 37.5
    (closer than the 2 s merge gap) and swallow it, although the whole recording keeps
    both."""
    composite = bumps(120, [18, 37])
    segments = [(0.0, 60.0), (60.0, 60.0)]
    assert batch_peaks(composite, 120.0, 15.0, 60.0) == [18.5, 37.5]
    for use_context in (True, False):
        reported = run_live_session(
            composite, segments, min_clip=15.0, max_clip=60.0, use_context=use_context
        )
        assert reported == [(0, 18.5), (1, 37.5)]


def test_a_moment_next_to_the_window_end_does_not_swallow_its_neighbour() -> None:
    """postRoll 10: the stronger moment at 57.5 is segment 1's (after 50); pushed inside
    segment 0's window, which ends at 60, its clip would reach the clip of the moment at
    38.5 and win the merge, so segment 0 would report neither."""
    composite = np.maximum(bumps(120, [38], height=3.0), bumps(120, [57], height=3.5))
    segments = [(0.0, 60.0), (60.0, 60.0)]
    assert batch_peaks(composite, 120.0, 15.0, 20.0) == [38.5, 57.5]
    for use_context in (True, False):
        reported = run_live_session(
            composite, segments, min_clip=15.0, max_clip=20.0, use_context=use_context
        )
        assert reported == [(0, 38.5), (1, 57.5)]


def random_layouts(
    seed: int, count: int
) -> Iterator[tuple[list[tuple[float, float]], list[int], float, float]]:
    """Sessions of 2-5 segments around a segment length (20-60 s, keyframe jitter), a
    quarter of them 2.5-12 s long (OBS file splits), with moments ``max(minClip, 7) +
    3..13`` s apart (a clip spans at least 7 s around its peak): ``(segments, planted
    seconds, minClip, maxClip)``."""
    rng = random.Random(seed)
    for _ in range(count):
        max_clip = rng.choice([12.0, 20.0, 60.0])
        min_clip = rng.choice([m for m in (3.0, 5.0, 8.0, 15.0) if m <= max_clip - 2.0])
        length = rng.choice([20.0, 25.0, 30.0, 45.0, 60.0])
        segments: list[tuple[float, float]] = []
        t = 0.0
        for _segment in range(rng.randint(2, 5)):
            short = rng.random() < 0.25
            duration = round(rng.uniform(2.5, 12.0) if short else length + rng.uniform(-2, 6), 3)
            segments.append((round(t, 3), duration))
            t = round(t + duration, 3)
        planted: list[int] = []
        k = rng.randint(0, 3)
        while k < math.ceil(t):
            planted.append(k)
            k += max(int(min_clip), 7) + 3 + rng.randint(0, 10)
        yield segments, planted, min_clip, max_clip


@pytest.mark.slow
def test_random_sessions_report_every_moment_exactly_once() -> None:
    """Every moment a whole-recording analysis keeps is reported once by the live session,
    whatever the segment lengths (at least 2.5 s) and clip settings."""
    checked = 0
    for segments, planted, min_clip, max_clip in random_layouts(20261023, 60):
        end = segments[-1][0] + segments[-1][1]
        composite = bumps(math.ceil(end), planted)
        expected = [k + 0.5 for k in planted]
        if batch_peaks(composite, end, min_clip, max_clip) != expected:
            continue  # moments the whole recording merges: no reference for the session
        checked += 1
        for use_context in (True, False):
            peaks = session_peaks(
                composite, segments, min_clip=min_clip, max_clip=max_clip, use_context=use_context
            )
            assert peaks == expected, (segments, planted, min_clip, max_clip, use_context)
    assert checked >= 35


def test_a_moment_decaying_into_a_window_does_not_hide_the_next_one() -> None:
    """minClipSeconds 20, postRoll 30: segment 1's window starts at second 15, where the
    composite of the moment at 12.5 (reported by segment 0) is still above the threshold.
    The moment at 34.5 (22 s later: a batch analysis keeps both) is segment 1's; were the
    window's first second a peak, it would suppress it (19 s apart) and nobody would
    report it."""
    segments = [(0.0, 60.0), (60.0, 60.0), (120.0, 60.0)]
    composite = np.maximum(
        bumps(180, [12], height=3.5, width=3.0), bumps(180, [34], height=2.0, width=1.0)
    )
    assert batch_peaks(composite, 180.0, 20.0, 60.0) == [12.5, 34.5]
    reported = run_live_session(composite, segments, min_clip=20.0, max_clip=60.0)
    assert reported == [(0, 12.5), (1, 34.5)]


def test_a_session_of_one_final_segment_reports_everything() -> None:
    composite = bumps(40, [0, 20, 39])
    reported = run_live_session(composite, [(0.0, 40.0)], min_clip=5.0, max_clip=12.0)
    assert sorted(peak for _, peak in reported) == [0.5, 20.5, 39.5]


def test_window_edges_are_not_peaks_unless_the_session_starts_or_ends_there() -> None:
    """A composite still decaying from a moment before the window is highest on the
    window's first second: as a peak it would suppress the real peak 10 s later
    (minimum distance 15 s), which the previous window does not report either."""
    composite = np.array([3.0, 2.8, 2.0, 1.0, 0.5, 0.2, 0.0, 0.0, 0.0, 1.0, 2.0, 1.0, 0.0])
    assert pick_peaks(composite, 1.5, 15) == [0]
    assert pick_peaks(composite, 1.5, 15, left_edge=False) == [10]
    rising = composite[::-1].copy()
    assert pick_peaks(rising, 1.5, 15) == [12]
    assert pick_peaks(rising, 1.5, 15, right_edge=False) == [2]


# --------------------------------------------------------------------------------------
# commit range and window plan
# --------------------------------------------------------------------------------------


def test_the_handover_point_leaves_the_next_window_some_context() -> None:
    # long enough (postRoll + 9 s or more): end - postRoll, at least 15 s after the next
    # window's start (its tail is at most 45 s)
    assert handover_point(60.0, 60.0, 30.0) == 90.0
    assert handover_point(0.0, 19.0, 10.0) == 9.0
    # 20 or 30 s segments with postRoll 30: end - postRoll is at (or before) the segment's
    # start, the next window's first second, never a peak there: 8 s after it instead
    assert handover_point(20.0, 20.0, 30.0) == 28.0
    assert handover_point(20.033, 20.0, 30.0) == 29.0  # ceil(20.033) + 8
    assert handover_point(30.0, 30.0, 30.0) == 38.0
    # 6 s with postRoll 10: 1 s before its end (its window's last second is never a peak)
    assert handover_point(60.0, 6.0, 10.0) == 65.0
    # a sliver: its own start (see the known limits)
    assert handover_point(29.867, 0.2, 30.0) == 29.867
    durations = (0.4, 1.0, 2.5, 6.0, 9.0, 20.0, 31.0, 38.5, 45.0, 60.0, 120.0)
    for start, duration, post_roll in itertools.product(
        (0.0, 20.033, 61.5), durations, (8.0, 10.0, 30.0)
    ):
        point = handover_point(start, duration, post_roll)
        end = start + duration
        # within postRoll before the end (where committedUntil is trusted)
        assert end - min(post_roll, duration) - 1e-6 <= point <= end
        # every peak before it can be this window's: its last second is never one
        assert point <= max(start, end - 1.0) + 1e-6
        # every peak after it can be the next window's: its first second is never one
        if duration >= 2.5:
            assert point > plan_window(end, duration).origin + 0.5


def test_both_sides_of_a_boundary_use_the_same_handover_point() -> None:
    """Without ``committedUntil`` the next segment recomputes the previous one's hand-over
    point from its duration: the very number that segment reported."""
    durations = (1.0, 6.0, 19.0, 20.0, 25.067, 30.0, 44.9, 60.0)
    for start, duration, post_roll in itertools.product(
        (0.0, 20.033, 61.5), durations, (8.0, 10.0, 30.0)
    ):
        point = handover_point(start, duration, post_roll)
        following = {
            "start": round(start + duration, 3),
            "duration": 60.0,
            "sequence": 4,
            "final": False,
            "post_roll": post_roll,
            "previous_duration": duration,
        }
        assert commit_range(**following).start == point
        assert commit_range(**following, committed_until=point).start == point


def test_post_roll_is_half_the_maximum_clip_clamped() -> None:
    assert post_roll_seconds(12) == 8.0
    assert post_roll_seconds(40) == 20.0
    assert post_roll_seconds(60) == 30.0
    assert post_roll_seconds(180) == 30.0


def test_commit_range_follows_the_rule() -> None:
    first = commit_range(start=0.0, duration=60.0, sequence=0, final=False, post_roll=30.0)
    assert first == CommitRange(-math.inf, 30.0)
    middle = commit_range(
        start=60.0, duration=60.0, sequence=1, final=False, post_roll=30.0, previous_duration=60
    )
    assert middle == CommitRange(30.0, 90.0)
    last = commit_range(
        start=120.0, duration=33.3, sequence=2, final=True, post_roll=30.0, previous_duration=60
    )
    assert last == CommitRange(90.0, math.inf)
    # the previous segment's own hand-over point wins when its context carries it
    assert commit_range(
        start=51.734,
        duration=18.333,
        sequence=2,
        final=True,
        post_roll=8.0,
        previous_duration=26.666,
        committed_until=43.733,
    ) == CommitRange(43.733, math.inf)
    # previous unknown (failed): start - postRoll
    assert commit_range(start=60.0, duration=60.0, sequence=1, final=False, post_roll=8.0) == (
        CommitRange(52.0, 112.0)
    )
    # a hand-over point that cannot be the predecessor's is not trusted
    for wrong in (5.0, 130.0):
        assert commit_range(
            start=60.0,
            duration=60.0,
            sequence=1,
            final=False,
            post_roll=8.0,
            previous_duration=60.0,
            committed_until=wrong,
        ) == CommitRange(52.0, 112.0)
    assert first.to_json() == [None, 30.0] and last.to_json() == [90.0, None]
    assert middle.contains(30.0) and not middle.contains(90.0)


def test_window_plan_starts_on_a_whole_session_second() -> None:
    plan = plan_window(0.0, None)
    assert (plan.origin, plan.file_origin, plan.media_offset, plan.tail_seconds) == (0, 0, 0, 0)

    # previous shorter than 45 s: all of it is the tail
    plan = plan_window(25.067, 25.067)
    assert plan.origin == 0.0 and plan.media_offset == pytest.approx(0.0)
    assert plan.tail_seconds == pytest.approx(25.067)
    # that window starts with the session: its first second is a real edge
    scope = plan.scope(CommitRange(8.0, 34.0), starts_session=False, ends_session=False)
    assert scope.left_edge and not scope.right_edge

    # 51.734 - 26.666 = 25.068 -> the analysis starts on second 26 of the session
    plan = plan_window(51.734, 26.666)
    assert plan.origin == 26.0
    assert plan.file_origin == pytest.approx(25.068)
    assert plan.media_offset == pytest.approx(0.932)
    assert plan.tail_seconds == pytest.approx(25.734)
    assert plan.to_file(56.0) == pytest.approx(30.932)
    assert plan.to_session(3.0) == 29.0

    # a previous segment longer than 45 s: only its last 45 s are analysed
    plan = plan_window(120.0, 60.0)
    assert (plan.origin, plan.file_origin, plan.media_offset) == (75.0, 60.0, 15.0)
    scope = plan.scope(CommitRange(90.0, 150.0), starts_session=False, ends_session=False)
    assert scope == PeakScope(15.0, 75.0, left_edge=False, right_edge=False)

    # no previous and a fractional start: the first whole second of the segment
    plan = plan_window(120.5, None)
    assert plan.origin == 121.0 and plan.media_offset == pytest.approx(0.5)
    # rounding noise never pushes the origin one second later
    assert plan_window(51.666667, 26.666667).origin == 25.0


def test_contiguity_of_the_previous_segment() -> None:
    assert contiguous(0.0, 25.067, 25.067)
    assert contiguous(25.067, 26.666, 51.734)
    assert contiguous(None, None, 60.0)
    assert not contiguous(0.0, 60.0, 90.0)


# --------------------------------------------------------------------------------------
# session score sample
# --------------------------------------------------------------------------------------


def test_each_second_of_the_session_belongs_to_one_segment() -> None:
    neutral = np.arange(106, dtype=np.float64)  # window origin 15: seconds 15..120
    own = own_seconds(neutral, 15.0, 60.0, 120.5)
    assert own.tolist() == list(range(45, 105))  # session seconds 60..119
    assert own_seconds(neutral, 15.0, 60.0).size == 61  # final: up to the window's end
    assert seconds_before(0.0) == 0
    assert seconds_before(25.067) == 25
    assert seconds_before(51.734) == 52
    assert seconds_before(120.5) == 120


def test_sample_keeps_every_value_until_it_is_full_then_stays_bounded() -> None:
    sample = update_sample([], [1.0, 2.0, 3.0], seen_before=0, seed="s:0")
    assert sample == [1.0, 2.0, 3.0]
    sample = update_sample(sample, [4.0, 5.0], seen_before=3, seed="s:1")
    assert sample == [1.0, 2.0, 3.0, 4.0, 5.0]
    big = update_sample(sample, np.arange(1000.0), seen_before=5, seed="s:2")
    assert len(big) == SAMPLE_SIZE
    # deterministic: a retried job computes the same sample
    assert update_sample(sample, np.arange(1000.0), seen_before=5, seed="s:2") == big
    assert update_sample(sample, np.arange(1000.0), seen_before=5, seed="s:3") != big
    # non-finite values are ignored, the values are rounded for the context
    assert update_sample([], [float("nan"), 1.23456], seen_before=0, seed="x") == [1.235]


def test_sample_represents_the_whole_session_uniformly() -> None:
    """60 segments of 60 s, value = session second: the reservoir is a uniform sample of
    the session, not of its beginning or its end."""
    sample: list[float] = []
    for sequence in range(60):
        start = sequence * 60.0
        values = np.arange(start, start + 60.0)
        sample = update_sample(
            sample,
            values,
            seen_before=seconds_before(start),
            seed=sample_seed("session", sequence),
        )
    assert len(sample) == SAMPLE_SIZE
    quarters = Counter(int(v // 900) for v in sample)
    assert set(quarters) == {0, 1, 2, 3}
    assert all(30 <= count <= 70 for count in quarters.values()), quarters
    assert 1800 - 250 <= float(np.mean(sample)) <= 1800 + 250


def test_score_percentile_includes_the_session_sample() -> None:
    window = bumps(40, [20], height=3.0)
    settings = fusion_settings(5.0, 20.0)

    def score(reference: np.ndarray | None) -> int:
        fusion = detect_candidates(
            40.0,
            settings,
            audio=audio_only(window),
            transcript=None,
            visual=None,
            score_reference=reference,
        )
        assert [c.peak for c in fusion.candidates] == [20.5]
        assert fusion.neutral is not None and np.allclose(fusion.neutral, window)
        return fusion.candidates[0].score

    threshold = 1.5  # sensitivity 50
    alone = score(None)
    assert alone == round(100 * (0.5 * np.mean(window < 3.0) + 0.5 * sigmoid(3.0 - threshold)))
    # a session full of stronger moments makes the same peak less exceptional...
    strong = np.full(200, 3.5)
    population = np.concatenate([strong, window])
    expected = round(100 * (0.5 * np.mean(population < 3.0) + 0.5 * sigmoid(3.0 - threshold)))
    assert score(strong) == expected < alone
    # ...and a calm session more
    assert score(np.full(200, -0.5)) > alone


# --------------------------------------------------------------------------------------
# session language and session times
# --------------------------------------------------------------------------------------


def test_session_language_is_fixed_after_the_first_segment_with_speech() -> None:
    auto = AnalysisSettings()
    assert fixed_language(auto, None) == (None, None)
    assert fixed_language(auto, LiveContext(language=None)) == (None, None)
    assert fixed_language(auto, LiveContext(language="pt")) == ("pt", "session")
    explicit = AnalysisSettings(language="en-US")
    assert fixed_language(explicit, LiveContext(language="pt")) == ("en", "settings")

    spoken = Transcript(available=True, language="es", speech_detected=True)
    silent = Transcript(available=True, language=None, speech_detected=False)
    unavailable = Transcript.unavailable("model missing")
    assert next_language(None, spoken) == "es"
    assert next_language(None, silent) is None  # still unknown: the next segment detects
    assert next_language(None, unavailable) is None
    assert next_language("pt", silent) == "pt"
    assert next_language("pt", spoken) == "pt"


def test_transcript_json_is_written_in_session_times() -> None:
    transcript = Transcript(
        available=True,
        language="pt",
        segments=[Segment(1.0, 2.0, "clutch", [Word(1.0, 1.5, " clutch", 0.9)])],
        gaps=[(10.0, 12.0)],
        duration=45.0,
    )
    data = session_transcript_json(transcript.to_json(), 135.0)
    assert data["segments"][0]["start"] == 136.0 and data["segments"][0]["end"] == 137.0
    assert data["segments"][0]["words"][0]["start"] == 136.0
    assert data["gaps"] == [[145.0, 147.0]]
    assert data["originSeconds"] == 135.0 and data["timeBase"] == "session"
    # a later render reads it back with the usual parser
    words = Transcript.from_json(data).words
    assert [(w.start, w.end) for w in words] == [(136.0, 136.5)]
