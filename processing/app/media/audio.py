"""PCM decoding through an ffmpeg pipe (16 kHz mono s16le by default)."""

from __future__ import annotations

import os
from collections.abc import Iterator

import numpy as np

from app.media.ffmpeg import FfmpegTools, fmt_num, timeout_for
from app.worker.errors import CancelToken

SAMPLE_RATE = 16000


def pcm_args(
    path: str | os.PathLike[str],
    *,
    sample_rate: int = SAMPLE_RATE,
    start: float | None = None,
    duration: float | None = None,
) -> list[str]:
    args: list[str] = []
    if start is not None and start > 0:
        args += ["-ss", fmt_num(start)]
    args += ["-i", os.fspath(path)]
    if duration is not None:
        args += ["-t", fmt_num(duration)]
    args += [
        "-vn",
        "-sn",
        "-dn",
        "-map",
        "0:a:0",
        "-ac",
        "1",
        "-ar",
        str(int(sample_rate)),
        "-f",
        "s16le",
        "-acodec",
        "pcm_s16le",
        "-",
    ]
    return args


def iter_pcm(
    tools: FfmpegTools,
    path: str | os.PathLike[str],
    *,
    sample_rate: int = SAMPLE_RATE,
    start: float | None = None,
    duration: float | None = None,
    chunk_seconds: float = 10.0,
    cancel: CancelToken | None = None,
    total_duration: float | None = None,
) -> Iterator[np.ndarray]:
    """Yield int16 mono chunks of ``chunk_seconds`` (the last one may be shorter)."""
    chunk_bytes = int(chunk_seconds * sample_rate) * 2
    timeout = timeout_for(
        duration if duration is not None else total_duration, factor=0.5, minimum=120
    )
    with tools.stream(
        pcm_args(path, sample_rate=sample_rate, start=start, duration=duration),
        timeout=timeout,
        cancel=cancel,
    ) as pipe:
        for raw in pipe.iter_chunks(chunk_bytes):
            if cancel is not None:
                cancel.raise_if_cancelled()
            usable = len(raw) - (len(raw) % 2)
            if usable <= 0:
                continue
            yield np.frombuffer(raw[:usable], dtype=np.int16)
        pipe.finished_cleanly = True


def decode_pcm(
    tools: FfmpegTools,
    path: str | os.PathLike[str],
    *,
    sample_rate: int = SAMPLE_RATE,
    start: float | None = None,
    duration: float | None = None,
    cancel: CancelToken | None = None,
) -> np.ndarray:
    """Decode a (bounded) range to one int16 array."""
    chunks = list(
        iter_pcm(
            tools, path, sample_rate=sample_rate, start=start, duration=duration, cancel=cancel
        )
    )
    if not chunks:
        return np.zeros(0, dtype=np.int16)
    return np.concatenate(chunks)


def pcm_to_float(pcm: np.ndarray) -> np.ndarray:
    """int16 -> float32 in [-1, 1] (what faster-whisper expects)."""
    return (pcm.astype(np.float32) / 32768.0).astype(np.float32)
