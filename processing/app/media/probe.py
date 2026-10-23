"""ffprobe JSON parsing.

``width``/``height`` are the *display* dimensions: rotation metadata (``Display Matrix``
side data or the legacy ``rotate`` tag of phone recordings) is applied, because ffmpeg
autorotates when decoding, so every filter graph, frame sample and crop in this service
sees the rotated frame.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from fractions import Fraction
from pathlib import Path
from typing import Any

from app.media.ffmpeg import FfmpegTools
from app.models import ProbeReport

CONTAINER_BY_FORMAT: dict[str, str] = {
    "mov": "mp4",
    "mp4": "mp4",
    "m4a": "mp4",
    "matroska": "mkv",
    "webm": "webm",
    "mp3": "mp3",
    "wav": "wav",
    "flv": "flv",
    "mpegts": "ts",
    "avi": "avi",
    "ogg": "ogg",
    "flac": "flac",
    "aac": "aac",
}


@dataclass(slots=True)
class ProbeResult:
    duration: float | None
    width: int | None
    height: int | None
    fps: float | None
    has_audio: bool
    has_video: bool
    container: str | None
    video_codec: str | None
    audio_codec: str | None
    size_bytes: int | None
    sample_rate: int | None = None
    channels: int | None = None
    bit_rate: int | None = None
    rotation: int = 0  # 0/90/180/270, already applied to width/height
    raw: dict[str, Any] = field(default_factory=dict, repr=False)

    def to_report(self) -> ProbeReport:
        return ProbeReport(
            duration_seconds=round(float(self.duration or 0.0), 3),
            width=self.width,
            height=self.height,
            fps=round(self.fps, 3) if self.fps else None,
            has_audio=self.has_audio,
            has_video=self.has_video,
            container=self.container,
            video_codec=self.video_codec,
            audio_codec=self.audio_codec,
            size_bytes=self.size_bytes,
        )


def _to_float(value: Any) -> float | None:
    try:
        if value is None or value == "N/A":
            return None
        number = float(value)
    except TypeError, ValueError:
        return None
    return number if number == number else None


def _to_int(value: Any) -> int | None:
    number = _to_float(value)
    return int(number) if number is not None else None


def parse_fps(value: Any) -> float | None:
    """``"30000/1001"`` -> 29.97; ``"0/0"`` -> None."""
    if not value or not isinstance(value, str):
        return _to_float(value)
    try:
        frac = Fraction(value)
    except ValueError, ZeroDivisionError:
        return None
    if frac <= 0:
        return None
    fps = float(frac)
    return fps if 0 < fps < 1000 else None


def _is_attached_picture(stream: dict[str, Any]) -> bool:
    disposition = stream.get("disposition") or {}
    return bool(disposition.get("attached_pic"))


def parse_rotation(stream: dict[str, Any]) -> int:
    """Rotation of a video stream normalised to 0/90/180/270.

    Read from the ``Display Matrix`` side data (``rotation``, what ffprobe >= 5 reports)
    or the legacy ``tags.rotate`` of older files. Values are rounded to the nearest
    quarter turn; sign/direction does not matter for swapping the dimensions.
    """
    value: float | None = None
    for item in stream.get("side_data_list") or []:
        if not isinstance(item, dict) or "rotation" not in item:
            continue
        kind = item.get("side_data_type")
        if kind in (None, "Display Matrix"):
            value = _to_float(item.get("rotation"))
            if value is not None:
                break
    if value is None:
        tags = stream.get("tags") or {}
        value = _to_float(tags.get("rotate")) if isinstance(tags, dict) else None
    if value is None:
        return 0
    return int(round(value / 90.0)) * 90 % 360


def container_from_format(format_name: str | None, file_name: str | None = None) -> str | None:
    if not format_name:
        return None
    tokens = [t.strip().lower() for t in format_name.split(",") if t.strip()]
    ext = Path(file_name).suffix.lower().lstrip(".") if file_name else ""
    if "matroska" in tokens or "webm" in tokens:
        return "webm" if ext == "webm" else "mkv"
    if "mp4" in tokens or "mov" in tokens:
        return "mov" if ext == "mov" else "mp4"
    for token in tokens:
        if token in CONTAINER_BY_FORMAT:
            return CONTAINER_BY_FORMAT[token]
    return tokens[0] if tokens else None


def parse_probe(
    data: dict[str, Any], *, size_bytes: int | None = None, file_name: str | None = None
) -> ProbeResult:
    streams = [s for s in data.get("streams", []) if isinstance(s, dict)]
    fmt = data.get("format") or {}
    video = next(
        (s for s in streams if s.get("codec_type") == "video" and not _is_attached_picture(s)),
        None,
    )
    audio = next((s for s in streams if s.get("codec_type") == "audio"), None)

    duration = _to_float(fmt.get("duration"))
    if duration is None:
        durations = [d for d in (_to_float(s.get("duration")) for s in streams) if d]
        duration = max(durations) if durations else None

    width = _to_int(video.get("width")) if video else None
    height = _to_int(video.get("height")) if video else None
    rotation = parse_rotation(video) if video else 0
    if rotation in (90, 270) and width and height:
        width, height = height, width  # stored landscape, displayed (and decoded) portrait
    fps = None
    if video:
        fps = parse_fps(video.get("avg_frame_rate")) or parse_fps(video.get("r_frame_rate"))

    size = _to_int(fmt.get("size"))
    if size is None:
        size = size_bytes

    return ProbeResult(
        duration=duration,
        width=width,
        height=height,
        fps=fps,
        has_audio=audio is not None,
        has_video=video is not None and bool(width and height),
        container=container_from_format(fmt.get("format_name"), file_name or fmt.get("filename")),
        video_codec=(video.get("codec_name") if video else None),
        audio_codec=(audio.get("codec_name") if audio else None),
        size_bytes=size,
        sample_rate=_to_int(audio.get("sample_rate")) if audio else None,
        channels=_to_int(audio.get("channels")) if audio else None,
        bit_rate=_to_int(fmt.get("bit_rate")),
        rotation=rotation,
        raw=data,
    )


def probe_file(tools: FfmpegTools, path: str | os.PathLike[str]) -> ProbeResult:
    path = Path(path)
    data = tools.probe_json(path)
    size = path.stat().st_size if path.exists() else None
    return parse_probe(data, size_bytes=size, file_name=path.name)
