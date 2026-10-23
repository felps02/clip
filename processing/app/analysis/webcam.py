"""Webcam overlay detection: faces on ~40 sampled frames -> stable rectangle.

Face detector: YuNet (``cv2.FaceDetectorYN``, model bundled in ``assets/models``,
MIT license). OpenCV 5 wheels no longer ship ``cv2.CascadeClassifier`` nor the Haar XML
files; when they do exist (older OpenCV builds) the Haar cascade is used as a fallback.
Without any detector the webcam detection is simply unavailable (``None``).

If faces cluster in a stable rectangle (typically a corner of the frame) the cluster is
expanded to the likely webcam window and returned normalised (``x, y, w, h`` in 0..1)
with a confidence; otherwise ``None``.
"""

from __future__ import annotations

import contextlib
from collections.abc import Iterable
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import cv2
import numpy as np

from app.config import ASSETS_DIR
from app.logging_setup import get_logger
from app.media.ffmpeg import FfmpegTools
from app.media.frames import extract_frame
from app.models import WebcamRegion
from app.worker.errors import CancelToken

log = get_logger("webcam")

YUNET_MODEL = ASSETS_DIR / "models" / "face_detection_yunet_2023mar.onnx"
CLUSTER_RADIUS = 0.12
MIN_STABILITY = 0.35
MIN_DETECTIONS = 6
EDGE_SNAP = 0.06
MIN_FACE_PX = 16


@dataclass(slots=True)
class FaceDetection:
    frame: int
    x: float  # normalised centre x
    y: float  # normalised centre y
    w: float  # normalised width
    h: float  # normalised height
    score: float = 1.0


class FaceDetector(Protocol):
    name: str

    def detect(self, bgr: np.ndarray) -> list[tuple[float, float, float, float, float]]:
        """Return ``(x, y, w, h, score)`` boxes in pixels for a BGR frame."""
        ...


class YuNetDetector:
    name = "yunet"

    def __init__(self, model_path: Path = YUNET_MODEL, score_threshold: float = 0.6) -> None:
        self.model_path = Path(model_path)
        self._detector = cv2.FaceDetectorYN.create(
            str(self.model_path), "", (320, 320), score_threshold, 0.3, 200
        )
        self._size: tuple[int, int] | None = None

    def detect(self, bgr: np.ndarray) -> list[tuple[float, float, float, float, float]]:
        h, w = bgr.shape[:2]
        if (w, h) != self._size:
            self._detector.setInputSize((w, h))
            self._size = (w, h)
        _, faces = self._detector.detect(bgr)
        if faces is None:
            return []
        out: list[tuple[float, float, float, float, float]] = []
        for face in faces:
            x, y, fw, fh = (float(v) for v in face[:4])
            if fw >= MIN_FACE_PX and fh >= MIN_FACE_PX:
                out.append((x, y, fw, fh, float(face[-1])))
        return out


class HaarDetector:
    name = "haar"

    def __init__(self, cascade_path: Path) -> None:
        self._cascade = cv2.CascadeClassifier(str(cascade_path))  # type: ignore[attr-defined]
        if self._cascade.empty():
            raise RuntimeError(f"could not load Haar cascade from {cascade_path}")

    def detect(self, bgr: np.ndarray) -> list[tuple[float, float, float, float, float]]:
        gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY) if bgr.ndim == 3 else bgr
        rects = self._cascade.detectMultiScale(
            cv2.equalizeHist(gray), scaleFactor=1.1, minNeighbors=5, minSize=(24, 24)
        )
        return [(float(x), float(y), float(w), float(h), 1.0) for x, y, w, h in rects]


def _quiet_opencv() -> None:
    # the dnn backend prints a harmless "targets not supported" warning on creation
    with contextlib.suppress(AttributeError):
        cv2.utils.logging.setLogLevel(cv2.utils.logging.LOG_LEVEL_ERROR)


def create_face_detector() -> FaceDetector | None:
    """YuNet when available, Haar cascade on older OpenCV builds, otherwise ``None``."""
    if hasattr(cv2, "FaceDetectorYN") and YUNET_MODEL.exists():
        try:
            _quiet_opencv()
            return YuNetDetector(YUNET_MODEL)
        except cv2.error as exc:  # pragma: no cover - depends on the OpenCV build
            log.warning("webcam.yunet_failed", error=str(exc)[:200])
    if hasattr(cv2, "CascadeClassifier"):
        data = getattr(getattr(cv2, "data", None), "haarcascades", None)
        if data:
            cascade = Path(data) / "haarcascade_frontalface_default.xml"
            if cascade.exists():
                try:
                    return HaarDetector(cascade)
                except RuntimeError as exc:  # pragma: no cover
                    log.warning("webcam.haar_failed", error=str(exc)[:200])
    log.warning("webcam.no_detector", yunet_model=str(YUNET_MODEL))
    return None


def sample_times(duration: float, n: int = 40) -> list[float]:
    duration = max(0.0, float(duration))
    if duration <= 0:
        return []
    n = max(1, min(n, int(duration) + 1))
    return [float(t) for t in np.linspace(duration * 0.03, duration * 0.97, n)]


def detect_faces(
    frame: np.ndarray, frame_index: int = 0, detector: FaceDetector | None = None
) -> list[FaceDetection]:
    """Detect faces in a BGR (or grayscale) frame; normalised boxes."""
    h, w = frame.shape[:2]
    if h < 32 or w < 32:
        return []
    detector = detector or create_face_detector()
    if detector is None:
        return []
    bgr = frame if frame.ndim == 3 else cv2.cvtColor(frame, cv2.COLOR_GRAY2BGR)
    out: list[FaceDetection] = []
    for x, y, fw, fh, score in detector.detect(np.ascontiguousarray(bgr)):
        out.append(
            FaceDetection(
                frame_index, (x + fw / 2.0) / w, (y + fh / 2.0) / h, fw / w, fh / h, score
            )
        )
    return out


def cluster_detections(detections: list[FaceDetection], n_frames: int) -> WebcamRegion | None:
    """Find the densest stable cluster of face detections and expand it to a webcam box."""
    required = min(MIN_DETECTIONS, max(2, n_frames // 3))
    if n_frames <= 0 or len(detections) < required:
        return None
    xs = np.array([d.x for d in detections])
    ys = np.array([d.y for d in detections])
    ws = np.array([d.w for d in detections])
    hs = np.array([d.h for d in detections])
    frames = np.array([d.frame for d in detections])

    best_idx: np.ndarray | None = None
    best_count = 0
    for i in range(len(detections)):
        near = (np.abs(xs - xs[i]) < CLUSTER_RADIUS) & (np.abs(ys - ys[i]) < CLUSTER_RADIUS)
        similar = (ws > ws[i] * 0.5) & (ws < ws[i] * 2.0)
        members = np.flatnonzero(near & similar)
        count = len(np.unique(frames[members]))
        if count > best_count:
            best_count = count
            best_idx = members
    if best_idx is None:
        return None
    stability = best_count / float(n_frames)
    if stability < MIN_STABILITY or best_count < required:
        return None

    cx = float(np.median(xs[best_idx]))
    cy = float(np.median(ys[best_idx]))
    fw = float(np.median(ws[best_idx]))
    fh = float(np.median(hs[best_idx]))
    spread = float(np.std(xs[best_idx]) + np.std(ys[best_idx]))

    # a webcam window (head and shoulders) is roughly 3.5x the face width and 2.8x the
    # face height, with the face slightly above the window centre
    region_w = min(0.5, max(0.15, fw * 3.5))
    region_h = min(0.5, max(0.15, fh * 2.8))
    x = cx - region_w / 2.0
    y = cy - region_h * 0.4
    if x < EDGE_SNAP:
        x = 0.0
    if y < EDGE_SNAP:
        y = 0.0
    if x + region_w > 1.0 - EDGE_SNAP:
        x = 1.0 - region_w
    if y + region_h > 1.0 - EDGE_SNAP:
        y = 1.0 - region_h
    x = min(max(x, 0.0), 1.0 - region_w)
    y = min(max(y, 0.0), 1.0 - region_h)
    confidence = max(0.0, min(1.0, stability * 1.2 * (1.0 - min(1.0, spread * 4.0))))
    return WebcamRegion(
        x=round(x, 4),
        y=round(y, 4),
        w=round(region_w, 4),
        h=round(region_h, 4),
        confidence=round(confidence, 3),
    )


def detect_webcam_in_frames(
    frames: Iterable[np.ndarray], detector: FaceDetector | None = None
) -> WebcamRegion | None:
    detector = detector or create_face_detector()
    if detector is None:
        return None
    detections: list[FaceDetection] = []
    n = 0
    for index, frame in enumerate(frames):
        n += 1
        detections.extend(detect_faces(frame, index, detector))
    return cluster_detections(detections, n)


def detect_webcam(
    tools: FfmpegTools,
    path: str | Path,
    duration: float,
    src_w: int,
    src_h: int,
    *,
    n_samples: int = 40,
    width: int = 960,
    cancel: CancelToken | None = None,
    start: float = 0.0,
) -> WebcamRegion | None:
    """Samples ``duration`` seconds of the file from ``start`` (a live window skips the
    beginning of its file)."""
    detector = create_face_detector()
    if detector is None:
        return None
    times = sample_times(duration, n_samples)
    start = max(0.0, float(start))
    detections: list[FaceDetection] = []
    sampled = 0
    for index, t in enumerate(times):
        if cancel is not None:
            cancel.raise_if_cancelled()
        try:
            frame = extract_frame(
                tools,
                path,
                at=start + t,
                src_w=src_w,
                src_h=src_h,
                width=width,
                cancel=cancel,
                color=True,
            )
        except Exception as exc:
            from app.worker.errors import JobCancelled

            if isinstance(exc, JobCancelled):
                raise
            log.warning("webcam.frame_failed", at=round(t, 2), error=str(exc)[:200])
            continue
        if frame is None:
            continue
        sampled += 1
        detections.extend(detect_faces(frame, index, detector))
    if sampled == 0:
        return None
    region = cluster_detections(detections, sampled)
    log.info(
        "webcam.result",
        detector=detector.name,
        sampled=sampled,
        faces=len(detections),
        region=region.to_api() if region else None,
    )
    return region
