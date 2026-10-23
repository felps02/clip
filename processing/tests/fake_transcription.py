"""A transcription service whose provider has a voice activity detector and a language
detector, and records what it is asked (live tests: session language, media offset)."""

from __future__ import annotations

from typing import Any

import numpy as np

from app.analysis.transcription import Segment, Transcript, Word
from app.media.audio import SAMPLE_RATE


class FakeSpeechProvider:
    """Transcription provider with a VAD and a language detector, recording what it is
    asked: ``speech`` turns the VAD's answer on or off."""

    name = "fake-speech"
    model_name = "fake/speech"

    def __init__(self, *, speech: bool = True, language: str = "pt") -> None:
        self.speech = speech
        self.language = language
        self.detect_calls = 0
        self.transcribed: list[str | None] = []
        self.vad_audio: list[np.ndarray] = []

    def available(self) -> bool:
        return True

    def speech_timestamps(self, audio: np.ndarray) -> list[tuple[float, float]]:
        self.vad_audio.append(np.asarray(audio))
        if not self.speech:
            return []
        return [(0.5, min(len(audio) / SAMPLE_RATE, 3.0))]

    def detect_language(
        self, audio: np.ndarray, *, segments: int, threshold: float = 0.5
    ) -> tuple[str, float, list[tuple[str, float]]]:
        self.detect_calls += 1
        return self.language, 0.9, [(self.language, 0.9), ("es", 0.05)]

    def transcribe(
        self,
        audio: np.ndarray,
        *,
        language: str | None = None,
        offset: float = 0.0,
        cancel: Any = None,
    ) -> Transcript:
        self.transcribed.append(language)
        words = [
            Word(offset + 1.0, offset + 1.3, " texto", 0.9),
            Word(offset + 1.3, offset + 1.6, " neutro", 0.9),
        ]
        return Transcript(
            available=True,
            model=self.model_name,
            language=language or self.language,
            language_probability=0.9,
            segments=[Segment(offset + 1.0, offset + 1.6, "texto neutro", words)],
            duration=len(audio) / SAMPLE_RATE,
        )


class FakeTranscriptionService:
    enabled = True

    def __init__(self, provider: FakeSpeechProvider) -> None:
        self.provider = provider

    def provider_for(self, requested_model: str | None) -> FakeSpeechProvider:
        return self.provider

    def status(self) -> dict[str, Any]:
        return {"enabled": True}
