"""Audio features from 16 kHz mono PCM (streamed in frames of 100 ms, hop 100 ms).

Frame features: RMS energy (dBFS), spectral centroid, zero-crossing rate, spectral flux
and onset strength, plus a 50 Hz amplitude envelope used for the laughter heuristic.

Per-second series (``docs/PROCESSING_PIPELINE.md`` step 3), all heuristic
(``method = "heuristic-v1"``):

* ``energy_z``: robust z-score (median/MAD, rolling 90 s) of the smoothed energy;
* ``burst``: short bursts >= 6 dB above baseline lasting 0.3-2 s (strength 0..2);
* ``sustained``: 3 s windows above baseline (strength 0..2);
* ``scream_like``: energy_z > 2 and centroid > 2.5 kHz and high ZCR for >= 0.4 s (0..1);
* ``laughter_like``: 3-8 Hz amplitude modulation with moderate energy (0..1);
* ``silence``: energy below -45 dBFS for >= 0.5 s (fraction of the second) and the list
  of silence intervals used to snap cut points.

``typical_db`` is the recording's typical level (median per-second level of its
non-silent seconds): the reference of "volume N dB acima do nível típico da gravação".
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any

import numpy as np

from app.analysis.common import (
    find_runs,
    moving_average,
    per_second,
    robust_rolling_z,
    rolling_median,
    rounded,
)

METHOD = "heuristic-v1"
FRAMES_PER_SECOND = 10
SUB_FRAMES_PER_FRAME = 5
ENVELOPE_RATE = FRAMES_PER_SECOND * SUB_FRAMES_PER_FRAME  # 50 Hz
BASELINE_WINDOW_SECONDS = 91
MIN_SIGMA_DB = 1.5
SILENCE_DBFS = -45.0
BURST_DB = 6.0
SUSTAINED_DB = 3.0
SCREAM_CENTROID_HZ = 2500.0
SCREAM_ZCR = 0.15
DB_FLOOR = -100.0


@dataclass(slots=True)
class FrameFeatures:
    rms_db: np.ndarray
    peak_db: np.ndarray
    centroid: np.ndarray
    zcr: np.ndarray
    flux: np.ndarray
    envelope: np.ndarray  # 50 Hz amplitude envelope (linear)
    sample_rate: int = 16000
    frame_seconds: float = 0.1

    @property
    def n_frames(self) -> int:
        return int(len(self.rms_db))


@dataclass(slots=True)
class AudioFeatures:
    available: bool = True
    method: str = METHOD
    seconds: int = 0
    energy_db: np.ndarray = field(default_factory=lambda: np.zeros(0))
    energy_z: np.ndarray = field(default_factory=lambda: np.zeros(0))
    onset_z: np.ndarray = field(default_factory=lambda: np.zeros(0))
    burst: np.ndarray = field(default_factory=lambda: np.zeros(0))
    sustained: np.ndarray = field(default_factory=lambda: np.zeros(0))
    scream_like: np.ndarray = field(default_factory=lambda: np.zeros(0))
    laughter_like: np.ndarray = field(default_factory=lambda: np.zeros(0))
    silence: np.ndarray = field(default_factory=lambda: np.zeros(0))
    peak_dbfs: np.ndarray = field(default_factory=lambda: np.zeros(0))
    centroid_hz: np.ndarray = field(default_factory=lambda: np.zeros(0))
    silences: list[tuple[float, float]] = field(default_factory=list)
    error: str | None = None
    # typical level of the recording (dBFS): median of ``energy_db`` over its non-silent
    # seconds; ``None`` = derive it from ``energy_db`` (see :meth:`typical_level`)
    typical_db: float | None = None

    @classmethod
    def unavailable(cls, seconds: int, error: str) -> AudioFeatures:
        return cls(available=False, seconds=seconds, error=error)

    def typical_level(self) -> float | None:
        """The recording's typical level in dBFS, or ``None`` when it is entirely silent."""
        if self.typical_db is not None:
            return self.typical_db
        return typical_level_db(self.energy_db)

    def to_json(self) -> dict[str, Any]:
        data: dict[str, Any] = {"available": self.available, "method": self.method}
        if self.error:
            data["error"] = self.error
        if self.available:
            typical = self.typical_level()
            data.update(
                {
                    "typicalLevelDbfs": round(typical, 2) if typical is not None else None,
                    "energyDb": rounded(self.energy_db, 2),
                    "energyZ": rounded(self.energy_z),
                    "onsetZ": rounded(self.onset_z),
                    "burst": rounded(self.burst),
                    "sustained": rounded(self.sustained),
                    "screamLike": rounded(self.scream_like),
                    "laughterLike": rounded(self.laughter_like),
                    "silence": rounded(self.silence),
                    "peakDbfs": rounded(self.peak_dbfs, 2),
                    "centroidHz": rounded(self.centroid_hz, 1),
                    "silences": [[round(s, 2), round(e, 2)] for s, e in self.silences],
                }
            )
        return data


class AudioFeatureExtractor:
    """Incremental frame-feature extraction (feed int16 chunks of any size)."""

    def __init__(self, sample_rate: int = 16000, frame_seconds: float = 0.1) -> None:
        self.sample_rate = int(sample_rate)
        self.frame_seconds = frame_seconds
        self.frame_len = int(round(self.sample_rate * frame_seconds))
        self.sub_len = max(1, self.frame_len // SUB_FRAMES_PER_FRAME)
        self._buffer = np.zeros(0, dtype=np.float32)
        self._window = np.hanning(self.frame_len).astype(np.float32)
        self._freqs = np.fft.rfftfreq(self.frame_len, 1.0 / self.sample_rate).astype(np.float32)
        self._prev_spectrum: np.ndarray | None = None
        self._rms_db: list[np.ndarray] = []
        self._peak_db: list[np.ndarray] = []
        self._centroid: list[np.ndarray] = []
        self._zcr: list[np.ndarray] = []
        self._flux: list[np.ndarray] = []
        self._env: list[np.ndarray] = []
        self.samples_seen = 0

    def feed(self, pcm: np.ndarray) -> None:
        if pcm.size == 0:
            return
        self.samples_seen += int(pcm.size)
        if pcm.dtype == np.int16:
            x = pcm.astype(np.float32) / 32768.0
        else:
            x = np.asarray(pcm, dtype=np.float32)
        buf = np.concatenate([self._buffer, x]) if self._buffer.size else x
        n_frames = buf.size // self.frame_len
        if n_frames == 0:
            self._buffer = buf
            return
        used = n_frames * self.frame_len
        frames = buf[:used].reshape(n_frames, self.frame_len)
        self._buffer = buf[used:].copy()
        self._process(frames)

    def _process(self, frames: np.ndarray) -> None:
        n_frames = frames.shape[0]
        rms = np.sqrt(np.mean(frames * frames, axis=1))
        rms_db = 20.0 * np.log10(np.maximum(rms, 1e-5))
        peak = np.max(np.abs(frames), axis=1)
        peak_db = 20.0 * np.log10(np.maximum(peak, 1e-5))
        signs = frames >= 0
        zcr = np.mean(signs[:, 1:] != signs[:, :-1], axis=1)

        spectrum = np.abs(np.fft.rfft(frames * self._window, axis=1)).astype(np.float32)
        total = spectrum.sum(axis=1) + 1e-9
        centroid = (spectrum * self._freqs).sum(axis=1) / total
        normalised = spectrum / total[:, None]
        prev = self._prev_spectrum if self._prev_spectrum is not None else normalised[0]
        prev_rows = np.vstack([prev[None, :], normalised[:-1]])
        flux = np.maximum(normalised - prev_rows, 0.0).sum(axis=1)
        self._prev_spectrum = normalised[-1].copy()

        usable = SUB_FRAMES_PER_FRAME * self.sub_len
        sub = frames[:, :usable].reshape(n_frames * SUB_FRAMES_PER_FRAME, self.sub_len)
        envelope = np.sqrt(np.mean(sub * sub, axis=1))

        self._rms_db.append(np.maximum(rms_db, DB_FLOOR).astype(np.float64))
        self._peak_db.append(np.maximum(peak_db, DB_FLOOR).astype(np.float64))
        self._centroid.append(centroid.astype(np.float64))
        self._zcr.append(zcr.astype(np.float64))
        self._flux.append(flux.astype(np.float64))
        self._env.append(envelope.astype(np.float64))

    def finish(self) -> FrameFeatures:
        if self._buffer.size >= self.frame_len // 2:
            pad = np.zeros(self.frame_len - self._buffer.size, dtype=np.float32)
            self._process(np.concatenate([self._buffer, pad])[None, :])
        self._buffer = np.zeros(0, dtype=np.float32)

        def cat(parts: list[np.ndarray]) -> np.ndarray:
            return np.concatenate(parts) if parts else np.zeros(0, dtype=np.float64)

        return FrameFeatures(
            rms_db=cat(self._rms_db),
            peak_db=cat(self._peak_db),
            centroid=cat(self._centroid),
            zcr=cat(self._zcr),
            flux=cat(self._flux),
            envelope=cat(self._env),
            sample_rate=self.sample_rate,
            frame_seconds=self.frame_seconds,
        )


def _modulation_ratio(envelope: np.ndarray, n_sec: int) -> np.ndarray:
    """Share of envelope-modulation power in the 3-8 Hz band, per second (2 s windows)."""
    rate = ENVELOPE_RATE
    win = 2 * rate
    needed = n_sec * rate
    env = np.asarray(envelope, dtype=np.float64)
    if env.size < needed:
        env = np.concatenate([env, np.zeros(needed - env.size)])
    env = env[:needed]
    half = win // 2
    padded = np.concatenate(
        [
            np.full(half, env[0] if env.size else 0.0),
            env,
            np.full(half, env[-1] if env.size else 0.0),
        ]
    )
    # window centred on the middle of each second; in the padded array the window that is
    # centred on original index c starts at padded index c
    centers = np.arange(n_sec) * rate + rate // 2
    idx = centers[:, None] + np.arange(win)[None, :]
    windows = padded[idx]
    windows = windows - windows.mean(axis=1, keepdims=True)
    power = np.abs(np.fft.rfft(windows * np.hanning(win)[None, :], axis=1)) ** 2
    freqs = np.fft.rfftfreq(win, 1.0 / rate)
    band = (freqs >= 3.0) & (freqs <= 8.0)
    total = (freqs >= 0.5) & (freqs <= 20.0)
    band_power = power[:, band].sum(axis=1)
    total_power = power[:, total].sum(axis=1) + 1e-12
    ratio = band_power / total_power
    # laughter is a regular pulse train: one modulation frequency dominates the band,
    # while speech spreads its syllabic modulation over the whole band
    peak_share = power[:, band].max(axis=1) / (band_power + 1e-12)
    ratio = ratio * np.clip((peak_share - 0.25) / 0.35, 0.0, 1.0)
    # ignore near-silent windows (modulation of noise is meaningless)
    quiet = windows.std(axis=1) < 1e-4
    ratio[quiet] = 0.0
    return ratio


def typical_level_db(energy_db: np.ndarray) -> float | None:
    """Median per-second level (dBFS) over the non-silent seconds (above ``SILENCE_DBFS``):
    the "typical level of the recording" that reasoning texts compare a moment against."""
    values = np.asarray(energy_db, dtype=np.float64)
    voiced = values[np.isfinite(values) & (values > SILENCE_DBFS)]
    if voiced.size == 0:
        return None
    return float(np.median(voiced))


def summarize_audio(frames: FrameFeatures, duration: float) -> AudioFeatures:
    """Turn frame features into the documented per-second series."""
    fps = FRAMES_PER_SECOND
    n_frames = frames.n_frames
    n_sec = int(math.ceil(max(float(duration or 0.0), n_frames / fps)))
    n_sec = max(1, n_sec)
    if n_frames == 0:
        return AudioFeatures.unavailable(n_sec, "no audio samples decoded")

    needed = n_sec * fps

    def pad(values: np.ndarray, fill: float) -> np.ndarray:
        values = np.asarray(values, dtype=np.float64)
        if values.size >= needed:
            return values[:needed]
        return np.concatenate([values, np.full(needed - values.size, fill)])

    rms_db = pad(frames.rms_db, DB_FLOOR)
    peak_db = pad(frames.peak_db, DB_FLOOR)
    centroid = pad(frames.centroid, 0.0)
    zcr = pad(frames.zcr, 0.0)
    flux = pad(frames.flux, 0.0)

    smooth = moving_average(rms_db, 5)  # 0.5 s
    energy_sec = per_second(smooth, fps, n_sec, "max", DB_FLOOR)
    baseline_sec = rolling_median(energy_sec, BASELINE_WINDOW_SECONDS)
    mad_sec = rolling_median(np.abs(energy_sec - baseline_sec), BASELINE_WINDOW_SECONDS)
    sigma_sec = np.maximum(1.4826 * mad_sec, MIN_SIGMA_DB)
    energy_z = (energy_sec - baseline_sec) / sigma_sec

    baseline_f = np.repeat(baseline_sec, fps)
    sigma_f = np.repeat(sigma_sec, fps)
    excess_f = smooth - baseline_f
    z_f = excess_f / sigma_f

    # bursts: >= 6 dB above baseline for 0.3-2 s
    burst = np.zeros(n_sec)
    for s, e in find_runs(excess_f >= BURST_DB, min_len=3, max_len=20):
        strength = min(2.0, float(excess_f[s:e].max()) / BURST_DB)
        burst[s // fps : (e - 1) // fps + 1] = np.maximum(
            burst[s // fps : (e - 1) // fps + 1], strength
        )

    # sustained: 3 s windows above baseline
    ma3 = moving_average(excess_f, 3 * fps)
    sustained_f = np.where(ma3 >= SUSTAINED_DB, np.clip(ma3 / SUSTAINED_DB, 0.0, 2.0), 0.0)
    sustained = per_second(sustained_f, fps, n_sec, "max")

    # scream-like: loud + bright + noisy for >= 0.4 s
    scream_mask = (z_f > 2.0) & (centroid > SCREAM_CENTROID_HZ) & (zcr > SCREAM_ZCR)
    scream_f = np.zeros(needed)
    for s, e in find_runs(scream_mask, min_len=4):
        scream_f[s:e] = 1.0
    scream_like = per_second(scream_f, fps, n_sec, "mean")

    # laughter-like: 3-8 Hz amplitude modulation with moderate energy
    ratio = _modulation_ratio(frames.envelope, n_sec)
    moderate = (energy_z > 0.3) & (energy_z < 4.5) & (energy_sec > -35.0)
    laughter_like = np.where((ratio > 0.5) & moderate, np.clip(ratio, 0.0, 1.0), 0.0)
    # sustained >= 1 s: require the neighbouring second to show modulation too
    neighbour = np.zeros(n_sec, dtype=bool)
    if n_sec > 1:
        neighbour[:-1] |= ratio[1:] > 0.3
        neighbour[1:] |= ratio[:-1] > 0.3
    laughter_like = np.where(neighbour | (n_sec == 1), laughter_like, 0.0)

    # silence intervals (>= 0.5 s below -45 dBFS)
    silences: list[tuple[float, float]] = []
    silence_f = np.zeros(needed)
    for s, e in find_runs(rms_db < SILENCE_DBFS, min_len=5):
        silences.append((s / fps, e / fps))
        silence_f[s:e] = 1.0
    silence = per_second(silence_f, fps, n_sec, "mean")

    onset_sec = per_second(flux, fps, n_sec, "max")
    onset_z = robust_rolling_z(onset_sec, BASELINE_WINDOW_SECONDS, min_sigma=0.02)

    voiced = (rms_db > -50.0).astype(np.float64)
    grid_c = (centroid * voiced)[:needed].reshape(n_sec, fps).sum(axis=1)
    grid_n = voiced[:needed].reshape(n_sec, fps).sum(axis=1)
    centroid_sec = np.divide(grid_c, grid_n, out=np.zeros(n_sec), where=grid_n > 0)

    return AudioFeatures(
        available=True,
        method=METHOD,
        seconds=n_sec,
        energy_db=energy_sec,
        energy_z=energy_z,
        onset_z=onset_z,
        burst=burst,
        sustained=sustained,
        scream_like=scream_like,
        laughter_like=laughter_like,
        silence=silence,
        peak_dbfs=per_second(peak_db, fps, n_sec, "max", DB_FLOOR),
        centroid_hz=centroid_sec,
        silences=silences,
        typical_db=typical_level_db(energy_sec),
    )


def audio_features_from_pcm(
    pcm: np.ndarray, duration: float, sample_rate: int = 16000
) -> AudioFeatures:
    """Convenience for tests / small files: whole PCM array in memory."""
    extractor = AudioFeatureExtractor(sample_rate)
    extractor.feed(pcm)
    return summarize_audio(extractor.finish(), duration)
