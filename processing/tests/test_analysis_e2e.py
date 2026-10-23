"""End-to-end analysis on the synthetic fixture (transcription disabled)."""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import pytest

from app.analysis.pipeline import AnalysisOptions, AnalysisResult, Analyzer
from app.config import Settings
from app.media.ffmpeg import FfmpegTools
from app.media.probe import probe_file
from app.models import AnalysisSettings
from tests.fixtures.make_fixture import FixtureSpec


@pytest.fixture(scope="module")
def analysis(base_settings: Settings, tools: FfmpegTools, fixture_video: Path) -> AnalysisResult:
    settings = base_settings.model_copy(update={"whisper_enabled": False})
    analyzer = Analyzer(settings, tools)
    probe = probe_file(tools, fixture_video)
    events: list[tuple[str, int, str]] = []
    result = analyzer.analyze(
        fixture_video,
        probe,
        AnalysisSettings(
            min_clip_seconds=8, max_clip_seconds=20, sensitivity=50, max_candidates=10
        ),
        options=AnalysisOptions(transcription=False, webcam=True, llm=False),
        progress=lambda phase, pct, msg: events.append((phase, pct, msg)),
    )
    result.timings["_events"] = len(events)
    assert events[0][0] == "analyzing" and events[0][1] == 10
    assert events[-1][0] == "detecting" and events[-1][1] == 65
    pcts = [e[1] for e in events]
    assert pcts == sorted(pcts)
    assert all(e[2] for e in events)
    return result


def test_candidates_cover_bursts_and_are_ranked(
    analysis: AnalysisResult, fixture_spec: FixtureSpec
) -> None:
    cands = analysis.candidates
    assert len(cands) >= len(fixture_spec.bursts)
    for burst_time in fixture_spec.burst_times:
        assert any(c.start <= burst_time <= c.end for c in cands), (
            f"burst at {burst_time}s not covered"
        )
    scores = [c.score for c in cands]
    assert scores == sorted(scores, reverse=True)
    assert [c.rank for c in cands] == list(range(1, len(cands) + 1))
    assert all(0 <= s <= 100 for s in scores)
    for c in cands:
        assert 8 - 1e-6 <= c.duration <= 20 + 1e-6
        assert c.reasoning.endswith(".")
        assert "acima da média" in c.reasoning or "explosão sonora" in c.reasoning


def test_availability_flags_are_honest(analysis: AnalysisResult) -> None:
    availability = analysis.availability()
    assert availability["audio"]["available"] is True
    assert availability["audio"]["method"] == "heuristic-v1"
    assert availability["transcript"]["available"] is False
    assert "disabled" in availability["transcript"]["error"]
    assert availability["visual"]["available"] is True
    assert availability["llm"]["available"] is False
    assert analysis.fusion.weights == pytest.approx({"audio": 0.45 / 0.7, "visual": 0.25 / 0.7})
    cand = analysis.candidates[0]
    assert cand.signals["transcript"] == {"available": False}
    assert cand.transcript == ""


def test_audio_series_detect_bursts_silence_and_scream(
    analysis: AnalysisResult, fixture_spec: FixtureSpec
) -> None:
    audio = analysis.audio
    assert audio.seconds == 90
    for burst in fixture_spec.bursts:
        sec = int(burst.mid)
        assert audio.burst[sec] > 0, f"burst at {burst.mid}s not flagged"
        assert audio.energy_z[sec] > 2.0
    assert fixture_spec.silence is not None
    s, e = fixture_spec.silence
    assert any(abs(a - s) < 0.3 and abs(b - e) < 0.3 for a, b in audio.silences), audio.silences
    noisy = next(b for b in fixture_spec.bursts if b.noise > 0)
    assert audio.scream_like[int(noisy.mid)] > 0.3
    assert audio.laughter_like.max() == 0.0
    assert audio.energy_db.max() > -10 and audio.energy_db.min() < -40


def test_visual_series_detect_scene_cuts(
    analysis: AnalysisResult, fixture_spec: FixtureSpec
) -> None:
    visual = analysis.visual
    assert visual.frames_analyzed == pytest.approx(180, abs=3)
    cut_seconds = set(np.flatnonzero(visual.scene_cut).tolist())
    for cut in fixture_spec.cuts:
        assert cut_seconds & {int(cut) - 1, int(cut), int(cut) + 1}, (
            f"cut at {cut}s not detected: {cut_seconds}"
        )
    assert visual.motion_z[int(fixture_spec.cuts[0])] > 1.0


def test_no_webcam_in_synthetic_video(analysis: AnalysisResult) -> None:
    assert analysis.webcam_region is None


def test_artifacts_are_json_serialisable(analysis: AnalysisResult) -> None:
    signals = json.loads(json.dumps(analysis.signals_json()))
    assert signals["availability"]["audio"]["available"] is True
    assert len(signals["composite"]) == 90
    assert len(signals["audio"]["energyZ"]) == 90
    assert signals["visual"]["sceneCut"]
    assert signals["candidates"][0]["rank"] == 1
    transcript = json.loads(json.dumps(analysis.transcript_json()))
    assert transcript["available"] is False and transcript["segments"] == []


def test_higher_sensitivity_finds_at_least_as_many(
    base_settings: Settings, tools: FfmpegTools, fixture_video: Path, analysis: AnalysisResult
) -> None:
    settings = base_settings.model_copy(
        update={"whisper_enabled": False, "webcam_detection": False}
    )
    analyzer = Analyzer(settings, tools)
    probe = probe_file(tools, fixture_video)
    loose = analyzer.analyze(
        fixture_video,
        probe,
        AnalysisSettings(
            min_clip_seconds=8, max_clip_seconds=20, sensitivity=100, max_candidates=20
        ),
        options=AnalysisOptions(transcription=False, webcam=False, llm=False),
    )
    assert len(loose.candidates) >= len(analysis.candidates)
    assert loose.fusion.threshold < analysis.fusion.threshold
