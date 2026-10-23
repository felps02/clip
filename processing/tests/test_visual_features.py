"""Visual features: partial coverage when the frame pipe stops before the end."""

from __future__ import annotations

from collections.abc import Callable, Iterator
from typing import Any

import numpy as np
import pytest

import app.analysis.visual_features as visual_module
from app.analysis.visual_features import compute_visual_features
from app.media.ffmpeg import FfmpegNotFound, FfmpegTimeout


def frames_until(
    stop_at: float, error: Exception | None = None
) -> Callable[..., Iterator[tuple[float, np.ndarray]]]:
    """Stand-in for ``iter_frames``: random gray frames at ``fps`` up to ``stop_at`` seconds,
    then ``error`` (a pipe that dies) or a normal end (a video stream that ends early)."""

    def iterate(
        *_args: Any, fps: float = 2.0, **_kwargs: Any
    ) -> Iterator[tuple[float, np.ndarray]]:
        rng = np.random.default_rng(7)
        index = 0
        while index / fps < stop_at:
            yield index / fps, rng.integers(0, 255, (18, 32), dtype=np.uint8)
            index += 1
        if error is not None:
            raise error

    return iterate


def compute(monkeypatch: pytest.MonkeyPatch, frames: Callable[..., Any], duration: float) -> Any:
    monkeypatch.setattr(visual_module, "iter_frames", frames)
    return compute_visual_features(None, "x.mp4", duration, 640, 360)  # type: ignore[arg-type]


def test_a_pipe_that_dies_midway_is_partial_coverage_not_a_complete_analysis(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    features = compute(monkeypatch, frames_until(10.0, FfmpegTimeout("timed out")), 60.0)
    assert features.available is True
    assert features.gaps == [(10.0, 60.0)]
    assert features.coverage == pytest.approx(10.0 / 60.0, abs=1e-3)
    assert features.error is not None
    assert "FfmpegTimeout" in features.error and "10s" in features.error
    data = features.to_json()
    assert data["coverage"] == pytest.approx(0.167, abs=1e-3) and data["gaps"] == [[10.0, 60.0]]


def test_a_video_stream_shorter_than_the_file_is_partial_coverage(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    features = compute(monkeypatch, frames_until(30.0), 60.0)
    assert features.available is True and features.gaps == [(30.0, 60.0)]
    assert features.coverage == pytest.approx(0.5)
    assert features.error is not None and "ended before the end" in features.error


def test_a_complete_decode_has_full_coverage(monkeypatch: pytest.MonkeyPatch) -> None:
    features = compute(monkeypatch, frames_until(60.0), 60.0)
    assert features.available is True and features.gaps == [] and features.error is None
    assert features.coverage == 1.0 and features.to_json()["coverage"] == 1.0


def test_too_few_frames_is_unavailable(monkeypatch: pytest.MonkeyPatch) -> None:
    features = compute(monkeypatch, frames_until(0.4, FfmpegTimeout("timed out")), 60.0)
    assert features.available is False and features.coverage == 0.0
    assert "FfmpegTimeout" in (features.error or "")


def test_a_missing_ffmpeg_is_not_swallowed(monkeypatch: pytest.MonkeyPatch) -> None:
    with pytest.raises(FfmpegNotFound):
        compute(monkeypatch, frames_until(0.0, FfmpegNotFound("ffmpeg not found")), 60.0)
