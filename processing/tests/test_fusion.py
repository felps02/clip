"""Fusion on synthetic z-score series with known peaks."""

from __future__ import annotations

import numpy as np
import pytest

from app.analysis.audio_features import AudioFeatures
from app.analysis.fusion import (
    BASE_WEIGHTS,
    FusionSettings,
    Window,
    audio_energy_text,
    build_reasoning,
    composite_parts,
    compute_composite,
    detect_candidates,
    expand_window,
    merge_windows,
    non_max_suppression,
    pick_peaks,
    score_window,
    snap_window,
    threshold_for,
)
from app.analysis.transcription import TimedKeyword, TranscriptSeries
from app.analysis.visual_features import VisualFeatures
from app.models import CATEGORIES

SETTINGS = FusionSettings(
    min_clip=15, max_clip=60, target_clip=30, sensitivity=50, max_candidates=20
)


def audio_with_peaks(n: int, peaks: dict[int, float], **extra: np.ndarray | list) -> AudioFeatures:
    z = np.zeros(n)
    for t, v in peaks.items():
        z[t] = v
        if t - 1 >= 0:
            z[t - 1] = max(z[t - 1], v * 0.7)
        if t + 1 < n:
            z[t + 1] = max(z[t + 1], v * 0.7)
    fields: dict[str, object] = {
        "available": True,
        "seconds": n,
        "energy_z": z,
        "energy_db": np.full(n, -30.0),
        "onset_z": np.zeros(n),
        "burst": np.zeros(n),
        "sustained": np.zeros(n),
        "scream_like": np.zeros(n),
        "laughter_like": np.zeros(n),
        "silence": np.zeros(n),
        "peak_dbfs": np.full(n, -6.0),
        "centroid_hz": np.zeros(n),
        "silences": [],
    }
    fields.update(extra)
    return AudioFeatures(**fields)  # type: ignore[arg-type]


def transcript_with(
    n: int,
    *,
    keywords: list[TimedKeyword] | None = None,
    speech_rate_z: np.ndarray | None = None,
    keyword_score: np.ndarray | None = None,
    exclamation: np.ndarray | None = None,
    pause_before: np.ndarray | None = None,
    boundaries: list[float] | None = None,
) -> TranscriptSeries:
    return TranscriptSeries(
        available=True,
        model="faster-whisper/base",
        language="pt",
        seconds=n,
        speech_rate=np.zeros(n),
        speech_rate_z=speech_rate_z if speech_rate_z is not None else np.zeros(n),
        keyword_score=keyword_score if keyword_score is not None else np.zeros(n),
        exclamation=exclamation if exclamation is not None else np.zeros(n),
        pause_before=pause_before if pause_before is not None else np.zeros(n),
        keywords=keywords or [],
        boundaries=boundaries or [],
        words=[],
    )


def visual_with(
    n: int, *, motion_z: np.ndarray | None = None, scene_cut: np.ndarray | None = None
) -> VisualFeatures:
    return VisualFeatures(
        available=True,
        seconds=n,
        motion=np.zeros(n),
        motion_z=motion_z if motion_z is not None else np.zeros(n),
        scene_cut=scene_cut if scene_cut is not None else np.zeros(n),
        flash=np.zeros(n),
        brightness=np.zeros(n),
        frames_analyzed=n * 2,
    )


def test_threshold_formula() -> None:
    assert threshold_for(0) == pytest.approx(2.4)
    assert threshold_for(50) == pytest.approx(1.5)
    assert threshold_for(100) == pytest.approx(0.6)


def test_weights_renormalised_when_signals_missing() -> None:
    n = 100
    _, w_audio, _ = compute_composite(
        n, audio=audio_with_peaks(n, {}), transcript=None, visual=None
    )
    assert w_audio == {"audio": pytest.approx(1.0)}
    _, w_av, _ = compute_composite(
        n, audio=audio_with_peaks(n, {}), transcript=None, visual=visual_with(n)
    )
    assert w_av["audio"] == pytest.approx(0.45 / 0.70)
    assert w_av["visual"] == pytest.approx(0.25 / 0.70)
    _, w_all, _ = compute_composite(
        n, audio=audio_with_peaks(n, {}), transcript=transcript_with(n), visual=visual_with(n)
    )
    assert w_all == pytest.approx(BASE_WEIGHTS)
    _, w_llm, comps = compute_composite(
        n, audio=audio_with_peaks(n, {}), transcript=None, visual=None, llm=np.full(n, 0.9)
    )
    assert w_llm["llm"] == pytest.approx(0.2)
    assert "llm" in comps


def test_composite_clamps_each_signal() -> None:
    n = 10
    audio = audio_with_peaks(n, {5: 40.0})
    composite, _, _ = compute_composite(n, audio=audio, transcript=None, visual=None)
    assert composite[5] == pytest.approx(4.0)
    audio.energy_z[:] = -50.0
    composite, _, _ = compute_composite(n, audio=audio, transcript=None, visual=None)
    assert composite.min() == pytest.approx(-1.0)


def test_windows_cover_peaks_with_pre_and_post_roll() -> None:
    n = 600
    peaks = {100: 4.0, 300: 4.0, 500: 4.0}
    result = detect_candidates(
        n, SETTINGS, audio=audio_with_peaks(n, peaks), transcript=None, visual=None
    )
    assert len(result.candidates) == 3
    for peak in peaks:
        covering = [c for c in result.candidates if c.start <= peak + 0.5 <= c.end]
        assert covering, f"no window covers peak {peak}"
        c = covering[0]
        assert c.start <= peak + 0.5 - 3.0  # pre-roll >= 3 s
        assert c.end >= peak + 0.5 + 4.0  # post-roll >= 4 s
        assert SETTINGS.min_clip - 1e-6 <= c.duration <= SETTINGS.max_clip + 1e-6
    scores = [c.score for c in result.candidates]
    assert scores == sorted(scores, reverse=True)
    assert [c.rank for c in result.candidates] == [1, 2, 3]
    assert all(0 <= s <= 100 for s in scores)
    assert all(c.category in CATEGORIES for c in result.candidates)


def test_min_duration_enforced_on_narrow_spike() -> None:
    n = 300
    result = detect_candidates(
        n, SETTINGS, audio=audio_with_peaks(n, {150: 4.0}), transcript=None, visual=None
    )
    assert len(result.candidates) == 1
    assert result.candidates[0].duration == pytest.approx(SETTINGS.min_clip, abs=0.05)


def test_max_duration_enforced_on_long_plateau() -> None:
    n = 400
    z = np.zeros(n)
    z[100:250] = 4.0
    audio = audio_with_peaks(n, {}, energy_z=z)
    result = detect_candidates(n, SETTINGS, audio=audio, transcript=None, visual=None)
    assert result.candidates
    for c in result.candidates:
        assert SETTINGS.min_clip - 1e-6 <= c.duration <= SETTINGS.max_clip + 1e-6
    # near-duplicates were suppressed: no pair with IoU > 0.5
    for a in result.candidates:
        for b in result.candidates:
            if a is b:
                continue
            inter = max(0.0, min(a.end, b.end) - max(a.start, b.start))
            union = a.duration + b.duration - inter
            assert inter / union <= 0.5 + 1e-9


def test_window_bounds_clamped_to_recording() -> None:
    n = 100
    composite = np.zeros(n)
    composite[2] = 4.0
    start, end = expand_window(composite, 2, 1.5, SETTINGS, float(n))
    assert start == 0.0
    assert end - start >= SETTINGS.min_clip
    composite = np.zeros(n)
    composite[98] = 4.0
    start, end = expand_window(composite, 98, 1.5, SETTINGS, float(n))
    assert end == pytest.approx(float(n))
    assert end - start >= SETTINGS.min_clip


def test_pick_peaks_respects_min_distance() -> None:
    composite = np.zeros(200)
    composite[100] = 3.0
    composite[105] = 2.5
    composite[150] = 2.0
    assert pick_peaks(composite, 1.5, 15) == [100, 150]
    assert pick_peaks(composite, 2.2, 15) == [100, 105] or pick_peaks(composite, 2.2, 1) == [
        100,
        105,
    ]


def test_merge_overlapping_and_adjacent_windows() -> None:
    merged = merge_windows([Window(10, 30, 15, 2.0), Window(31, 50, 40, 3.0)], SETTINGS)
    assert len(merged) == 1
    assert (merged[0].start, merged[0].end) == (10, 50)
    assert merged[0].peak == 40 and merged[0].value == 3.0
    # union longer than the maximum: keep the stronger one
    small = FusionSettings(min_clip=15, max_clip=30)
    merged = merge_windows([Window(10, 30, 15, 2.0), Window(31, 50, 40, 3.0)], small)
    assert len(merged) == 1
    assert (merged[0].start, merged[0].end) == (31, 50)
    # far apart windows stay separate
    merged = merge_windows([Window(10, 30, 15, 2.0), Window(40, 60, 50, 3.0)], SETTINGS)
    assert len(merged) == 2


def test_merge_happens_end_to_end_when_expansions_overlap() -> None:
    n = 400
    z = np.zeros(n)
    z[100:103] = 4.0
    z[118:121] = 3.5
    z[103:118] = 1.0  # above threshold/2 so both expansions bridge the gap
    result = detect_candidates(
        n, SETTINGS, audio=audio_with_peaks(n, {}, energy_z=z), transcript=None, visual=None
    )
    assert len(result.candidates) == 1
    c = result.candidates[0]
    assert c.start <= 100.5 and c.end >= 121


def test_non_max_suppression() -> None:
    kept = non_max_suppression(
        [Window(10, 40, 20, 2.0), Window(15, 45, 25, 3.0), Window(100, 130, 110, 1.0)]
    )
    assert [w.value for w in kept] == [3.0, 1.0]


def test_score_is_bounded_and_monotonic() -> None:
    composite = np.concatenate([np.zeros(500), np.array([1.0, 2.0, 3.0, 4.0])])
    s1 = score_window(2.0, composite, 1.5)
    s2 = score_window(4.0, composite, 1.5)
    assert 0 <= s1 <= 100 and 0 <= s2 <= 100
    assert s2 > s1
    assert score_window(-5.0, composite, 1.5) >= 0


def test_sensitivity_changes_candidate_count() -> None:
    n = 600
    peaks = {60: 0.8, 160: 1.2, 260: 1.8, 360: 2.6, 460: 3.5}
    audio = audio_with_peaks(n, peaks)
    loose = detect_candidates(
        n, FusionSettings(sensitivity=100), audio=audio, transcript=None, visual=None
    )
    strict = detect_candidates(
        n, FusionSettings(sensitivity=0), audio=audio, transcript=None, visual=None
    )
    assert len(loose.candidates) == 5
    assert len(strict.candidates) == 2
    assert len(loose.candidates) > len(strict.candidates)


def test_snap_to_silence_and_transcript_boundaries() -> None:
    start, end = snap_window(
        94.3,
        109.3,
        100,
        start_anchors=np.array([93.0]),
        end_anchors=np.array([110.5]),
        settings=SETTINGS,
        duration=300.0,
    )
    assert start == pytest.approx(93.0)
    assert end == pytest.approx(110.5)
    # an anchor that would violate the minimum duration is ignored
    start, end = snap_window(
        94.3,
        109.3,
        100,
        start_anchors=np.array([]),
        end_anchors=np.array([108.0]),
        settings=SETTINGS,
        duration=300.0,
    )
    assert end == pytest.approx(109.3)
    # anchors too far away are ignored
    start, end = snap_window(
        94.3,
        109.3,
        100,
        start_anchors=np.array([80.0]),
        end_anchors=np.array([120.0]),
        settings=SETTINGS,
        duration=300.0,
    )
    assert (start, end) == (pytest.approx(94.3), pytest.approx(109.3))


def test_snapping_used_by_detect_candidates() -> None:
    n = 300
    audio = audio_with_peaks(n, {100: 4.0}, silences=[(110.5, 111.2)])
    transcript = transcript_with(n, boundaries=[93.0])
    result = detect_candidates(n, SETTINGS, audio=audio, transcript=transcript, visual=None)
    c = result.candidates[0]
    assert c.start == pytest.approx(93.0)
    assert c.end == pytest.approx(110.5)


@pytest.mark.parametrize(
    ("kind", "expected"),
    [
        ("scream", "reaction"),
        ("laughter", "funny"),
        ("funny_kw", "funny"),
        ("tension_kw", "tension"),
        ("gameplay", "gameplay"),
        ("quote", "quote"),
        ("plain", "highlight"),
        ("hype_kw", "hype"),
        ("reaction_kw", "reaction"),
    ],
)
def test_categories(kind: str, expected: str) -> None:
    n = 300
    peak = 150
    audio = audio_with_peaks(n, {peak: 4.0})
    transcript = None
    visual = None
    if kind == "scream":
        audio.scream_like[peak] = 0.9
    elif kind == "laughter":
        audio.laughter_like[peak] = 0.8
    elif kind.endswith("_kw"):
        category = kind.removesuffix("_kw")
        phrase = {
            "funny": "kkk",
            "tension": "cuidado",
            "hype": "insano",
            "reaction": "nao acredito",
        }[category]
        transcript = transcript_with(
            n, keywords=[TimedKeyword(peak + 0.2, peak + 0.8, phrase, category, 2.5)]
        )
    elif kind == "gameplay":
        transcript = transcript_with(
            n, keywords=[TimedKeyword(peak + 0.2, peak + 0.8, "clutch", "gameplay", 3.0)]
        )
        mz = np.zeros(n)
        mz[peak] = 3.0
        visual = visual_with(n, motion_z=mz)
    elif kind == "quote":
        audio = audio_with_peaks(n, {})  # no audio energy at all
        ks = np.zeros(n)
        ks[peak] = 8.0
        transcript = transcript_with(
            n,
            keywords=[TimedKeyword(peak + 0.2, peak + 0.8, "a verdade e", "quote", 2.0)],
            keyword_score=ks,
        )
    result = detect_candidates(n, SETTINGS, audio=audio, transcript=transcript, visual=visual)
    assert result.candidates, kind
    assert result.candidates[0].category == expected


def test_reasoning_is_pt_br_sentence() -> None:
    n = 300
    audio = audio_with_peaks(n, {150: 3.1})
    audio.scream_like[150] = 0.9
    audio.energy_db[149:152] = [-20.0, -12.0, -15.0]  # 18 dB above the typical -30 dBFS
    ks = np.zeros(n)
    ks[150] = 3.0
    transcript = transcript_with(
        n, keywords=[TimedKeyword(150.2, 150.8, "clutch", "gameplay", 3.0)], keyword_score=ks
    )
    result = detect_candidates(n, SETTINGS, audio=audio, transcript=transcript, visual=None)
    reasoning = result.candidates[0].reasoning
    assert reasoning.endswith(".")
    assert reasoning[0].isupper()
    # the level is stated from the audio itself, never the z-score written as a ratio
    assert reasoning.startswith("Volume 18 dB acima do nível típico da gravação")
    assert "×" not in reasoning and "3,1" not in reasoning
    assert "grito detectado" in reasoning
    assert "menção a 'clutch'" in reasoning
    assert result.candidates[0].contributors[0] == "audio.energy"
    signals = result.candidates[0].signals["audio"]
    assert signals["energyZ"] == pytest.approx(3.1)  # the z-score stays in the signals
    assert signals["levelAboveTypicalDb"] == pytest.approx(18.0)


@pytest.mark.parametrize(
    ("energy_z", "above_db", "expected"),
    [
        (24.2, 18.4, "volume 18 dB acima do nível típico da gravação"),
        (3.1, 3.0, "volume 3 dB acima do nível típico da gravação"),
        # loud for its own part of the recording, not above the recording's typical level
        (5.0, -2.0, "energia de áudio muito acima do normal naquele trecho"),
        (2.5, 1.0, "energia de áudio bem acima do normal naquele trecho"),
        (1.2, None, "energia de áudio acima do normal naquele trecho"),
        (0.4, 12.0, ""),  # below the contribution threshold: no fragment
    ],
)
def test_audio_energy_text(energy_z: float, above_db: float | None, expected: str) -> None:
    assert audio_energy_text(energy_z, above_db) == expected


def test_reasoning_uses_bands_when_the_level_is_typical_for_the_recording() -> None:
    n = 300
    audio = audio_with_peaks(n, {150: 4.5})  # energy_db is a flat -30 dBFS
    result = detect_candidates(n, SETTINGS, audio=audio, transcript=None, visual=None)
    cand = result.candidates[0]
    assert cand.reasoning.startswith("Energia de áudio muito acima do normal naquele trecho")
    assert cand.signals["audio"]["levelAboveTypicalDb"] == pytest.approx(0.0)
    silent = audio_with_peaks(n, {150: 4.5}, energy_db=np.full(n, -80.0))
    cand = detect_candidates(n, SETTINGS, audio=silent, transcript=None, visual=None).candidates[0]
    assert cand.signals["audio"]["levelAboveTypicalDb"] is None  # no non-silent second
    assert "dB" not in cand.reasoning


def test_gaps_renormalise_the_weights_per_second() -> None:
    """A modality is left out of the seconds it did not analyse (untranscribed chunk,
    frames missing after a decoder failure): the other modalities carry those seconds."""
    n = 200
    transcript = transcript_with(n)
    transcript.gaps = [(100.0, 200.0)]
    visual = visual_with(n)
    composite, weights, _, series = composite_parts(
        n, audio=audio_with_peaks(n, {50: 4.0, 150: 4.0}), transcript=transcript, visual=visual
    )
    assert weights == pytest.approx(BASE_WEIGHTS)  # nominal weights are unchanged
    assert series["transcript"][50] == pytest.approx(0.30)
    assert series["transcript"][150] == 0.0
    assert series["audio"][150] == pytest.approx(0.45 / 0.70)
    assert series["visual"][150] == pytest.approx(0.25 / 0.70)
    for t in (50, 150):
        assert sum(float(s[t]) for s in series.values()) == pytest.approx(1.0)
    # the same audio peak scores higher where the silent transcript is not counted as calm
    assert composite[150] > composite[50]


def test_candidate_signals_report_partial_coverage() -> None:
    n = 300
    transcript = transcript_with(n, keywords=[TimedKeyword(100.2, 100.8, "clutch", "gameplay", 3)])
    transcript.gaps = [(200.0, 300.0)]
    visual = visual_with(n)
    visual.gaps = [(240.0, 300.0)]
    result = detect_candidates(
        n,
        SETTINGS,
        audio=audio_with_peaks(n, {100: 4.0, 250: 4.0}),
        transcript=transcript,
        visual=visual,
    )
    by_peak = {round(c.peak): c for c in result.candidates}
    covered, uncovered = by_peak[100].signals, by_peak[250].signals
    assert covered["transcript"]["coverage"] == 1.0 and covered["visual"]["coverage"] == 1.0
    assert uncovered["transcript"]["coverage"] == 0.0
    assert 0.0 <= uncovered["visual"]["coverage"] < 0.5
    assert covered["weights"] == pytest.approx(BASE_WEIGHTS)
    assert uncovered["weights"]["transcript"] == 0.0 and uncovered["weights"]["visual"] == 0.0
    assert uncovered["weights"]["audio"] == pytest.approx(1.0)


def test_build_reasoning_fallback_and_join() -> None:
    assert build_reasoning([]) == "Momento acima do limiar de relevância da gravação."
    text = build_reasoning(
        [
            ("a", 1.0, "fala rápida"),
            ("b", 0.8, "corte de cena"),
            ("c", 0.5, "flash de luz"),
            ("d", 0.3, "ignored?"),
        ]
    )
    assert text == "Fala rápida, corte de cena e flash de luz."


def test_candidate_signals_follow_documented_shape() -> None:
    n = 300
    audio = audio_with_peaks(n, {150: 4.0})
    transcript = transcript_with(
        n, keywords=[TimedKeyword(150.2, 150.8, "clutch", "gameplay", 3.0)]
    )
    vz = np.zeros(n)
    vz[150] = 2.2
    visual = visual_with(n, motion_z=vz)
    result = detect_candidates(
        n, SETTINGS, audio=audio, transcript=transcript, visual=visual, llm=None
    )
    signals = result.candidates[0].signals
    assert signals["audio"]["available"] is True
    assert signals["audio"]["method"] == "heuristic-v1"
    assert {"energyZ", "peakDbfs", "burst", "screamLike", "laughterLike"} <= set(signals["audio"])
    assert signals["transcript"]["keywords"][0] == {
        "word": "clutch",
        "category": "gameplay",
        "weight": 3.0,
    }
    assert signals["visual"]["motionZ"] == pytest.approx(2.2)
    assert signals["llm"] == {"available": False}
    assert signals["weights"] == pytest.approx({"audio": 0.45, "transcript": 0.3, "visual": 0.25})
    assert signals["threshold"] == pytest.approx(1.5)
    assert signals["contributors"]
    cand = result.candidates[0].to_input()
    assert cand.to_api()["startSeconds"] == result.candidates[0].start
    assert 0 <= cand.score <= 100


def test_max_candidates_respected() -> None:
    n = 2000
    peaks = dict.fromkeys(range(50, 2000, 100), 4.0)
    result = detect_candidates(
        n,
        FusionSettings(max_candidates=5),
        audio=audio_with_peaks(n, peaks),
        transcript=None,
        visual=None,
    )
    assert len(result.candidates) == 5
    assert [c.rank for c in result.candidates] == [1, 2, 3, 4, 5]


def test_no_candidates_when_everything_is_flat() -> None:
    n = 200
    result = detect_candidates(
        n, SETTINGS, audio=audio_with_peaks(n, {}), transcript=None, visual=None
    )
    assert result.candidates == []


# --------------------------------------------------------------------------------------
# round 3: focus, custom keywords, speechless transcript, short sources
# --------------------------------------------------------------------------------------


def test_focus_adjustments_are_exactly_the_documented_multipliers() -> None:
    from app.analysis.fusion import Focus

    assert Focus.from_categories(["funny"]).adjustments == {
        "audio.laughter": 1.8,
        "lexicon.funny": 1.5,
    }
    hype = {"audio.burst": 1.5, "audio.scream": 1.5, "lexicon.hype": 1.5, "lexicon.reaction": 1.5}
    assert Focus.from_categories(["hype"]).adjustments == hype
    assert Focus.from_categories(["reaction"]).adjustments == hype
    assert Focus.from_categories(["quote"]).adjustments == {
        "lexicon.quote": 1.5,
        "weight.transcript": 1.5,
    }
    assert Focus.from_categories(["gameplay"]).adjustments == {
        "lexicon.gameplay": 1.5,
        "visual.motion": 1.5,
    }
    assert Focus.from_categories(["tension"]).adjustments == {
        "lexicon.tension": 1.5,
        "transcript.pause": 1.5,
    }
    assert Focus.from_categories(["highlight"]).adjustments == {}
    both = Focus.from_categories(["hype", "reaction", "funny", "nonsense"])
    assert both.categories == ("hype", "reaction", "funny")
    assert both.adjustments["audio.scream"] == 1.5  # never stacked
    assert both.lexicon_multipliers == {"funny": 1.5, "hype": 1.5, "reaction": 1.5}


def test_focus_multiplies_the_signal_terms() -> None:
    from app.analysis.fusion import Focus, audio_z, transcript_z, visual_z

    n = 50
    audio = audio_with_peaks(n, {})
    audio.laughter_like[10] = 1.0
    audio.scream_like[20] = 1.0
    audio.burst[30] = 1.0
    plain = audio_z(audio, n)
    funny = audio_z(audio, n, Focus.from_categories(["funny"]))
    assert funny[10] == pytest.approx(plain[10] * 1.8)  # 0.75 x 1.8 laughter
    assert funny[20] == pytest.approx(plain[20]) and funny[30] == pytest.approx(plain[30])
    hype = audio_z(audio, n, Focus.from_categories(["hype"]))
    assert hype[20] == pytest.approx(0.75 * 1.5) and hype[30] == pytest.approx(0.5 * 1.5)

    kw = [
        TimedKeyword(10.2, 10.5, "kkk", "funny", 2.5),
        TimedKeyword(20.2, 20.5, "cuidado", "tension", 2.0),
    ]
    pause = np.zeros(n)
    pause[30] = 1.0
    ks = np.zeros(n)
    ks[10], ks[11], ks[9] = 2.5, 1.25, 0.625
    ks[20], ks[21], ks[19] = 2.0, 1.0, 0.5
    series = transcript_with(n, keywords=kw, keyword_score=ks, pause_before=pause)
    base_t = transcript_z(series, n)
    tension = transcript_z(series, n, Focus.from_categories(["tension"]))
    assert tension[20] == pytest.approx(base_t[20] * 1.5)  # tension lexicon x1.5
    assert tension[10] == pytest.approx(base_t[10])  # funny lexicon untouched
    assert tension[30] == pytest.approx(base_t[30] * 1.5)  # pause_before x1.5

    mz = np.zeros(n)
    mz[5] = 2.0
    visual = visual_with(n, motion_z=mz)
    assert visual_z(visual, n, Focus.from_categories(["gameplay"]))[5] == pytest.approx(
        visual_z(visual, n)[5] * 1.5
    )
    _, weights, _ = compute_composite(
        n,
        audio=audio,
        transcript=series,
        visual=visual,
        focus=Focus.from_categories(["quote"]),
    )
    # transcript base weight x1.5, renormalised
    assert weights["transcript"] == pytest.approx(0.45 / (0.45 + 0.45 + 0.25))
    assert sum(weights.values()) == pytest.approx(1.0)


def test_focused_category_gets_rank_priority_but_keeps_its_score() -> None:
    from app.analysis.fusion import Focus

    n = 300
    audio = audio_with_peaks(n, {100: 3.4, 200: 3.0})
    kw = [TimedKeyword(200.2, 200.6, "kkk", "funny", 2.5)]
    transcript = transcript_with(n, keywords=kw)
    plain = detect_candidates(n, SETTINGS, audio=audio, transcript=transcript, visual=None)
    assert [round(c.peak) for c in plain.candidates] == [100, 200]
    focused_settings = FusionSettings(
        min_clip=15, max_clip=60, target_clip=30, focus=Focus.from_categories(["funny"])
    )
    focused = detect_candidates(
        n, focused_settings, audio=audio, transcript=transcript, visual=None
    )
    first, second = focused.candidates
    assert round(first.peak) == 200 and first.category == "funny" and first.rank == 1
    funny_plain = next(c for c in plain.candidates if round(c.peak) == 200)
    assert first.score == funny_plain.score  # the stored score is not the priority
    assert first.score < second.score
    assert first.priority == first.score + 10
    assert first.signals["focus"] == {
        "categories": ["funny"],
        "adjustments": {"audio.laughter": 1.8, "lexicon.funny": 1.5},
        "inFocus": True,
        "rankBonus": 10,
        "customKeywords": [],
        "neutralComposite": pytest.approx(funny_plain.composite, abs=1e-3),
    }
    assert first.composite > funny_plain.composite  # the focus did weigh the moment up
    assert second.signals["focus"]["inFocus"] is False
    assert second.signals["focus"]["rankBonus"] == 0
    assert plain.candidates[0].signals["focus"]["categories"] == []


def test_focus_pool_lets_a_focused_moment_in_beyond_max_candidates() -> None:
    from app.analysis.fusion import Focus

    n = 400
    # 150 is stronger than 250 even with the focus weights; 250 is funny (+10 priority)
    audio = audio_with_peaks(n, {50: 4.0, 150: 3.3, 250: 2.7})
    kw = [TimedKeyword(250.2, 250.6, "kkk", "funny", 2.5)]
    transcript = transcript_with(n, keywords=kw)
    top2 = FusionSettings(max_candidates=2)
    assert {
        round(c.peak)
        for c in detect_candidates(
            n, top2, audio=audio, transcript=transcript, visual=None
        ).candidates
    } == {50, 150}
    focused = FusionSettings(max_candidates=2, focus=Focus.from_categories(["funny"]))
    result = detect_candidates(n, focused, audio=audio, transcript=transcript, visual=None)
    assert [round(c.peak) for c in result.candidates] == [250, 50]
    funny, top = result.candidates
    assert funny.category == "funny" and funny.score < top.score < funny.priority


def test_custom_keywords_are_mentioned_in_the_reasoning() -> None:
    n = 300
    audio = audio_with_peaks(n, {150: 3.1})
    audio.scream_like[150] = 0.9
    audio.energy_db[149:152] = [-20.0, -12.0, -15.0]
    audio.laughter_like[150] = 0.9
    kw = [
        TimedKeyword(150.2, 150.5, "absurdo", "hype", 2.5),
        TimedKeyword(150.6, 150.9, "patrocinio", "custom", 3.0, custom=True),
    ]
    transcript = transcript_with(n, keywords=kw)  # keyword_score left at 0: weak contribution
    result = detect_candidates(n, SETTINGS, audio=audio, transcript=transcript, visual=None)
    cand = result.candidates[0]
    assert "menções a 'patrocinio' e 'absurdo'" in cand.reasoning
    assert cand.reasoning.count("menç") == 1
    assert cand.signals["transcript"]["keywords"][0] == {
        "word": "patrocinio",
        "category": "custom",
        "weight": 3.0,
        "custom": True,
    }
    assert cand.signals["focus"]["customKeywords"] == ["patrocinio"]
    assert cand.category == "reaction"  # "custom" never drives the category


def test_speechless_transcript_is_left_out_of_the_composite() -> None:
    n = 200
    audio = audio_with_peaks(n, {100: 3.0})
    silent = transcript_with(n)
    silent.speech_detected = False
    composite, weights, _ = compute_composite(n, audio=audio, transcript=silent, visual=None)
    assert weights == {"audio": pytest.approx(1.0)}
    assert composite[100] == pytest.approx(3.0)
    result = detect_candidates(n, SETTINGS, audio=audio, transcript=silent, visual=None)
    signals = result.candidates[0].signals["transcript"]
    assert signals["available"] is True and signals["speechDetected"] is False
    assert result.candidates[0].signals["weights"] == {"audio": pytest.approx(1.0)}


def test_short_source_becomes_one_whole_video_candidate() -> None:
    from app.analysis.fusion import WHOLE_VIDEO_REASON

    n = 13
    short = FusionSettings(min_clip=12.7, max_clip=12.7, target_clip=12.7, short_source=True)
    # flat audio (no peak above the threshold), but the recording is not silent
    result = detect_candidates(
        12.7, short, audio=audio_with_peaks(n, {}), transcript=None, visual=None
    )
    assert result.whole_video is True and len(result.candidates) == 1
    cand = result.candidates[0]
    assert (cand.start, cand.end) == (0.0, 12.7) and cand.rank == 1
    assert cand.reasoning.startswith(WHOLE_VIDEO_REASON)
    assert cand.signals["shortSource"] == {
        "durationSeconds": 12.7,
        "wholeVideo": True,
        "belowThreshold": True,
    }
    assert 0 <= cand.score < 60  # an honest (low) estimate: nothing stood out
    # with a real peak the window is the whole video too
    peaked = detect_candidates(
        12.7, short, audio=audio_with_peaks(n, {6: 4.0}), transcript=None, visual=None
    )
    assert len(peaked.candidates) == 1 and peaked.whole_video is False
    assert (peaked.candidates[0].start, peaked.candidates[0].end) == (0.0, 12.7)
    # nothing at all happens (digital silence, no transcript, no visual): no candidate
    silent = audio_with_peaks(n, {}, energy_db=np.full(n, -90.0))
    assert (
        detect_candidates(12.7, short, audio=silent, transcript=None, visual=None).candidates == []
    )
    # a regular (not short) source never gets the fallback
    flat = detect_candidates(
        300, SETTINGS, audio=audio_with_peaks(300, {}), transcript=None, visual=None
    )
    assert flat.candidates == [] and flat.whole_video is False
