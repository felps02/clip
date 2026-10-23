from __future__ import annotations

import numpy as np

from app.analysis.webcam import (
    FaceDetection,
    cluster_detections,
    create_face_detector,
    detect_faces,
    detect_webcam_in_frames,
    sample_times,
)


def test_sample_times_are_inside_the_recording() -> None:
    times = sample_times(90.0, 40)
    assert len(times) == 40
    assert times[0] >= 2.0 and times[-1] <= 88.0
    assert times == sorted(times)
    assert sample_times(0.0) == []
    assert len(sample_times(5.0, 40)) <= 6


def test_stable_corner_cluster_becomes_webcam_region() -> None:
    rng = np.random.default_rng(0)
    detections = [
        FaceDetection(i, 0.86 + rng.normal(0, 0.01), 0.80 + rng.normal(0, 0.01), 0.07, 0.11)
        for i in range(40)
        if i % 4 != 0  # 30 of 40 frames
    ]
    region = cluster_detections(detections, 40)
    assert region is not None
    assert region.x + region.w >= 0.999  # snapped to the right edge
    assert region.y + region.h >= 0.999  # snapped to the bottom edge
    assert 0.12 <= region.w <= 0.5 and 0.12 <= region.h <= 0.5
    assert region.confidence is not None and region.confidence > 0.4
    assert region.x < 0.86 < region.x + region.w


def test_scattered_or_rare_faces_are_rejected() -> None:
    rng = np.random.default_rng(1)
    scattered = [
        FaceDetection(i, rng.uniform(0.1, 0.9), rng.uniform(0.1, 0.9), 0.08, 0.1) for i in range(40)
    ]
    assert cluster_detections(scattered, 40) is None
    rare = [FaceDetection(i, 0.5, 0.5, 0.08, 0.1) for i in range(5)]
    assert cluster_detections(rare, 40) is None
    assert cluster_detections([], 40) is None


def test_detector_is_available_and_handles_blank_frames() -> None:
    detector = create_face_detector()
    assert detector is not None, "no face detector available (YuNet model missing?)"
    assert detector.name in ("yunet", "haar")
    assert detect_faces(np.zeros((180, 320), dtype=np.uint8), 0, detector) == []
    assert detect_faces(np.zeros((180, 320, 3), dtype=np.uint8), 0, detector) == []
    assert detect_faces(np.zeros((8, 8), dtype=np.uint8), 0, detector) == []
    assert detect_webcam_in_frames([np.zeros((180, 320, 3), dtype=np.uint8)] * 5, detector) is None
