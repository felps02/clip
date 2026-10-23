"""Analyzer options used by live windows: the media offset (the analysis skips the start of
its file, every time counts from there) and the fixed session language."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from app.analysis.fusion import PeakScope
from app.analysis.pipeline import AnalysisOptions, AnalysisResult, Analyzer, WindowOptions
from app.analysis.transcription import transcribe_media
from app.config import Settings
from app.media.audio import SAMPLE_RATE, decode_pcm, pcm_to_float
from app.media.ffmpeg import FfmpegTools
from app.media.probe import probe_file
from app.models import AnalysisSettings
from tests.fake_transcription import FakeSpeechProvider, FakeTranscriptionService
from tests.fixtures.make_fixture import FixtureSpec

OFFSET = 30.0


@pytest.fixture(scope="module")
def offset_analysis(base_settings: Settings, tools: FfmpegTools, fixture_video: Path) -> tuple:
    settings = base_settings.model_copy(update={"whisper_enabled": False})
    analyzer = Analyzer(settings, tools)
    probe = probe_file(tools, fixture_video)
    job = AnalysisSettings(min_clip_seconds=8, max_clip_seconds=20, sensitivity=50)
    plain = analyzer.analyze(
        fixture_video,
        probe,
        job,
        options=AnalysisOptions(transcription=False, webcam=False, llm=False),
    )
    shifted = analyzer.analyze(
        fixture_video,
        probe,
        job,
        options=AnalysisOptions(transcription=False, webcam=False, llm=False, media_offset=OFFSET),
    )
    return plain, shifted


def test_media_offset_shifts_every_signal_by_the_skipped_seconds(
    offset_analysis: tuple[AnalysisResult, AnalysisResult], fixture_spec: FixtureSpec
) -> None:
    plain, shifted = offset_analysis
    assert shifted.duration == pytest.approx(plain.duration - OFFSET, abs=0.05)
    assert shifted.audio.seconds == 60 and shifted.visual.frames_analyzed == pytest.approx(
        120, abs=3
    )
    for burst in fixture_spec.bursts:
        if burst.mid < OFFSET:
            continue
        local = int(burst.mid - OFFSET)
        assert shifted.audio.burst[local] > 0, f"burst at {burst.mid}s not at {local}s"
        assert shifted.audio.energy_z[local] > 2.0
        assert any(c.start <= burst.mid - OFFSET <= c.end for c in shifted.candidates)
    # the silence of the fixture (60.5-62.5 s) and its scene cut at 60 s, 30 s earlier
    assert fixture_spec.silence is not None
    s, e = fixture_spec.silence
    assert any(
        abs(a - (s - OFFSET)) < 0.3 and abs(b - (e - OFFSET)) < 0.3
        for a, b in shifted.audio.silences
    ), shifted.audio.silences
    cut = int(fixture_spec.cuts[1] - OFFSET)
    assert set(np.flatnonzero(shifted.visual.scene_cut).tolist()) & {cut - 1, cut, cut + 1}
    # the per-second levels of the overlap are those of the whole-file analysis, shifted by
    # 30 s: equal everywhere but on a burst's first second, where decoding from a seek point
    # instead of the file start moves the 100 ms frame grid by a few milliseconds of AAC
    # priming (a few dB on the frame the onset falls in)
    same = np.abs(shifted.audio.energy_db[:55] - plain.audio.energy_db[30:85]) < 0.6
    assert same.sum() >= 52, np.flatnonzero(~same)
    moved = np.abs(shifted.visual.motion[:55] - plain.visual.motion[30:85]) < 0.5
    assert moved.sum() >= 52, np.flatnonzero(~moved)


def test_window_options_restrict_the_reported_peaks_and_join_the_score_population(
    base_settings: Settings, tools: FfmpegTools, fixture_video: Path
) -> None:
    settings = base_settings.model_copy(update={"whisper_enabled": False})
    analyzer = Analyzer(settings, tools)
    probe = probe_file(tools, fixture_video)
    job = AnalysisSettings(min_clip_seconds=8, max_clip_seconds=20, sensitivity=50)
    options = AnalysisOptions(transcription=False, webcam=False, llm=False)
    every = analyzer.analyze(fixture_video, probe, job, options=options)
    assert len(every.candidates) >= 3
    scoped = analyzer.analyze(
        fixture_video,
        probe,
        job,
        options=AnalysisOptions(
            transcription=False,
            webcam=False,
            llm=False,
            window=WindowOptions(
                scope=PeakScope(30.0, 60.0, left_edge=False, right_edge=False),
                score_reference=np.full(200, 10.0),
            ),
        ),
    )
    assert scoped.candidates and all(30.0 <= c.peak < 60.0 for c in scoped.candidates)
    assert [c.peak for c in scoped.candidates] == [
        c.peak for c in every.candidates if 30.0 <= c.peak < 60.0
    ]
    # a session of stronger moments lowers the percentile half of the score
    by_peak = {c.peak: c.score for c in every.candidates}
    assert all(c.score < by_peak[c.peak] for c in scoped.candidates)
    assert [c.rank for c in scoped.candidates] == list(range(1, len(scoped.candidates) + 1))
    assert scoped.fusion.neutral is not None and scoped.fusion.neutral.size == 90


def test_a_window_is_not_the_whole_recording_unless_it_is(
    base_settings: Settings, tools: FfmpegTools, short_video: Path
) -> None:
    """A 20 s window with minClipSeconds 30: the short-source rules (whole video as the
    candidate) only apply when the window is the whole session."""
    settings = base_settings.model_copy(update={"whisper_enabled": False})
    analyzer = Analyzer(settings, tools)
    probe = probe_file(tools, short_video)
    job = AnalysisSettings(min_clip_seconds=30, max_clip_seconds=60, sensitivity=0)
    base = {"transcription": False, "webcam": False, "llm": False}
    whole = analyzer.analyze(
        short_video,
        probe,
        job,
        options=AnalysisOptions(**base, window=WindowOptions(whole_recording=True)),
    )
    assert whole.effective["shortSource"] is True and whole.notices
    part = analyzer.analyze(
        short_video,
        probe,
        job,
        options=AnalysisOptions(
            **base,
            window=WindowOptions(scope=PeakScope(left_edge=False, right_edge=False)),
        ),
    )
    assert part.effective["shortSource"] is False and not part.notices
    assert all(not c.signals.get("shortSource") for c in part.candidates)


def test_the_session_language_skips_the_detection(
    base_settings: Settings, tools: FfmpegTools, short_video: Path
) -> None:
    provider = FakeSpeechProvider(language="es")
    analyzer = Analyzer(base_settings, tools, transcription=FakeTranscriptionService(provider))  # type: ignore[arg-type]
    probe = probe_file(tools, short_video)
    auto = AnalysisSettings()
    session = analyzer.analyze(
        short_video,
        probe,
        auto,
        options=AnalysisOptions(webcam=False, llm=False, language="pt", language_source="session"),
    )
    assert provider.detect_calls == 0 and provider.transcribed == ["pt"]
    assert session.transcript.language == "pt"
    assert session.transcript.language_source == "session"
    assert session.availability()["transcript"]["languageSource"] == "session"

    detected = analyzer.analyze(
        short_video, probe, auto, options=AnalysisOptions(webcam=False, llm=False)
    )
    assert provider.detect_calls == 1 and provider.transcribed[-1] == "es"
    assert detected.transcript.language_source == "detected"

    explicit = analyzer.analyze(
        short_video,
        probe,
        AnalysisSettings(language="en"),
        options=AnalysisOptions(webcam=False, llm=False),
    )
    assert provider.detect_calls == 1 and explicit.transcript.language_source == "settings"


def test_transcription_decodes_from_the_media_offset(
    tools: FfmpegTools, fixture_video: Path
) -> None:
    provider = FakeSpeechProvider()
    transcript = transcribe_media(
        tools,
        fixture_video,
        60.0,
        provider,
        language=None,
        chunk_seconds=30.0,
        media_offset=OFFSET,
    )
    # the chunks the VAD saw are the file's 30-60 s and 60-90 s
    expected = pcm_to_float(
        decode_pcm(tools, fixture_video, sample_rate=SAMPLE_RATE, start=OFFSET, duration=30.0)
    )
    assert len(provider.vad_audio) == 2
    first = provider.vad_audio[0]
    assert abs(len(first) - len(expected)) <= SAMPLE_RATE // 100
    n = min(len(first), len(expected))
    np.testing.assert_allclose(first[:n], expected[:n], atol=1e-4)
    # every time of the transcript counts from the offset
    assert [round(s.start, 3) for s in transcript.segments] == [1.0, 31.0]
    assert transcript.duration == 60.0 and transcript.speech_detected is True
