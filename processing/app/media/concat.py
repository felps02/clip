"""Stream-copy concatenation of recorded segments (FFmpeg concat demuxer).

Used for live sessions: the window of a ``live_segment`` job (previous segment + current
one) and the source of a ``render_clip`` of a live project (every segment overlapping the
clip). Nothing is re-encoded: ``-c copy`` into Matroska, so memory stays flat and the
cost is a file copy.

The list refers to the parts by bare file names in the output's directory, which keeps the
demuxer's ``safe`` mode on (no absolute path, no special character ever reaches the list),
and states each part's duration so the offsets of the concatenated timeline are exactly the
ones :class:`SegmentTimeline` maps session times with.
"""

from __future__ import annotations

import bisect
import os
import re
from dataclasses import dataclass
from pathlib import Path

from app.media.ffmpeg import FfmpegTools, fmt_num, timeout_for
from app.worker.errors import CancelToken

# what the concat demuxer's safe mode accepts in a relative name (portable character set,
# no leading dot)
SAFE_PART_NAME_RE = re.compile(r"^[A-Za-z0-9_-][A-Za-z0-9_.-]{0,79}$")


def concat_list(parts: list[Path], durations: list[float | None] | None = None) -> str:
    """``ffconcat`` list of ``parts`` (bare names) with their durations when known."""
    lines = ["ffconcat version 1.0"]
    for index, part in enumerate(parts):
        if not SAFE_PART_NAME_RE.match(part.name):
            raise ValueError(f"unsafe part name for the concat list: {part.name!r}")
        lines.append(f"file {part.name}")
        duration = durations[index] if durations is not None else None
        if duration is not None and duration > 0:
            lines.append(f"duration {fmt_num(duration)}")
    return "\n".join(lines) + "\n"


def concat_copy(
    tools: FfmpegTools,
    parts: list[Path],
    out: Path,
    *,
    durations: list[float | None] | None = None,
    cancel: CancelToken | None = None,
) -> Path:
    """Concatenate ``parts`` (all in ``out``'s directory) into ``out`` without re-encoding:
    the first video stream and the first audio stream (when present)."""
    if not parts:
        raise ValueError("nothing to concatenate")
    directory = out.parent.resolve()
    for part in parts:
        if part.parent.resolve() != directory:
            raise ValueError(f"part {part.name} is not in {directory}")
    list_path = out.with_name(f"{out.stem}.ffconcat")
    list_path.write_text(concat_list(parts, durations), encoding="utf-8")
    total = sum(d for d in (durations or []) if d)
    try:
        tools.run(
            [
                "-f",
                "concat",
                "-i",
                os.fspath(list_path),
                "-map",
                "0:v:0",
                "-map",
                "0:a:0?",
                "-c",
                "copy",
                "-f",
                "matroska",
                os.fspath(out),
            ],
            timeout=timeout_for(total or None, factor=0.5, minimum=120.0),
            cancel=cancel,
        )
    finally:
        list_path.unlink(missing_ok=True)
    return out


@dataclass(slots=True)
class TimelinePart:
    session_start: float  # session time of the part's first sample
    local_start: float  # time of that sample in the concatenated file
    duration: float


class SegmentTimeline:
    """Maps session times to the timeline of the concatenated file: each part starts in the
    file where the previous one ended (its duration), and in the session at its own
    ``startOffsetSeconds``. A time is placed in the last part that starts at or before it;
    a time in a gap between two parts is held at the end of the earlier part."""

    def __init__(self, parts: list[TimelinePart]) -> None:
        if not parts:
            raise ValueError("empty timeline")
        self.parts = sorted(parts, key=lambda p: p.session_start)
        self._starts = [p.session_start for p in self.parts]

    @classmethod
    def from_segments(cls, session_starts: list[float], durations: list[float]) -> SegmentTimeline:
        parts: list[TimelinePart] = []
        local = 0.0
        for start, duration in zip(session_starts, durations, strict=True):
            parts.append(TimelinePart(float(start), local, float(duration)))
            local += float(duration)
        return cls(parts)

    @property
    def duration(self) -> float:
        last = self.parts[-1]
        return last.local_start + last.duration

    def to_local(self, session_time: float) -> float:
        index = max(0, bisect.bisect_right(self._starts, float(session_time) + 1e-9) - 1)
        part = self.parts[index]
        offset = float(session_time) - part.session_start
        if index + 1 < len(self.parts):
            offset = min(offset, part.duration)  # inside a gap: the end of this part
        return part.local_start + offset
