from __future__ import annotations

import numpy as np
import pytest

from app.analysis.audio_features import (
    AudioFeatureExtractor,
    audio_features_from_pcm,
    summarize_audio,
    typical_level_db,
)

SR = 16000


def synth(duration: float = 30.0) -> np.ndarray:
    t = np.arange(int(duration * SR)) / SR
    x = 0.03 * np.sin(2 * np.pi * 220 * t)  # about -30 dBFS
    x[(t >= 15.0) & (t < 16.0)] += 0.8 * np.sin(2 * np.pi * 900 * t[(t >= 15.0) & (t < 16.0)])
    rng = np.random.default_rng(1)
    noise_mask = (t >= 22.0) & (t < 22.6)
    x[noise_mask] += 0.6 * rng.standard_normal(noise_mask.sum())  # scream-like: loud, bright, noisy
    x[(t >= 25.0) & (t < 27.0)] = 0.0  # silence
    return np.clip(x * 32767, -32768, 32767).astype(np.int16)


def test_burst_silence_scream_and_zscore() -> None:
    pcm = synth()
    features = audio_features_from_pcm(pcm, 30.0)
    assert features.available and features.method == "heuristic-v1"
    assert features.seconds == 30
    assert features.burst[15] > 0
    assert int(np.argmax(features.energy_z)) in (15, 22)
    assert features.energy_z[15] > 2.0
    assert features.scream_like[22] > 0.3
    assert features.scream_like[15] == 0.0  # pure tone is not noisy enough
    assert any(abs(s - 25.0) < 0.2 and abs(e - 27.0) < 0.2 for s, e in features.silences), (
        features.silences
    )
    assert features.silence[26] == 1.0
    assert features.energy_db[10] < -25 and features.energy_db[15] > -8
    assert features.peak_dbfs[15] > features.peak_dbfs[10]
    assert features.laughter_like.max() == 0.0
    data = features.to_json()
    assert len(data["energyZ"]) == 30 and data["silences"]


def test_streaming_feed_matches_one_shot() -> None:
    pcm = synth(12.0)
    one = audio_features_from_pcm(pcm, 12.0)
    extractor = AudioFeatureExtractor(SR)
    for start in range(0, len(pcm), 7000):  # odd chunk sizes crossing frame boundaries
        extractor.feed(pcm[start : start + 7000])
    streamed = summarize_audio(extractor.finish(), 12.0)
    np.testing.assert_allclose(streamed.energy_db, one.energy_db, atol=1e-6)
    np.testing.assert_allclose(streamed.energy_z, one.energy_z, atol=1e-6)


def test_empty_audio_is_unavailable() -> None:
    features = summarize_audio(AudioFeatureExtractor(SR).finish(), 10.0)
    assert features.available is False and features.error


def test_typical_level_is_the_median_of_the_non_silent_seconds() -> None:
    """The reference of "volume N dB acima do nível típico da gravação"."""
    features = audio_features_from_pcm(synth(), 30.0)
    typical = features.typical_level()
    assert typical is not None
    assert typical == pytest.approx(-33.5, abs=1.5)  # the 0.03 sine (RMS 0.021)
    assert features.to_json()["typicalLevelDbfs"] == pytest.approx(typical, abs=0.01)
    assert features.energy_db[15] - typical > 20  # the loud second, far above the typical level
    assert typical_level_db(np.full(10, -80.0)) is None  # nothing above the silence floor
    assert typical_level_db(np.array([-30.0, -20.0, -90.0, -10.0])) == pytest.approx(-20.0)
