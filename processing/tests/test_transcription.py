from __future__ import annotations

import os
import subprocess
import sys
import types
from pathlib import Path

import numpy as np
import pytest

import app.analysis.transcription as transcription_module
from app.analysis.lexicon import normalize_text
from app.analysis.transcription import (
    LOAD_RETRY_MAX_SECONDS,
    FasterWhisperProvider,
    Segment,
    Transcript,
    TranscriptionService,
    UnavailableProvider,
    Word,
    bound_model,
    chunk_boundaries,
    transcribe_media,
    transcript_series,
)
from app.config import Settings
from app.media.ffmpeg import FfmpegTimeout, FfmpegTools
from app.media.probe import probe_file
from tests.fixtures.make_tts_fixture import KEYWORDS


def test_bound_model() -> None:
    assert bound_model("small", "base") == "base"
    assert bound_model("tiny", "base") == "tiny"
    assert bound_model(None, "base") == "base"
    assert bound_model("weird", "small") == "small"
    assert bound_model("medium", "large-v3") == "medium"


def test_chunk_boundaries_snap_to_silences() -> None:
    chunks = chunk_boundaries(1000, 300, [(290.0, 291.0), (610.0, 611.5), (899.0, 899.6)])
    assert chunks[0] == (0.0, 290.5)
    assert chunks[1][0] == 290.5 and chunks[1][1] == pytest.approx(610.75)
    assert chunks[-1][1] == 1000
    assert chunk_boundaries(100, 300, None) == [(0.0, 100.0)]
    assert chunk_boundaries(0, 300, None) == []
    plain = chunk_boundaries(1000, 300, None)
    assert [c[0] for c in plain] == [0.0, 300.0, 600.0]
    assert plain[-1][1] == 1000


def test_unavailable_provider_degrades_honestly(tools: FfmpegTools, short_video: Path) -> None:
    provider = UnavailableProvider("transcription disabled")
    transcript = transcribe_media(tools, short_video, 20.0, provider, language=None)
    assert transcript.available is False
    assert transcript.error == "transcription disabled"
    series = transcript_series(transcript, 20.0)
    assert series.available is False and series.seconds == 20


def test_missing_model_marks_provider_unavailable(tmp_path: Path) -> None:
    provider = FasterWhisperProvider(
        "not-a-real-model", download_root=tmp_path, local_files_only=True
    )
    assert provider.available() is False
    assert provider.error
    result = provider.transcribe(np.zeros(16000, dtype=np.float32))
    assert result.available is False and result.model == "faster-whisper/not-a-real-model"


def test_transcript_json_round_trip() -> None:
    transcript = Transcript(
        available=True,
        model="faster-whisper/base",
        language="pt",
        segments=[
            Segment(
                1.0, 2.0, "olá mundo", [Word(1.0, 1.4, " olá", 0.9), Word(1.5, 2.0, " mundo", 0.8)]
            )
        ],
        duration=5.0,
    )
    data = transcript.to_json()
    back = Transcript.from_json(data)
    assert back.available and back.language == "pt"
    assert [w.word for w in back.words] == [" olá", " mundo"]
    assert back.text_between(1.2, 1.3) == "olá"
    assert back.boundaries() == [1.0, 2.0]


def test_transcript_series_keywords_exclamations_and_pause() -> None:
    words = [Word(10.0, 10.3, " que"), Word(10.4, 10.8, " clutch"), Word(11.0, 11.5, " absurdo!")]
    transcript = Transcript(
        available=True,
        model="m",
        language="pt",
        segments=[Segment(10.0, 11.5, "que clutch absurdo!", words)],
        duration=20.0,
    )
    burst = np.zeros(20)
    burst[10] = 1.0
    series = transcript_series(transcript, 20.0, audio_burst=burst)
    assert series.available
    assert series.keyword_score[10] > 0
    assert {k.phrase for k in series.keywords} == {"clutch", "absurdo"}
    assert series.exclamation[10] == 1
    assert series.pause_before[10] == 1.0  # silence before, then speech + burst
    assert series.text_between(10.0, 11.0) == "que clutch"
    assert series.keywords_between(10.0, 10.5)[0].phrase == "clutch"


@pytest.mark.slow
def test_faster_whisper_on_tts_wav(
    tts_wav: tuple[Path, str] | None, base_settings: Settings, tools: FfmpegTools
) -> None:
    if tts_wav is None:
        pytest.skip("no Windows SAPI voice available to synthesise the spoken fixture")
    wav, voice = tts_wav
    service = TranscriptionService(base_settings)
    provider = service.provider_for("base")
    if not provider.available():
        pytest.skip(
            f"faster-whisper model not available offline: {getattr(provider, 'error', None)}"
        )
    probe = probe_file(tools, wav)
    assert probe.has_audio and not probe.has_video
    transcript = transcribe_media(tools, wav, probe.duration or 0.0, provider, language=None)
    assert transcript.available, transcript.error
    assert transcript.model == "faster-whisper/base"
    assert transcript.language == "pt"
    assert transcript.words, "no word timestamps"
    starts = [w.start for w in transcript.words]
    assert starts == sorted(starts)
    assert all(0.0 <= w.start <= w.end <= (probe.duration or 0) + 1.0 for w in transcript.words)
    normalised = normalize_text(transcript.text)
    found = [kw for kw in KEYWORDS if kw in normalised]
    assert len(found) >= 3, f"voice={voice} text={transcript.text!r} found={found}"
    series = transcript_series(transcript, probe.duration or 0.0)
    assert series.keyword_score.max() > 0
    assert series.keywords
    assert service.status()["loaded"] is True


class _FlakyWhisperModel:
    """Stands in for ``faster_whisper.WhisperModel``: fails the first ``failures`` loads."""

    attempts = 0
    failures = 0

    def __init__(self, *args: object, **kwargs: object) -> None:
        type(self).attempts += 1
        if type(self).attempts <= type(self).failures:
            raise OSError("temporary failure in name resolution (huggingface.co)")


def _fake_whisper(monkeypatch: pytest.MonkeyPatch, failures: int) -> type[_FlakyWhisperModel]:
    model = type("FakeWhisperModel", (_FlakyWhisperModel,), {"attempts": 0, "failures": failures})
    monkeypatch.setitem(sys.modules, "faster_whisper", types.SimpleNamespace(WhisperModel=model))
    return model


def test_failed_model_load_is_retried_after_a_backoff(monkeypatch: pytest.MonkeyPatch) -> None:
    """A transient failure (network, disk) while loading the model is not cached for the
    life of the process: jobs within the backoff skip the load, the next one retries."""
    model = _fake_whisper(monkeypatch, failures=2)
    now = [1000.0]
    provider = FasterWhisperProvider("base", clock=lambda: now[0])

    assert provider.load() is False
    assert provider.error is not None and provider.error.startswith("OSError")
    assert provider.retry_in == pytest.approx(60.0) and provider.failures == 1
    now[0] += 30
    assert provider.load() is False and model.attempts == 1  # within the backoff: no attempt
    assert provider.transcribe(np.zeros(16000, dtype=np.float32)).available is False

    now[0] += 31
    assert provider.load() is False and model.attempts == 2  # retried, failed again
    assert provider.retry_in == pytest.approx(120.0)  # the backoff doubles

    now[0] += 121
    assert provider.load() is True and model.attempts == 3  # healed by itself
    assert provider.error is None and provider.retry_in is None and provider.failures == 0
    assert provider.loaded


def test_model_load_backoff_is_capped(monkeypatch: pytest.MonkeyPatch) -> None:
    _fake_whisper(monkeypatch, failures=99)
    now = [0.0]
    provider = FasterWhisperProvider("base", clock=lambda: now[0])
    for _ in range(10):
        provider.load()
        now[0] += 10_000
    assert provider.failures == 10
    assert provider.load() is False
    assert provider.retry_in == pytest.approx(LOAD_RETRY_MAX_SECONDS)


def test_service_status_reports_the_pending_retry(
    base_settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    _fake_whisper(monkeypatch, failures=99)
    service = TranscriptionService(base_settings)
    assert service.provider_for("base").available() is False
    status = service.status()
    assert status["loaded"] is False and "OSError" in status["errors"]["base"]
    assert 0 < status["retryInSeconds"]["base"] <= 60


class _ChunkProvider:
    """Transcribes every chunk except the ones starting at the ``fail_at`` offsets."""

    name = "fake"

    def __init__(self, fail_at: set[float]) -> None:
        self.fail_at = fail_at

    def available(self) -> bool:
        return True

    def transcribe(
        self,
        audio: np.ndarray,
        *,
        language: str | None = None,
        offset: float = 0.0,
        cancel: object = None,
    ) -> Transcript:
        if round(offset, 1) in self.fail_at:
            return Transcript.unavailable("RuntimeError: chunk exploded", "fake/model")
        word = Word(offset + 1.0, offset + 1.5, " oi", 0.9)
        return Transcript(
            available=True,
            model="fake/model",
            language="pt",
            segments=[Segment(offset + 1.0, offset + 1.5, "oi", [word])],
            duration=len(audio) / 16000,
        )


def test_failed_chunks_become_gaps_instead_of_a_silently_partial_transcript(
    tools: FfmpegTools, short_video: Path
) -> None:
    transcript = transcribe_media(
        tools, short_video, 20.0, _ChunkProvider({10.0}), language=None, chunk_seconds=5.0
    )
    assert transcript.available is True
    assert transcript.gaps == [(10.0, 15.0)]
    assert transcript.coverage == pytest.approx(0.75)
    assert transcript.error is not None and "1 of 4 chunks" in transcript.error
    assert "RuntimeError" in transcript.error
    data = transcript.to_json()
    assert data["coverage"] == 0.75 and data["gaps"] == [[10.0, 15.0]]
    assert Transcript.from_json(data).gaps == [(10.0, 15.0)]
    series = transcript_series(transcript, 20.0)
    assert series.coverage == pytest.approx(0.75) and series.gaps == [(10.0, 15.0)]
    assert series.to_json()["coverage"] == 0.75
    # an untranscribed chunk is not a silence: no "pause then reaction" right after it
    assert series.pause_before[15:18].sum() == 0


def test_a_chunk_that_cannot_be_decoded_is_a_gap(
    tools: FfmpegTools, short_video: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    real = transcription_module.decode_pcm

    def flaky_decode(*args: object, **kwargs: object) -> np.ndarray:
        if kwargs.get("start") == 15.0:
            raise FfmpegTimeout("ffmpeg pipe timed out after 120s")
        return real(*args, **kwargs)  # type: ignore[arg-type]

    monkeypatch.setattr(transcription_module, "decode_pcm", flaky_decode)
    transcript = transcribe_media(
        tools, short_video, 20.0, _ChunkProvider(set()), language=None, chunk_seconds=5.0
    )
    assert transcript.available and transcript.gaps == [(15.0, 20.0)]
    assert "FfmpegTimeout" in (transcript.error or "")


def test_a_failing_first_chunk_still_makes_the_transcript_unavailable(
    tools: FfmpegTools, short_video: Path
) -> None:
    transcript = transcribe_media(
        tools, short_video, 20.0, _ChunkProvider({0.0}), language=None, chunk_seconds=5.0
    )
    assert transcript.available is False and transcript.coverage == 0.0


def test_complete_transcript_has_full_coverage() -> None:
    transcript = Transcript(available=True, duration=30.0)
    assert transcript.coverage == 1.0 and transcript.to_json()["gaps"] == []


HF_CHECK = (
    "import os, app.analysis.transcription, faster_whisper, huggingface_hub.constants as c\n"
    "print(os.environ['HF_HUB_DISABLE_SYMLINKS_WARNING'], c.HF_HUB_DISABLE_SYMLINKS_WARNING)\n"
)


@pytest.mark.parametrize(("preset", "expected"), [(None, ["1", "True"]), ("0", ["0", "False"])])
def test_hf_hub_symlink_warning_is_disabled_before_faster_whisper_is_imported(
    preset: str | None, expected: list[str]
) -> None:
    """huggingface_hub reads the flag when it is first imported (faster_whisper imports it):
    importing the transcription module must have set it already, without overriding an
    explicit value."""
    env = {k: v for k, v in os.environ.items() if k != "HF_HUB_DISABLE_SYMLINKS_WARNING"}
    if preset is not None:
        env["HF_HUB_DISABLE_SYMLINKS_WARNING"] = preset
    proc = subprocess.run(
        [sys.executable, "-c", HF_CHECK],
        cwd=Path(__file__).resolve().parents[1],
        env=env,
        capture_output=True,
        text=True,
        timeout=180,
        check=False,
    )
    assert proc.returncode == 0, proc.stderr[-1000:]
    assert proc.stdout.split()[-2:] == expected


# --------------------------------------------------------------------------------------
# round 3: speech scan, once-per-job language detection, no speech
# --------------------------------------------------------------------------------------


class _VadProvider:
    """Provider with a voice activity detector and a language detector: ``speech`` holds
    the regions (relative seconds) the VAD reports for each scanned chunk, in order."""

    name = "fake-vad"
    model_name = "fake/vad"

    def __init__(
        self,
        speech: list[list[tuple[float, float]]],
        detection: tuple[str, float, list[tuple[str, float]]] | None = (
            "en",
            0.93,
            [("en", 0.93), ("pt", 0.04)],
        ),
        words: bool = True,
    ) -> None:
        self.speech = list(speech)
        self.detection = detection
        self.words = words
        self.detect_calls: list[tuple[float, int]] = []  # (seconds of audio, segments)
        self.transcribed: list[tuple[float, str | None]] = []  # (offset, language)

    def available(self) -> bool:
        return True

    def speech_timestamps(self, audio: np.ndarray) -> list[tuple[float, float]]:
        return self.speech.pop(0)

    def detect_language(
        self, audio: np.ndarray, *, segments: int, threshold: float = 0.5
    ) -> tuple[str, float, list[tuple[str, float]]] | None:
        self.detect_calls.append((len(audio) / 16000, segments))
        return self.detection

    def transcribe(
        self,
        audio: np.ndarray,
        *,
        language: str | None = None,
        offset: float = 0.0,
        cancel: object = None,
    ) -> Transcript:
        self.transcribed.append((offset, language))
        segments = []
        if self.words:
            word = Word(offset + 1.0, offset + 1.4, " clutch", 0.9)
            segments = [Segment(offset + 1.0, offset + 1.4, "clutch", [word])]
        return Transcript(
            available=True,
            model="fake/vad",
            language=language or "xx",
            language_probability=1.0,
            segments=segments,
            duration=len(audio) / 16000,
        )


def test_no_speech_skips_detection_and_transcription(tools: FfmpegTools, short_video: Path) -> None:
    provider = _VadProvider([[], [], [], []])
    transcript = transcribe_media(
        tools, short_video, 20.0, provider, language=None, chunk_seconds=5.0
    )
    assert transcript.available is True and transcript.speech_detected is False
    assert transcript.language is None and transcript.language_probability is None
    assert transcript.segments == [] and transcript.speech_seconds == 0.0
    assert provider.detect_calls == [] and provider.transcribed == []
    data = transcript.to_json()
    assert data["speechDetected"] is False and data["language"] is None
    series = transcript_series(transcript, 20.0)
    assert series.available and series.speech_detected is False and not series.has_speech
    assert series.to_json()["speechDetected"] is False


def test_language_is_detected_once_and_fixed_for_every_chunk(
    tools: FfmpegTools, short_video: Path
) -> None:
    provider = _VadProvider([[], [(1.0, 4.0)], [], [(0.5, 3.0)]])
    phases: list[tuple[str, float]] = []
    transcript = transcribe_media(
        tools,
        short_video,
        20.0,
        provider,
        language=None,
        chunk_seconds=5.0,
        on_phase=lambda name, frac: phases.append((name, frac)),
    )
    assert len(provider.detect_calls) == 1  # once per job, not per chunk
    assert provider.detect_calls[0][1] == 1  # 5.5 s of speech: one 30 s window
    # chunks without speech are skipped (not gaps); the others use the detected language
    assert provider.transcribed == [(5.0, "en"), (15.0, "en")]
    assert transcript.gaps == [] and transcript.coverage == 1.0
    assert transcript.language == "en" and transcript.language_source == "detected"
    assert transcript.language_probability == pytest.approx(0.93)
    assert transcript.language_confidence == "high" and transcript.speech_detected is True
    assert transcript.speech_seconds == pytest.approx(5.5)
    detection = transcript.language_detection
    assert detection is not None and detection["spans"] == [[6.0, 18.0]]
    assert detection["candidates"][0] == ["en", 0.93] and detection["ignored"] is None
    assert ("speech", 1.0) in phases and ("language", 1.0) in phases
    back = Transcript.from_json(transcript.to_json())
    assert back.language_confidence == "high" and back.speech_detected is True


def test_low_confidence_and_ignored_languages(tools: FfmpegTools, short_video: Path) -> None:
    provider = _VadProvider(
        [[(0.0, 5.0)]] * 4, detection=("la", 0.75, [("la", 0.75), ("pt", 0.13), ("en", 0.1)])
    )
    transcript = transcribe_media(
        tools, short_video, 20.0, provider, language=None, chunk_seconds=5.0
    )
    # Latin is never a stream language: the best other candidate, with its own probability
    assert transcript.language == "pt" and transcript.language_probability == pytest.approx(0.13)
    assert transcript.language_confidence == "low"
    assert transcript.language_detection is not None
    assert transcript.language_detection["ignored"] == {"language": "la", "probability": 0.75}
    assert {lang for _, lang in provider.transcribed} == {"pt"}
    series = transcript_series(transcript, 20.0)
    assert series.language_confidence == "low"


def test_language_from_the_settings_is_not_detected(tools: FfmpegTools, short_video: Path) -> None:
    provider = _VadProvider([[(0.0, 2.0)], [], [], []])
    transcript = transcribe_media(
        tools, short_video, 20.0, provider, language="pt", chunk_seconds=5.0
    )
    assert provider.detect_calls == [] and provider.transcribed == [(0.0, "pt")]
    assert transcript.language == "pt" and transcript.language_source == "settings"
    assert transcript.language_probability is None and transcript.language_confidence is None


def test_speech_without_words_reports_no_speech_and_no_language(
    tools: FfmpegTools, short_video: Path
) -> None:
    provider = _VadProvider([[(0.0, 2.0)], [], [], []], words=False)
    transcript = transcribe_media(
        tools, short_video, 20.0, provider, language=None, chunk_seconds=5.0
    )
    assert transcript.available and transcript.speech_detected is False
    assert transcript.language is None and transcript.speech_seconds == pytest.approx(2.0)


def test_detection_spans_spread_over_the_whole_recording() -> None:
    from app.analysis.transcription import detection_spans

    # 60 s of speech every 5 minutes over an hour: 720 s of speech
    regions = [(float(t), t + 60.0) for t in range(0, 3600, 300)]
    spans = detection_spans(regions)
    assert len(spans) == 6
    starts = sorted(s for s, _ in spans)
    assert starts[0] < 300 and starts[-1] > 3000  # not only the beginning
    for start, end in spans:
        speech = sum(max(0.0, min(e, end) - max(s, start)) for s, e in regions)
        # ~30 s of speech; a window reaching across a long pause stops at 90 s of audio
        assert 20.0 <= speech <= 30.01 and end - start <= 90.0
    # 20 s every 5 minutes: a window would span 310 s, so it stops at 90 s (20 s of speech)
    for start, end in detection_spans([(float(t), t + 20.0) for t in range(0, 3600, 300)]):
        assert end - start <= 90.0
    # little speech: one window with all of it
    assert detection_spans([(10.0, 15.0), (60.0, 70.0)]) == [(10.0, 70.0)]
    # sparse speech: a window never decodes more than 90 s
    sparse = detection_spans([(float(t), t + 1.0) for t in range(0, 600, 10)], count=1)
    assert len(sparse) == 1 and sparse[0][1] - sparse[0][0] <= 90.0
    # the densest window first (faster-whisper stops at the first clear 30 s segment)
    regions = [(0.0, 30.0), *[(float(t), t + 1.0) for t in range(100, 400, 5)]]
    assert detection_spans(regions, count=2)[0] == (0.0, 30.0)
    assert detection_spans([]) == []


@pytest.mark.slow
def test_english_speech_in_auto_mode_is_transcribed_as_english(
    tts_en_wav: tuple[Path, str] | None, base_settings: Settings, tools: FfmpegTools
) -> None:
    """Round-3 P1: English speech, language "auto", the free plan model (base)."""
    if tts_en_wav is None:
        pytest.skip("no en-US SAPI voice installed")
    from tests.fixtures.make_tts_fixture import EN_KEYWORDS

    wav, voice = tts_en_wav
    provider = TranscriptionService(base_settings).provider_for("base")
    if not provider.available():
        pytest.skip(f"faster-whisper base not available offline: {getattr(provider, 'error', '')}")
    probe = probe_file(tools, wav)
    transcript = transcribe_media(tools, wav, probe.duration or 0.0, provider, language=None)
    assert transcript.language == "en", (voice, transcript.language_detection)
    assert transcript.language_confidence == "high" and transcript.speech_detected is True
    normalised = normalize_text(transcript.text)
    found = [kw for kw in EN_KEYWORDS if kw in normalised]
    assert len(found) >= 5, f"voice={voice} text={transcript.text!r} found={found}"


@pytest.mark.slow
def test_real_vad_finds_no_speech_in_the_synthetic_video(
    base_settings: Settings, tools: FfmpegTools, short_video: Path
) -> None:
    """Tones and noise only: no language is made up (round-3 P3)."""
    provider = TranscriptionService(base_settings).provider_for("base")
    if not provider.available():
        pytest.skip("faster-whisper base not available offline")
    transcript = transcribe_media(tools, short_video, 20.0, provider, language=None)
    assert transcript.available and transcript.speech_detected is False
    assert transcript.language is None and transcript.segments == []
