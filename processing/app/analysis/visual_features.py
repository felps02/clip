"""Visual features from frames sampled at 2 fps / 320 px (ffmpeg rawvideo -> OpenCV).

Per frame: ``motion`` (mean absolute difference to the previous frame), ``scene_cut``
(histogram distance above a threshold) and ``flash`` (global brightness jump). The
series are reduced per second and ``motion`` is z-scored against a rolling baseline.

When the frame pipe stops before the end of the recording (ffmpeg timeout, decoder error,
a video stream shorter than the file) the features stay available for the decoded part
only: the rest is a gap (``gaps``/``coverage``), never presented as analysed.
"""

from __future__ import annotations

import math
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import cv2
import numpy as np

from app.analysis.common import Gaps, coverage_fraction, robust_rolling_z, rounded
from app.logging_setup import get_logger
from app.media.ffmpeg import FfmpegNotFound, FfmpegTools
from app.media.frames import iter_frames
from app.worker.errors import CancelToken, JobCancelled

log = get_logger("visual")

METHOD = "opencv-v1"
SCENE_CUT_THRESHOLD = 0.35  # Bhattacharyya distance between 32-bin histograms
SCENE_CUT_MIN_MOTION = 12.0
FLASH_JUMP = 45.0
BASELINE_WINDOW_SECONDS = 91
GAP_TOLERANCE_SECONDS = 2.0  # frames ending this close to the duration count as complete


@dataclass(slots=True)
class VisualFeatures:
    available: bool = True
    method: str = METHOD
    seconds: int = 0
    motion: np.ndarray = field(default_factory=lambda: np.zeros(0))
    motion_z: np.ndarray = field(default_factory=lambda: np.zeros(0))
    scene_cut: np.ndarray = field(default_factory=lambda: np.zeros(0))
    flash: np.ndarray = field(default_factory=lambda: np.zeros(0))
    brightness: np.ndarray = field(default_factory=lambda: np.zeros(0))
    frames_analyzed: int = 0
    fps: float = 2.0
    width: int = 320
    error: str | None = None
    duration: float = 0.0
    gaps: Gaps = field(default_factory=list)  # time ranges without decoded frames

    @property
    def coverage(self) -> float:
        """Share of the recording covered by decoded frames (0..1; 0 when unavailable)."""
        if not self.available:
            return 0.0
        total = self.duration or float(self.seconds)
        return coverage_fraction(self.gaps, 0.0, total) if total > 0 else 1.0

    @classmethod
    def unavailable(cls, seconds: int, error: str) -> VisualFeatures:
        return cls(available=False, seconds=seconds, error=error)

    def to_json(self) -> dict[str, Any]:
        data: dict[str, Any] = {"available": self.available, "method": self.method}
        if self.error:
            data["error"] = self.error
        if self.available:
            data.update(
                {
                    "coverage": round(self.coverage, 3),
                    "gaps": [[round(s, 2), round(e, 2)] for s, e in self.gaps],
                    "fps": self.fps,
                    "width": self.width,
                    "framesAnalyzed": self.frames_analyzed,
                    "motion": rounded(self.motion, 2),
                    "motionZ": rounded(self.motion_z),
                    "sceneCut": rounded(self.scene_cut, 0),
                    "flash": rounded(self.flash, 0),
                    "brightness": rounded(self.brightness, 1),
                }
            )
        return data


class VisualAccumulator:
    """Frame-by-frame accumulation (independent of the frame source, for tests)."""

    def __init__(self, fps: float) -> None:
        self.fps = fps
        self.times: list[float] = []
        self.motion: list[float] = []
        self.hist_dist: list[float] = []
        self.brightness: list[float] = []
        self._prev: np.ndarray | None = None
        self._prev_hist: np.ndarray | None = None

    def add(self, t: float, gray: np.ndarray) -> None:
        frame = gray.astype(np.float32)
        hist = cv2.calcHist([gray], [0], None, [32], [0, 256])
        cv2.normalize(hist, hist, alpha=1.0, beta=0.0, norm_type=cv2.NORM_L1)
        if self._prev is None:
            motion = 0.0
            dist = 0.0
        else:
            motion = float(np.mean(np.abs(frame - self._prev)))
            dist = float(cv2.compareHist(self._prev_hist, hist, cv2.HISTCMP_BHATTACHARYYA))
        self.times.append(t)
        self.motion.append(motion)
        self.hist_dist.append(dist)
        self.brightness.append(float(frame.mean()))
        self._prev = frame
        self._prev_hist = hist

    def summarize(self, duration: float, width: int) -> VisualFeatures:
        n_frames = len(self.times)
        n_sec = max(
            1,
            int(
                math.ceil(
                    max(
                        float(duration or 0.0),
                        (self.times[-1] + 1.0 / self.fps) if n_frames else 0.0,
                    )
                )
            ),
        )
        if n_frames < 2:
            return VisualFeatures.unavailable(n_sec, "not enough frames decoded")
        times = np.asarray(self.times)
        motion = np.asarray(self.motion)
        dist = np.asarray(self.hist_dist)
        bright = np.asarray(self.brightness)
        idx = np.clip(times.astype(np.int64), 0, n_sec - 1)

        motion_sec = np.zeros(n_sec)
        counts = np.zeros(n_sec)
        np.add.at(motion_sec, idx, motion)
        np.add.at(counts, idx, 1.0)
        motion_sec = np.divide(motion_sec, counts, out=np.zeros(n_sec), where=counts > 0)

        bright_sec = np.zeros(n_sec)
        np.add.at(bright_sec, idx, bright)
        bright_sec = np.divide(bright_sec, counts, out=np.zeros(n_sec), where=counts > 0)

        cut_frames = (dist > SCENE_CUT_THRESHOLD) & (motion > SCENE_CUT_MIN_MOTION)
        cut_sec = np.zeros(n_sec)
        np.add.at(cut_sec, idx[cut_frames], 1.0)

        jumps = np.concatenate([[0.0], np.diff(bright)])
        flash_frames = (jumps > FLASH_JUMP) & (np.concatenate([[255.0], bright[:-1]]) < 140.0)
        flash_sec = np.zeros(n_sec)
        np.add.at(flash_sec, idx[flash_frames], 1.0)
        flash_sec = np.minimum(flash_sec, 1.0)

        motion_z = robust_rolling_z(motion_sec, BASELINE_WINDOW_SECONDS, min_sigma=1.5)
        total = max(float(duration or 0.0), 0.0)
        covered_until = float(self.times[-1]) + 1.0 / self.fps
        gaps = (
            [(round(covered_until, 2), total)]
            if total - covered_until > max(GAP_TOLERANCE_SECONDS, 4.0 / self.fps)
            else []
        )
        return VisualFeatures(
            available=True,
            method=METHOD,
            seconds=n_sec,
            motion=motion_sec,
            motion_z=motion_z,
            scene_cut=cut_sec,
            flash=flash_sec,
            brightness=bright_sec,
            frames_analyzed=n_frames,
            fps=self.fps,
            width=width,
            duration=total,
            gaps=gaps,
        )


def compute_visual_features(
    tools: FfmpegTools,
    path: str | Path,
    duration: float,
    src_w: int,
    src_h: int,
    *,
    fps: float = 2.0,
    width: int = 320,
    progress: Callable[[float, float], None] | None = None,
    cancel: CancelToken | None = None,
    start: float = 0.0,
) -> VisualFeatures:
    """Visual series of ``duration`` seconds of the file from ``start`` (a live window skips
    the beginning of its file); the frame times are relative to ``start``."""
    acc = VisualAccumulator(fps)
    last_report = -1.0
    error: str | None = None
    start = max(0.0, float(start))
    try:
        for t, gray in iter_frames(
            tools,
            path,
            src_w=src_w,
            src_h=src_h,
            fps=fps,
            width=width,
            start=start if start > 0 else None,
            cancel=cancel,
            total_duration=duration,
        ):
            t -= start
            acc.add(t, gray)
            if progress is not None and t - last_report >= 5.0:
                last_report = t
                progress(t, duration)
    except JobCancelled, FfmpegNotFound:
        raise  # a cancel, or a deployment problem the job must report as such
    except Exception as exc:
        error = f"{type(exc).__name__}: {exc}"[:300]
        log.warning("visual.failed", error=error, frames=len(acc.times))
        if len(acc.times) < 2:
            return VisualFeatures.unavailable(max(1, int(math.ceil(duration))), error)
    features = acc.summarize(duration, width)
    if features.available and features.gaps:
        detail = error or "the video stream ended before the end of the file"
        features.error = (f"frames decoded up to {features.gaps[0][0]:.0f}s only: {detail}")[:300]
        log.warning("visual.partial", coverage=round(features.coverage, 3), error=features.error)
    elif error is not None:
        features.error = error
    return features
