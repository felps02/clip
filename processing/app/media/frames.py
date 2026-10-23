"""Frame sampling through an ffmpeg rawvideo pipe (grayscale, small width)."""

from __future__ import annotations

import os
from collections.abc import Iterator

import numpy as np

from app.media.ffmpeg import FfmpegTools, fmt_num, timeout_for
from app.worker.errors import CancelToken


def scaled_height(src_w: int, src_h: int, width: int) -> int:
    """Even height that keeps the aspect ratio for the requested width."""
    if src_w <= 0 or src_h <= 0:
        return 2
    height = int(round(src_h * width / src_w / 2.0)) * 2
    return max(2, height)


def iter_frames(
    tools: FfmpegTools,
    path: str | os.PathLike[str],
    *,
    src_w: int,
    src_h: int,
    fps: float = 2.0,
    width: int = 320,
    start: float | None = None,
    duration: float | None = None,
    cancel: CancelToken | None = None,
    total_duration: float | None = None,
) -> Iterator[tuple[float, np.ndarray]]:
    """Yield ``(timestamp_seconds, gray uint8 HxW array)`` at ``fps`` frames per second."""
    width = max(16, int(width) // 2 * 2)
    height = scaled_height(src_w, src_h, width)
    frame_bytes = width * height
    args: list[str] = []
    if start is not None and start > 0:
        args += ["-ss", fmt_num(start)]
    args += ["-i", os.fspath(path)]
    if duration is not None:
        args += ["-t", fmt_num(duration)]
    args += [
        "-an",
        "-sn",
        "-dn",
        "-map",
        "0:v:0",
        "-vf",
        f"fps={fmt_num(fps)},scale={width}:{height}:flags=area",
        "-f",
        "rawvideo",
        "-pix_fmt",
        "gray",
        "-",
    ]
    timeout = timeout_for(
        duration if duration is not None else total_duration, factor=2.0, minimum=180
    )
    base = float(start or 0.0)
    with tools.stream(args, timeout=timeout, cancel=cancel) as pipe:
        for index, raw in enumerate(pipe.iter_chunks(frame_bytes)):
            if len(raw) < frame_bytes:
                break
            if cancel is not None and index % 50 == 0:
                cancel.raise_if_cancelled()
            frame = np.frombuffer(raw, dtype=np.uint8).reshape(height, width)
            yield base + index / fps, frame
        pipe.finished_cleanly = True


def extract_frame(
    tools: FfmpegTools,
    path: str | os.PathLike[str],
    *,
    at: float,
    src_w: int,
    src_h: int,
    width: int = 640,
    cancel: CancelToken | None = None,
    color: bool = False,
) -> np.ndarray | None:
    """Fast-seek to ``at`` seconds and return one frame (gray HxW, or BGR HxWx3 when
    ``color``), or ``None`` past the end of the file."""
    width = max(16, int(width) // 2 * 2)
    height = scaled_height(src_w, src_h, width)
    pix_fmt = "bgr24" if color else "gray"
    channels = 3 if color else 1
    args = [
        "-ss",
        fmt_num(max(0.0, at)),
        "-i",
        os.fspath(path),
        "-an",
        "-sn",
        "-dn",
        "-map",
        "0:v:0",
        "-frames:v",
        "1",
        "-vf",
        f"scale={width}:{height}:flags=area",
        "-f",
        "rawvideo",
        "-pix_fmt",
        pix_fmt,
        "-",
    ]
    result = tools.run(args, timeout=120.0, cancel=cancel, capture_stdout=True)
    expected = width * height * channels
    if len(result.stdout) < expected:
        return None
    frame = np.frombuffer(result.stdout[:expected], dtype=np.uint8)
    if color:
        return frame.reshape(height, width, channels)
    return frame.reshape(height, width)
