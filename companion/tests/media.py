"""Media helpers for tests (FFmpeg/ffprobe wrappers and a minimal Matroska walker)."""

from __future__ import annotations

import subprocess
from pathlib import Path


def run_ffmpeg(ffmpeg: str, *args: str) -> None:
    subprocess.run(
        [ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", *args],
        check=True,
        timeout=120,
    )


def video_packets(ffprobe: str, path: Path) -> list[tuple[float, bool]]:
    """(pts, keyframe) of every video packet, in file (decode) order."""
    result = subprocess.run(
        [
            ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries",
            "packet=pts_time,flags", "-of", "csv=p=0", str(path),
        ],
        capture_output=True,
        text=True,
        check=True,
        timeout=60,
    )  # fmt: skip
    packets = []
    for line in result.stdout.splitlines():
        parts = line.strip().split(",")
        if len(parts) >= 2 and parts[0] not in ("", "N/A"):
            packets.append((float(parts[0]), "K" in parts[1]))
    return packets


def media_duration(ffprobe: str, path: Path) -> float:
    result = subprocess.run(
        [ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)],
        capture_output=True,
        text=True,
        check=True,
        timeout=60,
    )
    return float(result.stdout.strip())


def cluster_offsets(path: Path) -> list[int]:
    """Byte offsets of the top-level Matroska Cluster elements (minimal EBML walker)."""
    data = path.read_bytes()

    def read_vint(pos: int, keep_marker: bool) -> tuple[int, int, bool]:
        first = data[pos]
        mask = 0x80
        length = 1
        while length <= 8 and not first & mask:
            mask >>= 1
            length += 1
        value = first if keep_marker else first & (mask - 1)
        for i in range(1, length):
            value = (value << 8) | data[pos + i]
        unknown = not keep_marker and value == (1 << (7 * length)) - 1
        return value, length, unknown

    offsets = []
    _, id_len, _ = read_vint(0, True)  # EBML header
    size, size_len, _ = read_vint(id_len, False)
    pos = id_len + size_len + size
    _, id_len, _ = read_vint(pos, True)  # Segment
    _, size_len, _ = read_vint(pos + id_len, False)
    pos += id_len + size_len
    while pos < len(data):
        element_id, id_len, _ = read_vint(pos, True)
        size, size_len, unknown = read_vint(pos + id_len, False)
        if element_id == 0x1F43B675:
            offsets.append(pos)
        if unknown:
            break
        pos += id_len + size_len + size
    return offsets
